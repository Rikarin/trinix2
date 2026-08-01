# shellcheck shell=bash
# dosfstools — mkfs.vfat and fsck.vfat for the ESP.
#
# The ESP is FAT because UEFI says so. The image is built with mtools on the
# Mac, which never mounts anything, so this is for the running system: checking
# the ESP at boot, and rebuilding it during an A/B update when the bootloader
# itself changes.

RECIPE_SOURCE="dosfstools"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --sbindir=/usr/bin \
        --enable-compat-symlinks \
        `# The ESP holds a bootloader and a kernel, not filenames that need` \
        `# transcoding; iconv support would only add a codepage dependency.` \
        --without-iconv

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    rm -rf "$DESTDIR/usr/share/man" "$DESTDIR/usr/share/doc"
}

trinix_check() {
    local missing=''
    for required in mkfs.fat fsck.fat mkfs.vfat; do
        [ -e "$DESTDIR/usr/bin/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "dosfstools: missing$missing" >&2; return 1; }
}
