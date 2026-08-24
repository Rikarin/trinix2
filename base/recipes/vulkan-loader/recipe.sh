# shellcheck shell=bash
# libvulkan.so.1 — the thing an application actually links against.
#
# Vulkan has no single implementation: the loader is a thin dispatcher that
# reads the ICD manifests in /usr/share/vulkan/icd.d, dlopens the drivers they
# name, and forwards every call. Trinix installs exactly one ICD — Mesa's
# lavapipe, which rasterises on the CPU — so the loader has one driver to find.
# It is still the loader that has to be present under the soname, because that
# is the name a Vulkan application links: Vixen reaches Vulkan through
# Silk.NET, whose DllImport asks for "vulkan", and no ICD answers to that.
#
# WSI: Wayland only. The loader compiles in one code path per window system it
# knows, and Trinix has one. Building the X11 paths would mean linking libxcb
# into the image to support a display server it does not have.

RECIPE_SOURCE="vulkan-loader"
RECIPE_DEPENDS="glibc-runtime vulkan-headers wayland"

trinix_build() {
    cmake -G Ninja -S "$SRCDIR" -B "$BUILDDIR" \
        -DCMAKE_TOOLCHAIN_FILE="$CMAKE_TOOLCHAIN" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX=/usr \
        -DCMAKE_INSTALL_SYSCONFDIR=/etc \
        \
        `# Every window system answered explicitly. Left to itself the loader` \
        `# detects what is in the sysroot, so the image would differ between a` \
        `# clean build and one where something had pulled xcb in.` \
        -DBUILD_WSI_WAYLAND_SUPPORT=ON \
        -DBUILD_WSI_XCB_SUPPORT=OFF \
        -DBUILD_WSI_XLIB_SUPPORT=OFF \
        -DBUILD_WSI_DIRECTFB_SUPPORT=OFF \
        -DBUILD_WSI_SCREEN_QNX_SUPPORT=OFF \
        \
        -DBUILD_TESTS=OFF \
        -DUPDATE_DEPS=OFF \
        `# The default is /usr/local/etc plus /etc; naming it keeps the search` \
        `# list to the one directory Mesa installs its manifest into.` \
        -DSYSCONFDIR=/etc \
        -DFALLBACK_DATA_DIRS=/usr/local/share:/usr/share

    DESTDIR="$DESTDIR" ninja -C "$BUILDDIR" -j "$JOBS" install
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libvulkan.so.1" ] \
        || { echo 'vulkan-loader: libvulkan.so.1 missing' >&2; return 1; }

    # Silk.NET's DllImport("vulkan") resolves to libvulkan.so, so the
    # development symlink is load-bearing here rather than a build artefact —
    # unusually, and the reason this is asserted rather than assumed.
    [ -e "$DESTDIR/usr/lib/libvulkan.so" ] \
        || { echo 'vulkan-loader: libvulkan.so missing — DllImport("vulkan") will not resolve' >&2; return 1; }

    # A loader built without the Wayland WSI links, runs, and has no way to
    # make a surface — which surfaces as a driver problem rather than a build
    # one. It is asserted by symbol rather than by NEEDED because the loader
    # links no wayland library at all: vkCreateWaylandSurfaceKHR only carries
    # the wl_display through to the ICD, so the WSI is a compile-time path
    # here and nothing more.
    #
    # The symbol list is captured before it is searched, rather than piped into
    # `grep -q`: grep exits on the first match, awk takes SIGPIPE, and under
    # the driver's `set -o pipefail` a successful match becomes a failed check.
    local symbols
    symbols="$(llvm-nm --defined-only --dynamic "$DESTDIR/usr/lib/libvulkan.so.1" | awk '{ print $NF }')"
    grep -qx 'vkCreateWaylandSurfaceKHR' <<<"$symbols" \
        || { echo 'vulkan-loader: vkCreateWaylandSurfaceKHR not exported — the Wayland WSI was not built' >&2; return 1; }
}
