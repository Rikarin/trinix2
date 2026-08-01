# shellcheck shell=bash
# xkeyboard-config — the keyboard layout database.
#
# Data, and the X11 name is historical: this is where "us", "gb", "de-neo" and
# every compose sequence actually live. libxkbcommon compiles a keymap out of
# these files at runtime, so without them a keyboard produces keycodes and no
# keysyms — which looks exactly like a broken compositor.

RECIPE_SOURCE="xkeyboard-config"
RECIPE_DEPENDS=""

trinix_build() {
    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --prefix=/usr \
        --buildtype=release \
        `# Translated layout *descriptions*, for a settings UI that does not` \
        `# exist yet and would be a megabyte of .mo files if it did.` \
        -Dnls=false \
        -Dxorg-rules-symlinks=false \
        -Dnon-latin-layouts-list=false

    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR"
}

trinix_check() {
    # Upstream installs into a versioned directory and leaves
    # /usr/share/X11/xkb as a compatibility symlink to it. Both halves matter:
    # the data is what a keymap is compiled from, and the symlink is the path
    # baked into libxkbcommon.
    local root="$DESTDIR/usr/share/xkeyboard-config-2"
    local missing=''
    for required in rules/evdev symbols/us keycodes/evdev compat/complete; do
        [ -e "$root/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "xkeyboard-config: missing$missing" >&2; return 1; }

    [ -L "$DESTDIR/usr/share/X11/xkb" ] \
        || { echo 'xkeyboard-config: /usr/share/X11/xkb is not the expected symlink' >&2; return 1; }
}
