# shellcheck shell=bash
# trinix-wl-client — the C half of Trinix's Vixen platform.
#
# The mirror image of base/recipes/trinix-wlr, on the other side of the wire,
# and here for the same structural reason and one sharper one. What each is for
# is documented at the top of its own header; the short version is that
# libwayland's client API is proxy-and-interface-table based rather than a wire
# format C# could speak instead, and that Vulkan's WSI takes a libwayland
# `struct wl_display *` and calls libwayland on it, so the display has to be
# libwayland's whatever else is decided.
#
# Source lives in this recipe's src/ for trinix-wlr's reason: it cross-compiles
# with clang against a sysroot, which is what a recipe does and what the
# `dotnet publish` stage cannot, and keeping it here puts it in the rebuild
# stamp so that editing a .c file rebuilds the library.

RECIPE_SOURCE=""
RECIPE_DEPENDS="wayland wayland-protocols wayland-scanner trinix-protocols libxkbcommon"

trinix_build() {
    local wayland_protocols trinix_protocols
    wayland_protocols="$(pkg-config --variable=pkgdatadir wayland-protocols)"
    trinix_protocols="$(pkg-config --variable=pkgdatadir trinix-protocols)"

    local xdg_shell="$wayland_protocols/stable/xdg-shell/xdg-shell.xml"
    [ -f "$xdg_shell" ] || { echo "trinix-wl-client: no xdg-shell.xml at $xdg_shell" >&2; return 1; }

    # The *client* headers, which is the whole difference from trinix-wlr's
    # generation step: the same XML, the same scanner, the other side's stubs.
    local generated=()
    wayland-scanner client-header "$xdg_shell" "$BUILDDIR/xdg-shell-client-protocol.h"
    wayland-scanner private-code  "$xdg_shell" "$BUILDDIR/xdg-shell-protocol.c"
    generated+=("$BUILDDIR/xdg-shell-protocol.c")

    # Trinix's own two, from the same XML the compositor generates its server
    # side from — which is the point of shipping the protocol as a package
    # rather than as two copies that drift.
    local protocol name
    for protocol in "$trinix_protocols"/*.xml; do
        name="$(basename "$protocol" .xml)"
        wayland-scanner client-header "$protocol" "$BUILDDIR/$name-client-protocol.h"
        wayland-scanner private-code  "$protocol" "$BUILDDIR/$name-protocol.c"
        generated+=("$BUILDDIR/$name-protocol.c")
    done

    local cflags=() ldflags=()
    mapfile -t cflags  < <(pkg-config --cflags wayland-client xkbcommon | tr ' ' '\n' | grep -v '^$')
    mapfile -t ldflags < <(pkg-config --libs   wayland-client xkbcommon | tr ' ' '\n' | grep -v '^$')

    # -Wno-missing-field-initializers: a listener struct is designated
    # initialisers over a dozen events, which is what designated initialisers
    # are for, and every event this library ignores is one it names and voids.
    "$CC" -O2 -std=c11 -fPIC -shared \
        -Wall -Wextra -Werror -Wno-missing-field-initializers \
        -Wl,-soname,libtrinix-wl-client.so.1 \
        -I"$BUILDDIR" -I"$RECIPE_DIR/src" \
        "${cflags[@]}" \
        -o "$BUILDDIR/libtrinix-wl-client.so.1" \
        "$RECIPE_DIR/src/trinix-wl-client.c" \
        "${generated[@]}" \
        "${ldflags[@]}"

    install -d "$DESTDIR/usr/lib" "$DESTDIR/usr/include"
    install -m 755 "$BUILDDIR/libtrinix-wl-client.so.1" "$DESTDIR/usr/lib/"
    ln -sfn libtrinix-wl-client.so.1 "$DESTDIR/usr/lib/libtrinix-wl-client.so"
    install -m 644 "$RECIPE_DIR/src/trinix-wl-client.h" "$DESTDIR/usr/include/"
}

trinix_check() {
    local lib="$DESTDIR/usr/lib/libtrinix-wl-client.so.1"
    [ -e "$lib" ] || { echo 'trinix-wl-client: not built' >&2; return 1; }

    local want
    case "$TRINIX_ARCH" in
        arm64)  want='AArch64' ;;
        x86_64) want='X86-64'  ;;
    esac
    llvm-readelf --file-header "$lib" | grep -q "$want" \
        || { echo 'trinix-wl-client: wrong machine type' >&2; return 1; }

    # Every entry point the managed side resolves by name. A P/Invoke that
    # finds no symbol throws EntryPointNotFoundException at the first call,
    # which is a long way from the build that dropped it — and dropping one is
    # a plausible accident, since nothing in C fails when a function is unused.
    local symbols missing=''
    symbols="$(llvm-nm --defined-only --dynamic "$lib" | awk '{ print $NF }' | sed 's/@@.*//')"
    for symbol in trinix_wl_client_connect trinix_wl_client_destroy trinix_wl_client_pump \
                  trinix_wl_client_roundtrip \
                  trinix_wl_client_globals trinix_wl_client_display \
                  trinix_wl_window_create trinix_wl_window_destroy trinix_wl_window_surface \
                  trinix_wl_window_set_title trinix_wl_window_set_mode \
                  trinix_wl_window_begin_move trinix_wl_window_begin_resize \
                  trinix_wl_window_set_shadow trinix_wl_window_set_corner_radius \
                  trinix_wl_window_set_drag_region trinix_wl_window_set_control \
                  trinix_wl_client_menu_create trinix_wl_window_menu_create \
                  trinix_wl_menu_insert trinix_wl_menu_commit trinix_wl_menu_destroy; do
        grep -qx "$symbol" <<<"$symbols" || missing="$missing $symbol"
    done
    [ -z "$missing" ] || { echo "trinix-wl-client: missing entry point(s):$missing" >&2; return 1; }

    # And that it really is a client: linking libwayland-server here would mean
    # the wrong scanner output got compiled in, which produces a library that
    # builds and then cannot talk to anything.
    if llvm-readelf --dynamic "$lib" | grep -q 'libwayland-server'; then
        echo 'trinix-wl-client: links libwayland-server — server stubs were generated' >&2
        return 1
    fi
}
