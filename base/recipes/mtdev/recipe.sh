# shellcheck shell=bash
# mtdev — translate the kernel's older multitouch protocol into the newer one.
#
# Present for one reason: libinput declares it unconditionally. Trinix links
# nothing else against it, and on the hardware Trinix runs on it will translate
# nothing, because every touch device made this decade speaks protocol B
# natively.

RECIPE_SOURCE="mtdev"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --disable-static

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    rm -f "$DESTDIR"/usr/lib/*.la
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libmtdev.so.1" ] || { echo 'mtdev: libmtdev.so.1 missing' >&2; return 1; }
}
