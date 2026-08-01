# shellcheck shell=bash
# libffi — call a function whose signature was only known at runtime.
#
# Here for libwayland, which decodes each incoming protocol message using the
# argument list from the XML rather than a compiled-in prototype, and so has to
# build the call frame itself.

RECIPE_SOURCE="libffi"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --disable-static \
        --disable-multi-os-directory \
        --disable-docs

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    rm -f "$DESTDIR"/usr/lib/*.la
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libffi.so.8" ] || { echo 'libffi: libffi.so.8 missing' >&2; return 1; }
}
