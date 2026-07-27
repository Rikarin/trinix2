# shellcheck shell=bash
# zlib — the first real compile in the base set.
#
# Chosen to go first because it is small, has no dependencies beyond libc, and
# uses a hand-written configure script rather than autotools. If the recipe
# contract works here it works for the easy half of the base set.

RECIPE_SOURCE="zlib"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    # The first real LLD-versus-GNU-ld difference in the tree, and a subtle one.
    #
    # zlib's configure probes for shared-library support by linking a *trivial*
    # test object against zlib's full zlib.map version script. GNU ld ignores
    # version-script entries naming symbols that do not exist; LLD has rejected
    # them since v17 (--no-undefined-version became the default). So the probe
    # fails, configure decides the compiler cannot build shared libraries, and
    # silently produces only libz.a — with the failure surfacing much later as a
    # missing libz.so.1 in the rootfs.
    #
    # Restoring the GNU behaviour for this link is the minimal fix. It is set
    # here rather than globally because rejecting undefined version-script
    # symbols is a genuinely useful diagnostic everywhere else.
    export LDSHARED="$CC -shared -Wl,-soname,libz.so.1 -Wl,--version-script,$SRCDIR/zlib.map -Wl,--undefined-version"

    # zlib's configure is bespoke: no --host, cross-compilation is driven purely
    # by CC pointing at a cross compiler.
    "$SRCDIR/configure" --prefix=/usr --libdir=/usr/lib

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    # Static archives are for the build container, not the image, and nothing in
    # the base set links zlib statically.
    rm -f "$DESTDIR/usr/lib/libz.a"
}

trinix_check() {
    # Named by soname, not by version: the point is that a *shared* library got
    # built at all, which is exactly what the LLD difference above breaks.
    [ -e "$DESTDIR/usr/lib/libz.so.1" ] \
        || { echo 'zlib: no shared library was built — check the version-script probe' >&2; return 1; }

    local machine
    machine="$("$READELF" --file-header "$DESTDIR/usr/lib/libz.so.1" | awk -F: '/Machine:/ {print $2}')"
    case "$TRINIX_ARCH:$machine" in
        arm64:*AArch64*|x86_64:*X86-64*) ;;
        *) echo "zlib: libz.so.1 is '$machine', wrong for $TRINIX_ARCH" >&2; return 1 ;;
    esac
}
