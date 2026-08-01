# shellcheck shell=bash
# trinix-wl-demo — the client Phase 4 is proved with.
#
# See src/demo.c for what it does. Why it is Trinix's own rather than a
# third-party application: the exit criterion has to be checked without a
# screen, over a serial console, unattended. A terminal emulator would prove
# more to a human watching, and nothing at all to a script — whereas a client
# that prints "configured 640x480" and "frames=10" is evidence a machine can
# evaluate, which is what the A/B update model needs everything to be.
#
# What keeps it honest is that it speaks only the protocol: libwayland plus
# xdg-shell generated from wayland-protocols' XML, with no knowledge of the
# compositor's implementation. Phase 5's Vixen applications are the answer to
# "does a real toolkit work"; this is the answer to "does the protocol".

RECIPE_SOURCE=""
RECIPE_DEPENDS="wayland wayland-protocols wayland-scanner"

trinix_build() {
    local protocol_dir
    protocol_dir="$(pkg-config --variable=pkgdatadir wayland-protocols)"
    local xdg_shell="$protocol_dir/stable/xdg-shell/xdg-shell.xml"
    [ -f "$xdg_shell" ] || { echo "trinix-wl-demo: no xdg-shell.xml at $xdg_shell" >&2; return 1; }

    # The same generator every other Wayland component in the image used. It
    # runs on the build machine and emits target-independent C.
    wayland-scanner client-header "$xdg_shell" "$BUILDDIR/xdg-shell-client-protocol.h"
    wayland-scanner private-code  "$xdg_shell" "$BUILDDIR/xdg-shell-protocol.c"

    local cflags=() ldflags=()
    mapfile -t cflags  < <(pkg-config --cflags wayland-client | tr ' ' '\n' | grep -v '^$')
    mapfile -t ldflags < <(pkg-config --libs   wayland-client | tr ' ' '\n' | grep -v '^$')

    # -Wno-missing-field-initializers: designated initialisers for a struct
    # with two dozen fields are the point of designated initialisers.
    "$CC" -O2 -std=c11 -Wall -Wextra -Werror -Wno-missing-field-initializers \
        -I"$BUILDDIR" \
        "${cflags[@]}" \
        -o "$BUILDDIR/trinix-wl-demo" \
        "$RECIPE_DIR/src/demo.c" \
        "$BUILDDIR/xdg-shell-protocol.c" \
        "${ldflags[@]}"

    install -d "$DESTDIR/usr/bin"
    install -m 755 "$BUILDDIR/trinix-wl-demo" "$DESTDIR/usr/bin/trinix-wl-demo"

    # Installed but not enabled — see the unit's own header.
    install -d "$DESTDIR/usr/lib/systemd/system"
    install -m 644 "$RECIPE_DIR/trinix-wl-demo.service" \
                   "$DESTDIR/usr/lib/systemd/system/trinix-wl-demo.service"
}

trinix_check() {
    local binary="$DESTDIR/usr/bin/trinix-wl-demo"
    [ -x "$binary" ] || { echo 'trinix-wl-demo: not built' >&2; return 1; }

    llvm-readelf --file-header "$binary" | grep -q "$(
        case "$TRINIX_ARCH" in
            arm64)  echo 'AArch64' ;;
            x86_64) echo 'X86-64'  ;;
        esac
    )" || { echo 'trinix-wl-demo: wrong machine type' >&2; return 1; }
}
