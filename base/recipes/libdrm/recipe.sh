# shellcheck shell=bash
# libdrm — the userspace side of the kernel's DRM/KMS interface.
#
# Modesetting, connector and CRTC enumeration, and the dumb-buffer ioctls the
# compositor draws into. Every one of the per-vendor helper libraries this can
# build (intel, amdgpu, nouveau, ...) is a GPU-specific command-submission
# helper, and Trinix submits no GPU commands yet — the generic core is the
# whole of what virtio-gpu needs.

RECIPE_SOURCE="libdrm"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --prefix=/usr \
        --buildtype=release \
        `# Vendor backends: none. They are needed by Mesa drivers, and there` \
        `# is no Mesa in the image yet.` \
        -Dintel=disabled \
        -Dradeon=disabled \
        -Damdgpu=disabled \
        -Dnouveau=disabled \
        -Dvmwgfx=disabled \
        -Domap=disabled \
        -Dexynos=disabled \
        -Dfreedreno=disabled \
        -Dtegra=disabled \
        -Dvc4=disabled \
        -Detnaviv=disabled \
        \
        `# udev rather than mknod: /dev is devtmpfs plus udev, and libdrm` \
        `# creating device nodes behind its back would be a surprise.` \
        -Dudev=true \
        -Dcairo-tests=disabled \
        -Dman-pages=disabled \
        -Dvalgrind=disabled \
        -Dtests=false \
        -Dinstall-test-programs=false

    meson compile -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR" --no-rebuild
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libdrm.so.2" ] || { echo 'libdrm: libdrm.so.2 missing' >&2; return 1; }
    [ -e "$DESTDIR/usr/include/xf86drmMode.h" ] \
        || { echo 'libdrm: no modesetting header — wlroots will not configure' >&2; return 1; }
}
