#!/usr/bin/env bash
# build-image.sh <arm64|x86_64> — assemble a bootable Trinix disk image.
#
# Produces $TRINIX_OUT/trinix-<arch>.img: a GPT disk with an ESP holding
# systemd-boot and the kernel, two root slots, and one writable /data.
#
#   1  trinix-esp     FAT32   systemd-boot, loader entries, kernels
#   2  trinix-root-a  ext4    the base system, mounted read-only
#   3  trinix-root-b  ext4    empty — the target of the first A/B update
#   4  trinix-data    btrfs   @home, @apps, @var, @containers
#
# Nothing here mounts anything. Docker containers cannot attach loop devices
# without privileges, and requiring --privileged for a build would undo the
# "Docker Desktop is the whole list" promise in the README. So the filesystems
# are populated by their own userspace tools — mke2fs -d for ext4,
# mkfs.btrfs --rootdir for Btrfs, mtools for FAT — and the partition images are
# written into the disk at offsets this script computes itself.
#
# That last one is why the /data switch to Btrfs was possible at all without
# giving the build container privileges: mkfs.btrfs grew --rootdir long ago,
# and since 6.11 it grew --subvol, so a subvolume layout can be built from a
# staging directory exactly the way mke2fs -d builds an ext4 one.

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

# ⚠ 2 GiB, up from the 1 GiB the ext4 /data had, and the extra gigabyte is not
# for content. Btrfs allocates chunks up front — a fresh 1 GiB filesystem here
# comes out with ~126 MiB already allocated, and metadata is DUP on a single
# device so every tree block is written twice — and, more to the point, a Btrfs
# that is nearly full behaves badly in ways ext4 does not: the allocator can
# fail a *delete* because removing a file needs to write metadata first.
# docs/plan/18 R8 flags exactly this, and docs/plan/10's snapshot budget is the
# runtime half of the answer. The image's half is not to ship a filesystem that
# starts out at the size where the trouble begins.
#
# It costs nothing on disk: the partition is written with `dd conv=sparse` and
# the unused gigabyte is zeros. On real hardware the installer resizes /data to
# the disk anyway — `btrfs filesystem resize max`, online, which unlike ext4
# needs no unmount and no separate grow-on-first-boot dance.
DATA_SIZE_MIB=2048
ALIGN_MIB=1

# The mount options every /data subvolume gets. Defined here because this script
# both asserts that /etc/fstab agrees with them and prints the block it wants
# when it does not; the full argument for each one is in image/README.md
# § Mount options, and the short version is: compress because doc 10 asked for
# it, noatime because on a snapshotted filesystem reading a file would otherwise
# write one, and discard=async because nothing else is going to trim this disk.
DATA_MOUNT_OPTS='rw,noatime,compress=zstd:1,discard=async'

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

# ---------------------------------------------------------------------------
# ⚠ The half this script cannot write, checked before it does any work.
#
# /etc/fstab lives in the base image (base/recipes/trinix-system), and until it
# is updated it still says `ext4`. A filesystem type in fstab is not advisory —
# mount will not guess and will not fall back — so an image assembled by this
# script and booted against the old fstab fails to mount /data, which means no
# /var, which means no boot. That failure reads as a page of systemd dependency
# errors on a serial console, so it is caught here instead, where the message
# can say what to paste and where.
# ---------------------------------------------------------------------------
if ! grep -qE '^[^#][^[:space:]]*[[:space:]]+/data[[:space:]]+btrfs[[:space:]]' "$ROOTFS/etc/fstab"; then
    die "the rootfs /etc/fstab does not mount /data as btrfs, and /data is Btrfs now.
     base/recipes/trinix-system needs this block — the reasoning for every
     option is in image/README.md § Mount options:

       PARTLABEL=trinix-data /data              btrfs $DATA_MOUNT_OPTS,subvolid=5           0 0
       PARTLABEL=trinix-data /data/home         btrfs $DATA_MOUNT_OPTS,subvol=/@home        0 0
       PARTLABEL=trinix-data /data/Applications btrfs $DATA_MOUNT_OPTS,subvol=/@apps        0 0
       PARTLABEL=trinix-data /data/var          btrfs $DATA_MOUNT_OPTS,subvol=/@var         0 0
       PARTLABEL=trinix-data /data/containers   btrfs $DATA_MOUNT_OPTS,subvol=/@containers  0 0"
fi

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
# /data — the writable half, and the one Btrfs filesystem in the image.
#
# Seeded here rather than left to systemd-tmpfiles on first boot, for two
# reasons. The directories: /var is a symlink into here, and a symlink whose
# target does not exist yet turns a boot failure into a puzzle. The contents:
# anything a recipe installed into /var — systemd's and dbus's state
# directories — was relocated by the base driver into /usr/share/factory/data,
# and this is where it comes back.
#
# The layout, per docs/plan/10 § Rewind. Four subvolumes, one per thing that
# should be restorable on its own, plus somewhere to keep the snapshots:
#
#   top level (subvolid 5)        mounted at /data — mount points, and the
#                                 small directories nothing snapshots
#   ├── @home        → /data/home          ← /home is a symlink to this
#   ├── @apps        → /data/Applications  ← /Applications is a symlink to this
#   ├── @var         → /data/var           ← /var is a symlink to this
#   ├── @containers  → /data/containers    ← ~/Library/Containers points here
#   ├── .snapshots                          Rewind's snapshot store
#   └── root, srv, opt                      plain directories, not subvolumes
#
# Why the split is exactly this: a snapshot in Btrfs is per-subvolume, so the
# subvolume boundaries *are* the restore boundaries. Rolling @home back an hour
# must not uninstall an application, which puts /Applications on its own; and
# /var churns constantly — journal, caches, dbus state — so leaving it inside
# @home would mean every hourly home snapshot pinned an hour of log writes.
#
# ⚠ ~/Library/Containers is the awkward one, because docs/plan/01 and
# docs/plan/04 put it *inside* the home directory while doc 10 wants it as a
# peer subvolume. It is resolved with a symlink rather than by nesting: the
# documented path stays literally what those documents say, and a `btrfs send`
# of @home carries the symlink instead of a few gigabytes of app state. The
# alternative — a nested subvolume under @home — appears as an empty directory
# inside any snapshot of its parent, which is a genuinely confusing thing to
# meet while restoring.
#
# root/srv/opt stay plain directories in the top level. /root is a home
# directory in name only here (root's shell is reachable from the rescue entry
# and nothing else), srv and opt are empty, and the top level is precisely the
# thing that is never rolled back — which is what you want of the volume that
# contains the snapshots.
# ---------------------------------------------------------------------------
step 'building the data filesystem'
data="$WORK/data"

