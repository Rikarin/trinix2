# shellcheck shell=bash
# glslangValidator, for the build machine.
#
# Mesa 26 builds ray-tracing acceleration-structure shaders for every Vulkan
# driver including lavapipe — `with_bvh` in its meson.build is "amd or intel or
# swrast or freedreno" — and compiles them from GLSL to SPIR-V at build time.
# The SPIR-V is embedded in the driver, so this compiler's output ends up
# inside a target binary.
#
# Which is exactly the test base/recipes/README.md sets for RECIPE_HOST_ONLY:
# `glslang-tools` from Debian would be one line in the Dockerfile and an
# unpinned input to the graphics driver, in a repository whose first rule is
# that nothing unpinned enters a build. wayland-scanner is here for the same
# reason and generates C rather than SPIR-V; the argument does not change.
#
# Pinned to the same Vulkan SDK tag as vulkan-headers and vulkan-loader, which
# is how Khronos releases the three of them.

RECIPE_SOURCE="glslang"
RECIPE_DEPENDS=""
RECIPE_HOST_ONLY=1

trinix_build() {
    # ENABLE_OPT=OFF drops the SPIR-V optimiser, which would pull SPIRV-Tools
    # and SPIRV-Headers in as two more pinned sources. Mesa asks this program
    # to compile GLSL, not to optimise it — the driver's own compiler does that
    # at draw time, which is what libLLVM is in the image for.
    cmake -G Ninja -S "$SRCDIR" -B "$BUILDDIR" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX=/usr/local \
        -DENABLE_OPT=OFF \
        -DENABLE_GLSLANG_BINARIES=ON \
        -DGLSLANG_TESTS=OFF \
        -DBUILD_TESTING=OFF \
        -DBUILD_SHARED_LIBS=OFF

    ninja -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" ninja -C "$BUILDDIR" install
}

trinix_check() {
    # ⚠ The name matters, not just the program. glslang installs its compiler
    # as `glslang` and keeps `glslangValidator` as a compatibility alias, and
    # Mesa's meson looks for the old name — so a build that dropped the alias
    # would install a working compiler that Mesa cannot find.
    local validator="$DESTDIR/usr/local/bin/glslangValidator"
    [ -x "$validator" ] || { echo 'glslang: glslangValidator is missing — Mesa looks for that name' >&2; return 1; }

    # Mesa requires 12.2 or newer for its preamble, and reads the version by
    # splitting `--version` output on colons. Running it here means a version
    # too old fails in the recipe that pinned it rather than in the one that
    # depends on it.
    "$validator" --version || { echo 'glslang: glslangValidator does not run' >&2; return 1; }
}
