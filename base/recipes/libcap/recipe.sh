# shellcheck shell=bash
# libcap — POSIX capabilities.
#
# A hard systemd dependency: CapabilityBoundingSet=, AmbientCapabilities= and
# most of systemd's privilege dropping are libcap calls.

RECIPE_SOURCE="libcap"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    cp -a "$SRCDIR/." "$BUILDDIR/"

    # libcap builds a code generator that must run on the *build* machine while
    # everything else is cross-compiled, so it needs both compilers named
    # separately — BUILD_CC for the generator, CC for the target.
    local args=(
        CC="$CC"
        BUILD_CC=gcc
        AR=llvm-ar
        RANLIB=llvm-ranlib
        OBJCOPY=llvm-objcopy
        prefix=/usr
        lib=lib
        SBINDIR=/usr/bin
        PAM_CAP=no        # no PAM in the base image
        GOLANG=no
    )

    make -C "$BUILDDIR" -j"$JOBS" "${args[@]}"
    make -C "$BUILDDIR" "${args[@]}" DESTDIR="$DESTDIR" install

    rm -f "$DESTDIR/usr/lib/libcap.a" "$DESTDIR/usr/lib/libpsx.a"
    chmod 755 "$DESTDIR"/usr/lib/libcap.so.* 2>/dev/null || true
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libcap.so.2" ] || { echo 'libcap: libcap.so.2 missing' >&2; return 1; }
}
