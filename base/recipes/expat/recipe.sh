# shellcheck shell=bash
# expat — XML parsing, needed only because D-Bus parses its configuration and
# service files as XML.

RECIPE_SOURCE="expat"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --disable-static \
        --without-docbook \
        --without-examples \
        --without-tests

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    rm -f "$DESTDIR"/usr/lib/*.la
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libexpat.so.1" ] || { echo 'expat: libexpat.so.1 missing' >&2; return 1; }
}
