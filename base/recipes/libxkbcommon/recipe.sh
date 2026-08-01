# shellcheck shell=bash
# libxkbcommon — compile a keymap, turn keycodes into keysyms.
#
# Wayland has no keymap protocol of its own: the compositor compiles a keymap
# and hands clients the compiled bytes over a file descriptor, and every client
# feeds those to its own copy of this library. So the compositor and the
# applications must agree on it, which is why it is a base component rather
# than something the compositor bundles.

RECIPE_SOURCE="libxkbcommon"
RECIPE_DEPENDS="glibc-runtime xkeyboard-config"

trinix_build() {
    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --prefix=/usr \
        --buildtype=release \
        `# Where the keymap database from the xkeyboard-config recipe lives.` \
        `# Left to autodetection it resolves against the *build* machine's` \
        `# pkg-config, which answers with Debian's path.` \
        -Dxkb-config-root=/usr/share/X11/xkb \
        -Dx-locale-root=/usr/share/X11/locale \
        \
        -Denable-x11=false \
        `# The Wayland utilities are xkbcli subcommands for debugging keymaps;` \
        `# they would pull in wayland-scanner and the client library for a tool` \
        `# nothing in the image runs.` \
        -Denable-wayland=false \
        -Denable-tools=false \
        `# libxkbregistry parses the layout list as XML, for settings UIs.` \
        -Denable-xkbregistry=false \
        -Denable-docs=false \
        -Denable-bash-completion=false

    meson compile -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR" --no-rebuild
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libxkbcommon.so.0" ] \
        || { echo 'libxkbcommon: libxkbcommon.so.0 missing' >&2; return 1; }

    # An xkbcommon built against the host's config root compiles keymaps that
    # do not exist on the target. The path is baked into the library, so this
    # is the only place the mistake is visible.
    grep -q '/usr/share/X11/xkb' "$DESTDIR/usr/lib/libxkbcommon.so.0" \
        || { echo 'libxkbcommon: does not reference the target XKB config root' >&2; return 1; }
}