# The staging tree carries both halves of the layout: the @-prefixed directories
# that mkfs turns into subvolumes, and the bare mount points they get mounted
# over once /etc/fstab has its say. Both have to exist — a subvolume mounted at
# /data/home needs /data/home to be a directory in the top level first.
mkdir -p "$data"/{@home,@apps,@var,@containers,.snapshots}
mkdir -p "$data"/{home,Applications,var,containers}
mkdir -p "$data"/{srv,opt,root}
chmod 700 "$data/root"

# Where each relocated directory lands. build-base.sh moves /var, /home, /root,
# /srv, /opt and /Applications into the factory image because recipes install
# into them; this is the only place that decides which subvolume each one is
# part of, so an unmapped one is a decision nobody has made rather than
# something to quietly drop on the floor.
factory="$ROOTFS/usr/share/factory/data"
declare -A subvol_of=(
    [var]='@var'
    [home]='@home'
    [Applications]='@apps'
    [root]='root'
    [srv]='srv'
    [opt]='opt'
)

if [ -d "$factory" ]; then
    for path in "$factory"/*/; do
        [ -d "$path" ] || continue
        name="$(basename "$path")"
        target="${subvol_of[$name]:-}"
        [ -n "$target" ] \
            || die "the factory image has /$name, which no subvolume claims — map it in build-image.sh"
        mkdir -p "$data/$target"
        rsync -a "$path" "$data/$target/"
    done
    step "seeded /data from the factory image ($(du -sh "$factory" | cut -f1))"
fi

# One container root per baked-in user, and the symlink that makes
# ~/Library/Containers reach it. The users are decided at build time by the
# trinix-system recipe, so the home directories that exist here are the whole
# list; deriving it from them rather than hard-coding a name means adding a
# second user does not silently leave them without a container root.
for home in "$data"/@home/*/; do
    [ -d "$home" ] || continue
    user="$(basename "$home")"
    owner="$(stat -c '%u:%g' "$home")"
    mkdir -p "$data/@containers/$user" "$home/Library"
    chown "$owner" "$data/@containers/$user" "$home/Library"
    ln -sfn "/data/containers/$user" "$home/Library/Containers"
    chown -h "$owner" "$home/Library/Containers"
    step "container root for $user"
done

data_img="$WORK/data.img"
truncate -s "${DATA_SIZE_MIB}M" "$data_img"

# ⚠ The `-O verity` that used to be here is gone, and nothing replaces it.
# ext4 keeps fs-verity as a superblock feature that must be set at mkfs time and
# can never be added afterwards, which is why it had to be argued for on a line
# of its own; Btrfs stores the Merkle tree as ordinary filesystem items and sets
# its own compat_ro bit the first time a file is sealed. Installing an
# application still seals its files — see docs/app-bundles.md — it just no
# longer depends on a decision made here.
#
# --device-uuid alongside -U for the same reason the ext4 hash seed was pinned:
# mkfs seeds it from urandom otherwise, and two builds of the same tree should
# differ as little as possible. ⚠ They will still differ: mkfs.btrfs stamps
# otime/ctime on the root items from the wall clock and honours no
# SOURCE_DATE_EPOCH, so unlike the ext4 /data this partition — and therefore the
# image's sha256 — is not bit-identical between two builds. Recorded here rather
# than discovered later; closing it needs an upstream change, not a flag.
#
# -O block-group-tree is set explicitly rather than left to the default because
# the two mkfs.btrfs binaries involved disagree about it: the build container's
# is Debian's, the one a running Trinix has is base/recipes/btrfs-progs, and
# upstream made bgt a default in 7.x. Without this line a /data made by the
# installer or by Recovery would have a different on-disk format from a /data
# that came out of this script — the kind of difference that shows up as a
# mount-time performance change nobody can account for. It needs kernel 6.1 to
# mount; the pinned kernel is 6.12.
#
# --compress zstd:1 matches what /data is mounted with, so the seeded /var
# content is already compressed rather than waiting to be rewritten. The
# argument for the level is in image/README.md § Mount options.
mkfs.btrfs --quiet \
       --label trinix-data \
       --uuid "$DATA_UUID" \
       --device-uuid "$DATA_UUID" \
       --features block-group-tree \
       --checksum crc32c \
       --nodiscard \
       --compress zstd:1 \
       --rootdir "$data" \
       --subvol rw:@home \
       --subvol rw:@apps \
       --subvol rw:@var \
       --subvol rw:@containers \
       --subvol rw:.snapshots \
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
