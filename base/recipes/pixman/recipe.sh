# shellcheck shell=bash
# pixman — pixel manipulation.
#
# On most systems this is the fallback nobody exercises. On Trinix it is the
# renderer: the compositor composites through wlroots' pixman renderer into DRM
# dumb buffers, because the VM's virtio-gpu offers no render node to accelerate
# against. See base/recipes/wlroots/recipe.sh.
#
# The SIMD backends therefore matter. They are asked for explicitly rather than
# left to autodetection, so that a rootfs is the same whatever the build
# machine happened to be able to probe.

RECIPE_SOURCE="pixman"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    local simd=()
    case "$TRINIX_ARCH" in
        arm64)  simd=(-Da64-neon=enabled  -Dneon=disabled -Darm-simd=disabled
                      -Dmmx=disabled -Dsse2=disabled -Dssse3=disabled) ;;
        # -march=x86-64-v2 guarantees SSE4.2, so SSE2 and SSSE3 are a given.
        x86_64) simd=(-Dsse2=enabled -Dssse3=enabled -Dmmx=disabled
                      -Da64-neon=disabled -Dneon=disabled -Darm-simd=disabled) ;;
    esac

    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --prefix=/usr \
        --buildtype=release \
        "${simd[@]}" \
        -Dloongson-mmi=disabled \
        -Dvmx=disabled \
        -Dmips-dspr2=disabled \
        -Drvv=disabled \
        -Dopenmp=disabled \
        -Dgtk=disabled \
        -Dlibpng=disabled \
        -Dtests=disabled \
        -Ddemos=disabled

    meson compile -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR" --no-rebuild
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libpixman-1.so.0" ] \
        || { echo 'pixman: libpixman-1.so.0 missing' >&2; return 1; }
}
