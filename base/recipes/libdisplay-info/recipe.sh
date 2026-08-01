# shellcheck shell=bash
# libdisplay-info — parse EDID and DisplayID.
#
# A monitor describes itself in a binary blob that has accumulated thirty years
# of extensions and vendor deviation. wlroots requires this for its DRM
# backend, and uses it to answer questions the compositor genuinely needs:
# physical size, preferred mode, and what the display is called.

RECIPE_SOURCE="libdisplay-info"
RECIPE_DEPENDS="glibc-runtime hwdata"

trinix_build() {
    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --native-file "$MESON_NATIVE" \
        --prefix=/usr \
        --buildtype=release

    meson compile -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR" --no-rebuild
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libdisplay-info.so.2" ] \
        || { echo 'libdisplay-info: libdisplay-info.so.2 missing' >&2; return 1; }
}
