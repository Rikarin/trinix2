# shellcheck shell=bash
# xz / liblzma — compression, and a systemd dependency.
#
# systemd links liblzma for compressed journal records and for reading
# xz-compressed kernel modules.

RECIPE_SOURCE="xz"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --disable-static \
        --disable-doc \
        --disable-nls

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    # libtool archives describe host paths and confuse cross-linking later.
    rm -f "$DESTDIR"/usr/lib/*.la
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/liblzma.so.5" ] || { echo 'xz: liblzma.so.5 missing' >&2; return 1; }
}
