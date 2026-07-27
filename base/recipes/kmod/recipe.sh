# shellcheck shell=bash
# kmod — module loading, and the modprobe/insmod/lsmod tools.
#
# systemd-udevd loads kernel modules through libkmod, so this is on the boot
# path even though nothing in the base image types `modprobe`.
#
# Compression support matters here: Trinix installs modules uncompressed today,
# but zstd support costs nothing and the image will likely compress them later.

RECIPE_SOURCE="kmod"
RECIPE_DEPENDS="glibc-runtime zlib xz zstd"

trinix_build() {
    # kmod-34's tarball is internally inconsistent: build-aux/ltmain.sh is
    # Debian's libtool 2.5.4-4 while aclocal.m4's LT_INIT macros come from
    # 2.5.4.1. Every compile then dies with "libtool: Version mismatch error".
    # Re-extracting does not help — the tarball ships it that way — so the
    # autotools have to be regenerated against one consistent libtool.
    ( cd "$SRCDIR" && autoreconf -fi >/dev/null )

    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --bindir=/usr/bin \
        --sysconfdir=/etc \
        --disable-static \
        --disable-manpages \
        --with-zstd \
        --with-xz \
        --with-zlib \
        --without-openssl

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    rm -f "$DESTDIR"/usr/lib/*.la

    # The traditional module tool names are symlinks to kmod. systemd's
    # units and udev rules invoke `modprobe` by name.
    for tool in depmod insmod lsmod modinfo modprobe rmmod; do
        ln -sfn kmod "$DESTDIR/usr/bin/$tool"
    done
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libkmod.so.2" ] || { echo 'kmod: libkmod.so.2 missing' >&2; return 1; }
    [ -L "$DESTDIR/usr/bin/modprobe" ]     || { echo 'kmod: modprobe symlink missing' >&2; return 1; }
}
