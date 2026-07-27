# shellcheck shell=bash
# The C++ and unwinding runtime, plus the GCC compat libraries.
#
# Two distinct sets of libraries, shipped for two distinct reasons:
#
#   libc++ / libc++abi / libunwind  what everything Trinix compiles links against
#   libgcc_s / libstdc++           what Microsoft's official .NET binaries expect
#
# The second set is a compatibility surface, not a dependency: nothing built
# from this tree links against it. Keeping them in one recipe makes that
# relationship explicit and gives a single place to check when .NET fails to
# start with a missing-symbol error.

RECIPE_SOURCE=""          # synthetic: built in Phase 1, selected here
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    install -d "$DESTDIR/usr/lib"

    for lib in libc++ libc++abi libunwind; do
        for so in "$SYSROOT/usr/lib/$lib".so*; do
            [ -e "$so" ] || continue
            cp -P "$so" "$DESTDIR/usr/lib/"
        done
    done

    for lib in libgcc_s libstdc++ libatomic libgomp; do
        for so in "$SYSROOT/usr/lib/$lib".so*; do
            [ -e "$so" ] || continue
            cp -P "$so" "$DESTDIR/usr/lib/"
        done
    done

    # The .so development symlinks and libstdc++'s gdb pretty-printer script
    # are build-time artefacts; they have no business in a shipped image.
    rm -f "$DESTDIR"/usr/lib/*.so "$DESTDIR"/usr/lib/*-gdb.py
}

trinix_check() {
    local missing=''
    for required in libc++.so.1 libc++abi.so.1 libgcc_s.so.1 libstdc++.so.6; do
        [ -e "$DESTDIR/usr/lib/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "llvm-runtime: missing$missing" >&2; return 1; }
}
