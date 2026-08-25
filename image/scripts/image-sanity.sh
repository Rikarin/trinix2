#!/usr/bin/env bash
# image-sanity.sh <arm64|x86_64> — check the disk image without booting it.
#
# The Phase 2 exit criterion is a boot, and a boot needs QEMU. What can be
# checked here is everything the firmware and the kernel look at before a
# single userspace process runs: is there a GPT, is there an ESP the firmware
# will recognise, is the bootloader on the path firmware actually searches, and
# does the loader entry name the partition that the root filesystem is really
# in. Each of those failures looks identical from a serial console — a machine
# that prints nothing — so each is worth catching here instead.

# shellcheck source=../../toolchain/scripts/trinix-toolchain-lib.sh
. /usr/local/lib/trinix/scripts/trinix-toolchain-lib.sh

trinix_set_arch "${1:?usage: image-sanity.sh <arm64|x86_64>}"

IMAGE="${TRINIX_OUT:-/out}/trinix-$TRINIX_ARCH.img"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

failures=0
fail() { printf '  \033[1;31mFAIL\033[0m %s\n' "$*"; failures=$((failures + 1)); }
pass() { printf '  \033[1;32mok\033[0m   %s\n' "$*"; }

case "$TRINIX_ARCH" in
    arm64)  efi_fallback='BOOTAA64.EFI' ;;
    x86_64) efi_fallback='BOOTX64.EFI'  ;;
esac

log "Disk image sanity — $TRINIX_ARCH ($IMAGE)"
[ -f "$IMAGE" ] || die "no image at $IMAGE"

# --- Partition table -------------------------------------------------------
log 'Partition table'
if sgdisk --verify "$IMAGE" | grep -q 'No problems found'; then
    pass 'GPT verifies (primary and backup headers agree)'
else
    fail 'sgdisk reports problems with the partition table'
fi

# sgdisk -i prints one partition's attributes; parsing it is how the rest of
# this script learns where each partition actually starts.
part_field() {
    sgdisk --info="$1" "$IMAGE" | awk -F': *' -v key="$2" '$1 ~ key { print $2; exit }'
}

expected_names=(trinix-esp trinix-root-a trinix-root-b trinix-data)
for index in 1 2 3 4; do
    name="$(part_field "$index" 'Partition name')"
    name="${name//\'/}"
    if [ "$name" = "${expected_names[$((index - 1))]}" ]; then
        pass "partition $index is $name"
    else
        fail "partition $index is '$name', expected ${expected_names[$((index - 1))]}"
    fi
done

esp_type="$(part_field 1 'Partition GUID code')"
case "$esp_type" in
    C12A7328-F81F-11D2-BA4B-00A0C93EC93B*) pass 'partition 1 has the EFI System type GUID' ;;
    *) fail "partition 1 type is '$esp_type' — firmware will not look for a bootloader on it" ;;
esac

# Slot B must be the same size as slot A, or the first A/B update has nowhere
# to go. This is cheap to assert now and expensive to discover in Phase 7.
size_a="$(part_field 2 'Partition size')"
size_b="$(part_field 3 'Partition size')"
if [ "$size_a" = "$size_b" ]; then
    pass "both root slots are ${size_a}"
else
    fail "slot A is $size_a but slot B is $size_b — an update could not fit"
fi

# --- The ESP ---------------------------------------------------------------
# mtools addresses a partition inside a disk image with @@<offset>, which is
# what makes this checkable without a loop device.
log 'EFI system partition'
esp_start_sector="$(part_field 1 'First sector' | awk '{print $1}')"
esp_at="$IMAGE@@$((esp_start_sector * 512))"
export MTOOLS_SKIP_CHECK=1

esp_listing="$WORK/esp.txt"
if mdir -i "$esp_at" -b -/ :: > "$esp_listing" 2>/dev/null; then
    pass 'the ESP is a readable FAT filesystem'
else
    fail 'the ESP could not be read as FAT'
fi

for required in "::/EFI/BOOT/$efi_fallback" '::/loader/loader.conf' \
                '::/loader/entries/trinix-a.conf' '::/trinix/a/vmlinuz'; do
    if grep -qiF "$required" "$esp_listing"; then
        pass "${required#::}"
    else
        fail "${required#::} is missing from the ESP"
    fi
done

# --- The loader entry points at the partition that exists ------------------
log 'Loader entry'
mtype -i "$esp_at" '::/loader/entries/trinix-a.conf' > "$WORK/entry.conf" 2>/dev/null || true
entry_root="$(awk '/^options/ { for (i = 2; i <= NF; i++) if ($i ~ /^root=PARTUUID=/) { sub(/^root=PARTUUID=/, "", $i); print tolower($i) } }' "$WORK/entry.conf")"
root_a_guid="$(part_field 2 'Partition unique GUID' | tr 'A-Z' 'a-z')"

