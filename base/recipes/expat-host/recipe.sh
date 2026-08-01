# shellcheck shell=bash
# expat, for the build machine — wayland-scanner's XML parser.
#
# The scanner reads protocol XML, and expat is how. Debian has the library, and
# adding libexpat1-dev to the build container would be one line; it would also
# invalidate the host-tools image and with it the LLVM and toolchain images
# built on top, which is several hours to avoid writing this file.
#
# Static, and only static. A shared library installed under /usr/local/lib is
# found by the linker and then not found by ld.so, because nothing has run
# ldconfig — a failure that appears at the first invocation of the scanner
# rather than at the link that caused it. Nothing else on the build machine
# wants expat, so there is nothing to share.

RECIPE_SOURCE="expat"
RECIPE_DEPENDS=""
RECIPE_HOST_ONLY=1

trinix_build() {
    "$SRCDIR/configure" \
        --prefix=/usr/local \
        --libdir=/usr/local/lib \
        --enable-static \
        --disable-shared \
        --without-docbook \
        --without-examples \
        --without-tests

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    rm -f "$DESTDIR"/usr/local/lib/*.la
}

trinix_check() {
    [ -e "$DESTDIR/usr/local/lib/libexpat.a" ] \
        || { echo 'expat-host: no static library' >&2; return 1; }
    [ -e "$DESTDIR/usr/local/lib/pkgconfig/expat.pc" ] \
        || { echo 'expat-host: no pkg-config file — meson will not find it' >&2; return 1; }
}
