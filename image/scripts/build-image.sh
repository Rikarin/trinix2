#!/usr/bin/env bash
# build-image.sh <arm64|x86_64> — assemble a bootable Trinix disk image.
#
# Produces $TRINIX_OUT/trinix-<arch>.img: a GPT disk with an ESP holding
# systemd-boot and the kernel, two root slots, and one writable /data.
#
#   1  trinix-esp     FAT32   systemd-boot, loader entries, kernels
#   2  trinix-root-a  ext4    the base system, mounted read-only
#   3  trinix-root-b  ext4    empty — the target of the first A/B update
#   4  trinix-data    ext4    /var, /home, /root, /Applications
#
# Nothing here mounts anything. Docker containers cannot attach loop devices
# without privileges, and requiring --privileged for a build would undo the
# "Docker Desktop is the whole list" promise in the README. So the filesystems
# are populated by their own userspace tools — mke2fs -d for ext4, mtools for
# FAT — and the partition images are written into the disk at offsets this
# script computes itself.

# shellcheck source=../../toolchain/scripts/trinix-toolchain-lib.sh
. /usr/local/lib/trinix/scripts/trinix-toolchain-lib.sh

trinix_set_arch "${1:?usage: build-image.sh <arm64|x86_64>}"

ROOTFS="${TRINIX_ROOTFS:-/opt/trinix/rootfs}/$TARGET_TRIPLE"
OUT="${TRINIX_OUT:-/out}"
WORK="${TRINIX_BUILD:-/build}/image/$TRINIX_ARCH"
IMAGE="$OUT/trinix-$TRINIX_ARCH.img"

# ---------------------------------------------------------------------------
# Identifiers.
#
# Fixed rather than generated, because two builds of the same source tree must
# produce the same image — and because the loader entry has to name the root
# partition by an ID that exists before the image does. They spell TRNX and
# then the slot, which makes a partition table readable at a glance.
# ---------------------------------------------------------------------------
DISK_UUID='54524e58-0000-4000-a000-000000000000'
ESP_UUID='54524e58-0000-4000-a000-000000000001'
ROOT_A_UUID='54524e58-0000-4000-a000-00000000000a'
ROOT_B_UUID='54524e58-0000-4000-a000-00000000000b'
DATA_UUID='54524e58-0000-4000-a000-0000000000da'

TYPE_ESP='c12a7328-f81f-11d2-ba4b-00a0c93ec93b'
# Deliberately the generic Linux filesystem type rather than the architecture's
# discoverable-root GUID: two partitions carrying the root GUID make
# systemd-gpt-auto-generator ambiguous about which is the real root, and Trinix
# identifies its slots by partition label anyway (see image/README.md).
TYPE_LINUX='0fc63daf-8483-4772-8e79-3d69d8477de4'

ESP_SIZE_MIB=256
DATA_SIZE_MIB=1024
ALIGN_MIB=1

case "$TRINIX_ARCH" in
    arm64)
        efi_stub='systemd-bootaa64.efi'; efi_fallback='BOOTAA64.EFI'
        kernel_image='Image'
        # QEMU's arm64 `virt` machine puts its console on the PL011 at ttyAMA0.
        #
        # The serial console is listed *last* deliberately. The kernel prints to
        # every console= it is given, but /dev/console — and therefore where
        # systemd's getty generator puts the login prompt — is the last one.
        # Reverse these two and the boot messages still scroll past on the
        # serial line while the prompt appears on a virtual terminal that
        # `-display none` means nobody will ever see.
        console_args='console=tty0 console=ttyAMA0,115200'
        ;;
    x86_64)
        efi_stub='systemd-bootx64.efi'; efi_fallback='BOOTX64.EFI'
        kernel_image='bzImage'
        console_args='console=tty0 console=ttyS0,115200'
        ;;
esac

[ -d "$ROOTFS" ] || die "no rootfs at $ROOTFS — build the base stage first"

kernel_release="$(cat "$ROOTFS/usr/lib/trinix/kernel-release" 2>/dev/null)" \
    || die 'the rootfs has no kernel — check the linux recipe'
version="$(awk -F= '/^VERSION_ID=/ {print $2}' "$ROOTFS/usr/lib/os-release")"

rm -rf "$WORK"
mkdir -p "$WORK" "$OUT"