if [ -n "$entry_root" ] && [ "$entry_root" = "$root_a_guid" ]; then
    pass "root=PARTUUID=$entry_root matches slot A"
else
    fail "the loader entry boots root=PARTUUID='$entry_root' but slot A is '$root_a_guid'"
fi

grep -q "^linux[[:space:]]" "$WORK/entry.conf" && pass 'the entry names a kernel' \
    || fail 'the entry has no linux line'

# --- The root filesystem ---------------------------------------------------
# Only the superblock is extracted: enough for dumpe2fs to confirm this is the
# filesystem the loader entry thinks it is, without copying half a gigabyte.
log 'Root filesystem'
root_start_sector="$(part_field 2 'First sector' | awk '{print $1}')"
dd if="$IMAGE" of="$WORK/root-super.img" bs=512 \
   skip="$root_start_sector" count=16384 status=none

super="$(dumpe2fs -h "$WORK/root-super.img" 2>/dev/null || true)"
if printf '%s\n' "$super" | grep -q 'Filesystem volume name:.*trinix-root-a'; then
    pass 'slot A carries an ext4 filesystem labelled trinix-root-a'
else
    fail 'slot A has no recognisable ext4 superblock'
fi

fs_uuid="$(printf '%s\n' "$super" | awk -F': *' '/Filesystem UUID/ {print tolower($2)}')"
[ "$fs_uuid" = "$root_a_guid" ] \
    && pass 'the filesystem UUID matches the partition GUID' \
    || fail "filesystem UUID $fs_uuid does not match partition GUID $root_a_guid"

# --- /data, and its subvolumes ---------------------------------------------
# Worth checking here rather than trusting build-image.sh, because the
# subvolume layout is the one thing in the image that no boot test exercises:
# a /data with the right label and no subvolumes mounts, boots, logs in, and
# then turns out to have nowhere for docs/plan/10's Rewind to snapshot.
#
# Unlike the root slot this needs the whole partition, not just a superblock —
# the root tree can be anywhere the allocator put it. conv=sparse keeps the
# copy cheap: almost all of a fresh /data is holes.
log 'Data filesystem'
data_start_sector="$(part_field 4 'First sector' | awk '{print $1}')"
data_end_sector="$(part_field 4 'Last sector' | awk '{print $1}')"
dd if="$IMAGE" of="$WORK/data.img" bs=512 \
   skip="$data_start_sector" count=$(( data_end_sector - data_start_sector + 1 )) \
   conv=sparse status=none

data_super="$(btrfs inspect-internal dump-super "$WORK/data.img" 2>/dev/null || true)"
if printf '%s\n' "$data_super" | grep -qE '^label[[:space:]]+trinix-data$'; then
    pass '/data carries a Btrfs filesystem labelled trinix-data'
else
    fail '/data has no recognisable Btrfs superblock — is it still ext4?'
fi

data_guid="$(part_field 4 'Partition unique GUID' | tr 'A-Z' 'a-z')"
data_fsid="$(printf '%s\n' "$data_super" | awk '/^fsid/ {print tolower($2)}')"
[ "$data_fsid" = "$data_guid" ] \
    && pass 'the Btrfs fsid matches the partition GUID' \
    || fail "Btrfs fsid $data_fsid does not match partition GUID $data_guid"

# block-group-tree is compat_ro bit 3. Asserted because it is the one on-disk
# feature the image sets by hand — precisely so that a /data written later by
# the installer or by Recovery, with a newer mkfs.btrfs, agrees with this one.
compat_ro="$(printf '%s\n' "$data_super" | awk '/^compat_ro_flags/ {print $2}')"
if [ -n "$compat_ro" ] && (( compat_ro & 0x8 )); then
    pass "block-group-tree is on (compat_ro $compat_ro)"
else
    fail "compat_ro is '$compat_ro' — block-group-tree is missing, so this /data does not match one made by the installer"
fi

# The subvolumes themselves, read out of the root tree by name. This is the
# assertion docs/plan/10 § Rewind actually depends on.
subvols="$(btrfs inspect-internal dump-tree -t root "$WORK/data.img" 2>/dev/null \
           | awk '/root ref key/ { print $NF }' | sort -u)"
for required in '@home' '@apps' '@var' '@containers' '.snapshots'; do
    if printf '%s\n' "$subvols" | grep -qxF "$required"; then
        pass "subvolume $required"
    else
        fail "subvolume $required is missing from /data"
    fi
done

echo
[ "$failures" -eq 0 ] || die "$failures image check(s) failed for $TRINIX_ARCH"
log "Disk image sanity passed for $TRINIX_ARCH"
