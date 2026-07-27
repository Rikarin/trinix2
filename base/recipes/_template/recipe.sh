# shellcheck shell=bash
# Template recipe. Copy the directory, rename it, delete what you do not need.
#
# Sourced by the stage driver: define variables and functions only, run nothing.
# See ../README.md for the full contract and the available environment.

RECIPE_SOURCE="example"        # key in base/sources.json
RECIPE_DEPENDS="glibc"         # recipes that must be in $SYSROOT first

# Uncomment for a tool that runs on the build machine rather than the target.
# RECIPE_HOST_ONLY=1

trinix_build() {
    # $BUILDDIR is the cwd; $SRCDIR holds the unpacked, patched source.
    #
    # Autotools: --host is the target triple, --build is left to config.guess.
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --sysconfdir=/etc \
        --localstatedir=/var \
        --disable-static

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    # CMake alternative:
    #   cmake -G Ninja -S "$SRCDIR" \
    #         -DCMAKE_TOOLCHAIN_FILE="$CMAKE_TOOLCHAIN" \
    #         -DCMAKE_INSTALL_PREFIX=/usr \
    #         -DCMAKE_BUILD_TYPE=Release
    #   ninja -j"$JOBS"
    #   DESTDIR="$DESTDIR" ninja install
    #
    # Meson alternative:
    #   meson setup "$BUILDDIR" "$SRCDIR" \
    #         --cross-file "/usr/local/share/trinix/meson/$TARGET_TRIPLE.ini" \
    #         --prefix=/usr --buildtype=release
    #   meson compile -C "$BUILDDIR" -j "$JOBS"
    #   DESTDIR="$DESTDIR" meson install -C "$BUILDDIR"
}

# Optional: assertions run when the stage is built with -Verify. Prefer cheap,
# specific checks — "is it the right machine type", not "does the test suite pass".
trinix_check() {
    llvm-readelf --file-header "$DESTDIR/usr/bin/example" | grep -q "$(
        case "$TRINIX_ARCH" in
            arm64)  echo 'AArch64' ;;
            x86_64) echo 'X86-64'  ;;
        esac
    )"
}