log "Assembling trinix-$TRINIX_ARCH.img (kernel $kernel_release, Trinix $version)"

# ---------------------------------------------------------------------------
# The ESP.
# ---------------------------------------------------------------------------
step 'staging the EFI system partition'
esp="$WORK/esp"
mkdir -p "$esp/EFI/BOOT" "$esp/EFI/systemd" "$esp/loader/entries" "$esp/trinix/a"

boot_efi="$ROOTFS/usr/lib/systemd/boot/efi/$efi_stub"
[ -f "$boot_efi" ] || die "systemd-boot was not built: $boot_efi is missing"

# EFI/BOOT/BOOT<arch>.EFI is the removable-media fallback path. Firmware boots
# it without an NVRAM entry, which is exactly the situation in a fresh QEMU VM,
# so it is the copy that actually gets used today; the EFI/systemd one is what
# `bootctl install` would maintain on real hardware.
install -m644 "$boot_efi" "$esp/EFI/BOOT/$efi_fallback"
install -m644 "$boot_efi" "$esp/EFI/systemd/$efi_stub"
install -m644 "$ROOTFS/usr/lib/trinix/vmlinuz" "$esp/trinix/a/vmlinuz"

cat > "$esp/loader/loader.conf" <<EOF
default  trinix-a.conf
timeout  3
console-mode keep
editor   yes
EOF

# Only slot A has an entry. Slot B is an empty partition until the first update
# populates it, and a menu entry pointing at an empty partition is a boot
# failure waiting for someone to press a key. Phase 7's updater writes
# trinix-b.conf — with systemd-boot's boot-attempt counters in the filename —
# at the same time it writes the slot.
cat > "$esp/loader/entries/trinix-a.conf" <<EOF
title      Trinix $version (slot A)
version    $kernel_release
linux      /trinix/a/vmlinuz
options    root=PARTUUID=$ROOT_A_UUID ro rootfstype=ext4 $console_args systemd.show_status=yes
EOF

# The rescue entry. PowerShell is the login shell, which means the interactive
# path now depends on a .NET runtime starting correctly — so there has to be a
# way in that does not. systemd's rescue target runs sulogin, which execs
# root's shell, and root's shell is deliberately bash (see the trinix-system
# recipe). Nothing here is Trinix-specific except the decision to put it in the
# boot menu rather than expecting someone to know the incantation.
cat > "$esp/loader/entries/trinix-a-rescue.conf" <<EOF
title      Trinix $version (slot A, rescue shell)
version    $kernel_release
linux      /trinix/a/vmlinuz
options    root=PARTUUID=$ROOT_A_UUID ro rootfstype=ext4 $console_args systemd.unit=rescue.target
EOF

esp_img="$WORK/esp.img"
truncate -s "${ESP_SIZE_MIB}M" "$esp_img"
mkfs.vfat -F 32 -n TRINIX-ESP "$esp_img" >/dev/null

