# shellcheck shell=bash
# The Vulkan API headers and the registry they are generated from.
#
# Headers and cmake files only — nothing is compiled and nothing ships that the
# loader does not. They are a recipe rather than a copy inside the loader's
# because Khronos versions the two together and the loader must be built
# against the headers of its own SDK tag: a loader compiled against newer
# headers advertises entry points its dispatch tables do not have.
#
# Mesa is not a consumer. It carries its own copy of these headers in-tree,
# which is how an ICD can be built against a Vulkan version the system loader
# has never heard of.

RECIPE_SOURCE="vulkan-headers"
RECIPE_DEPENDS=""

trinix_build() {
    cmake -G Ninja -S "$SRCDIR" -B "$BUILDDIR" \
        -DCMAKE_TOOLCHAIN_FILE="$CMAKE_TOOLCHAIN" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX=/usr

    DESTDIR="$DESTDIR" ninja -C "$BUILDDIR" -j "$JOBS" install
}

trinix_check() {
    [ -e "$DESTDIR/usr/include/vulkan/vulkan_core.h" ] \
        || { echo 'vulkan-headers: vulkan_core.h missing' >&2; return 1; }

    # The loader locates these through find_package, so the config file is the
    # part that actually has to be there — headers alone configure to "not
    # found" and the loader falls back to a vendored copy of a different age.
    find "$DESTDIR/usr" -name 'VulkanHeadersConfig.cmake' -print -quit | grep -q . \
        || { echo 'vulkan-headers: no VulkanHeadersConfig.cmake' >&2; return 1; }
}
