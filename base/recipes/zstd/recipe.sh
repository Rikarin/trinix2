# shellcheck shell=bash
# zstd / libzstd.
#
# The compression the rest of Trinix defaults to: initramfs, kernel modules,
# journal records, and later the .tdi app images. Chosen over xz for
# decompression speed, which is what boot time actually cares about.

RECIPE_SOURCE="zstd"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    # zstd's Makefiles have no out-of-tree support, and the source tree is
    # shared between both architectures — so build from a copy rather than
    # letting arm64 and x86_64 write objects over each other.
    cp -a "$SRCDIR/." "$BUILDDIR/"

    # Only the library and the CLI; the tests, contrib and legacy format
    # support are all build time the image never benefits from.
    make -C "$BUILDDIR/lib" -j"$JOBS" libzstd
    make -C "$BUILDDIR/programs" -j"$JOBS" zstd

    make -C "$BUILDDIR/lib" DESTDIR="$DESTDIR" PREFIX=/usr LIBDIR=/usr/lib install
    make -C "$BUILDDIR/programs" DESTDIR="$DESTDIR" PREFIX=/usr install

    rm -f "$DESTDIR/usr/lib/libzstd.a"
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libzstd.so.1" ] || { echo 'zstd: libzstd.so.1 missing' >&2; return 1; }
}