# mtools refuses to touch an image whose geometry it cannot recognise unless
# told the check is not worth failing over; a plain file has no geometry.
export MTOOLS_SKIP_CHECK=1
( cd "$esp" && mcopy -i "$esp_img" -s -Q ./* :: )

# ---------------------------------------------------------------------------
# The root slots.
#
# Sized from the content plus a quarter, so that an A/B update whose image grew
# a little still fits in the slot laid out by this one — the partition table is
# fixed at install time and every future update has to live inside it.
# ---------------------------------------------------------------------------
step 'building the root filesystem'
rootfs_mib="$(du -sm --apparent-size "$ROOTFS" | cut -f1)"
root_size_mib=$(( rootfs_mib * 5 / 4 + 128 ))
root_size_mib=$(( (root_size_mib + 63) / 64 * 64 ))   # round up to 64 MiB

root_img="$WORK/root-a.img"
truncate -s "${root_size_mib}M" "$root_img"

# -d populates from a directory, which is the whole reason this works without
# root privileges over a loop device. -U and -E hash_seed keep two builds of
# the same tree byte-identical; without them mke2fs seeds both from urandom.
mke2fs -q -t ext4 \
       -L trinix-root-a \
       -U "$ROOT_A_UUID" \
       -E "hash_seed=$ROOT_A_UUID,root_owner=0:0" \
       -b 4096 -m 0 \
       -d "$ROOTFS" \
       "$root_img"

step "root filesystem: ${rootfs_mib} MiB of content in a ${root_size_mib} MiB slot"

# ---------------------------------------------------------------------------
# /data — the writable half.
#
# Seeded here rather than left to systemd-tmpfiles on first boot, for two
# reasons. The directories: /var is a symlink into here, and a symlink whose
# target does not exist yet turns a boot failure into a puzzle. The contents:
# anything a recipe installed into /var — systemd's and dbus's state
# directories — was relocated by the base driver into /usr/share/factory/data,
# and this is where it comes back.
# ---------------------------------------------------------------------------
step 'building the data filesystem'
data="$WORK/data"
mkdir -p "$data"/{var,home,srv,opt,Applications}
mkdir -p "$data/root"
chmod 700 "$data/root"

factory="$ROOTFS/usr/share/factory/data"
if [ -d "$factory" ]; then
    rsync -a "$factory/" "$data/"
    step "seeded /data from the factory image ($(du -sh "$factory" | cut -f1))"
fi

data_img="$WORK/data.img"
truncate -s "${DATA_SIZE_MIB}M" "$data_img"
# -O verity, because /Applications is here. Installing an application asks the
# kernel to seal each of its files with fs-verity, which turns "this bundle
# verified a moment ago" into "this bundle cannot be modified" — and the feature
# has to be present in the superblock, which is decided here and nowhere else.
# Without it the installer still works and says why it could not seal anything,
# which is a considerably more confusing thing to read than this line is to add.
mke2fs -q -t ext4 \
       -O verity \
       -L trinix-data \
       -U "$DATA_UUID" \
       -E "hash_seed=$DATA_UUID,root_owner=0:0" \
       -b 4096 -m 1 \
       -d "$data" \
       "$data_img"

# ---------------------------------------------------------------------------
# The disk.
# ---------------------------------------------------------------------------
step 'writing the partition table'

esp_start=$ALIGN_MIB
root_a_start=$(( esp_start + ESP_SIZE_MIB ))
root_b_start=$(( root_a_start + root_size_mib ))
data_start=$(( root_b_start + root_size_mib ))
total_mib=$(( data_start + DATA_SIZE_MIB + ALIGN_MIB ))

rm -f "$IMAGE"
truncate -s "${total_mib}M" "$IMAGE"

sgdisk --clear --disk-guid="$DISK_UUID" \
       --new="1:${esp_start}M:+${ESP_SIZE_MIB}M"   --typecode=1:"$TYPE_ESP"   --partition-guid=1:"$ESP_UUID"    --change-name=1:trinix-esp \
       --new="2:${root_a_start}M:+${root_size_mib}M" --typecode=2:"$TYPE_LINUX" --partition-guid=2:"$ROOT_A_UUID" --change-name=2:trinix-root-a \
       --new="3:${root_b_start}M:+${root_size_mib}M" --typecode=3:"$TYPE_LINUX" --partition-guid=3:"$ROOT_B_UUID" --change-name=3:trinix-root-b \
       --new="4:${data_start}M:+${DATA_SIZE_MIB}M"   --typecode=4:"$TYPE_LINUX" --partition-guid=4:"$DATA_UUID"   --change-name=4:trinix-data \
       "$IMAGE" >/dev/null

# conv=notrunc, because dd would otherwise cut the image off at the end of what
# it just wrote; conv=sparse so the empty slot B costs nothing on disk.
step 'writing partition contents'
dd if="$esp_img"  of="$IMAGE" bs=1M seek="$esp_start"    conv=notrunc,sparse status=none
dd if="$root_img" of="$IMAGE" bs=1M seek="$root_a_start" conv=notrunc,sparse status=none
dd if="$data_img" of="$IMAGE" bs=1M seek="$data_start"   conv=notrunc,sparse status=none
# Slot B is left as zeros. An empty partition is not a filesystem, which is why
# it has no loader entry.

sha256sum "$IMAGE" | cut -d' ' -f1 > "$IMAGE.sha256"

log "Image ready: $IMAGE"
printf '  %-14s %s\n' 'apparent size' "$(du -h --apparent-size "$IMAGE" | cut -f1)"
printf '  %-14s %s\n' 'on disk'       "$(du -h "$IMAGE" | cut -f1)"
printf '  %-14s %s\n' 'sha256'        "$(cat "$IMAGE.sha256")"
echo
sgdisk --print "$IMAGE" | tail -n +2
