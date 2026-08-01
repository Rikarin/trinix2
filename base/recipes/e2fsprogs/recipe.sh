# shellcheck shell=bash
# e2fsprogs — mke2fs and fsck for ext4.
#
# The A/B root slots are read-only images built on the Mac, so this is not for
# them: it is for `/data`, the one writable partition, which is created on
# first boot at whatever size the disk actually has and checked on every boot
# after that.
#
# Built against util-linux's libblkid/libuuid rather than its own copies. Two
# implementations of libuuid in one image, differing by a few years, is the
# kind of thing that works until the day it does not.

RECIPE_SOURCE="e2fsprogs"
RECIPE_DEPENDS="glibc-runtime util-linux"

trinix_build() {
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --bindir=/usr/bin \
        --sbindir=/usr/bin \
        --sysconfdir=/etc \
        --enable-elf-shlibs \
        --disable-static \
        --disable-nls \
        --disable-libblkid \
        --disable-libuuid \
        --disable-uuidd \
        --disable-fsck \
        --disable-fuse2fs \
        --disable-e2initrd-helper

    make -j"$JOBS"

    # e2fsprogs still distinguishes "root" binaries — the ones that had to work
    # before /usr was mounted — from the rest, and installs them into /sbin
    # and /lib regardless of --sbindir. On a merged-/usr system there is no
    # such distinction left, so the root prefix is pointed at the same place as
    # everything else.
    local dirs=(root_sbindir=/usr/bin root_bindir=/usr/bin root_libdir=/usr/lib)
    make DESTDIR="$DESTDIR" "${dirs[@]}" install
    make DESTDIR="$DESTDIR" "${dirs[@]}" install-libs

    rm -f "$DESTDIR"/usr/lib/*.a "$DESTDIR"/usr/lib/*.la
}

trinix_check() {
    local missing=''
    for required in mke2fs e2fsck resize2fs tune2fs dumpe2fs; do
        [ -x "$DESTDIR/usr/bin/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "e2fsprogs: missing$missing" >&2; return 1; }

    # mkfs.ext4 is a symlink to mke2fs, and it is the name systemd's
    # x-systemd.makefs and the first-boot /data provisioning both invoke.
    [ -e "$DESTDIR/usr/bin/mkfs.ext4" ] \
        || { echo 'e2fsprogs: mkfs.ext4 missing — /data could not be created on first boot' >&2; return 1; }

    # Its own libuuid would shadow util-linux's at runtime.
    [ ! -e "$DESTDIR/usr/lib/libuuid.so.1" ] \
        || { echo 'e2fsprogs: built its own libuuid, which collides with util-linux' >&2; return 1; }
}
