# shellcheck shell=bash
# trinix-vk-probe — the Vulkan runtime, as something a script can check.
#
# See src/probe.c for what it does and why the frame is a clear rather than a
# triangle. This recipe exists for the same reason trinix-wl-demo's does: the
# graphics stack has to be provable over a serial console, unattended, before
# there is a toolkit on top of it to prove it by accident.
#
# The difference between the two is the whole of Phase 5's graphics work.
# trinix-wl-demo answers "does the protocol work" with wl_shm buffers it fills
# itself. This answers "does Vulkan work" — loader, ICD, device, swapchain,
# present — which is the question a Vixen application's first frame asks.

RECIPE_SOURCE=""
RECIPE_DEPENDS="wayland wayland-protocols wayland-scanner vulkan-headers vulkan-loader mesa"

trinix_build() {
    local protocol_dir xdg_shell
    protocol_dir="$(pkg-config --variable=pkgdatadir wayland-protocols)"
    xdg_shell="$protocol_dir/stable/xdg-shell/xdg-shell.xml"
    [ -f "$xdg_shell" ] || { echo "trinix-vk-probe: no xdg-shell.xml at $xdg_shell" >&2; return 1; }

    wayland-scanner client-header "$xdg_shell" "$BUILDDIR/xdg-shell-client-protocol.h"
    wayland-scanner private-code  "$xdg_shell" "$BUILDDIR/xdg-shell-protocol.c"

    # ⚠ No trinix-shell-v1 and no trinix-menu-v1, deliberately. A probe that
    # bound Trinix's own extensions would stop being a check of the *Vulkan*
    # stack and start being a second copy of trinix-wl-demo. What it presents
    # is an undecorated xdg_toplevel, which the contract says must work.
    local cflags=() ldflags=()
    mapfile -t cflags  < <(pkg-config --cflags wayland-client vulkan | tr ' ' '\n' | grep -v '^$')
    mapfile -t ldflags < <(pkg-config --libs   wayland-client vulkan | tr ' ' '\n' | grep -v '^$')

    "$CC" -O2 -std=c11 -Wall -Wextra -Werror -Wno-missing-field-initializers \
        -I"$BUILDDIR" \
        "${cflags[@]}" \
        -o "$BUILDDIR/trinix-vk-probe" \
        "$RECIPE_DIR/src/probe.c" \
        "$BUILDDIR/xdg-shell-protocol.c" \
        "${ldflags[@]}"

    install -d "$DESTDIR/usr/bin"
    install -m 755 "$BUILDDIR/trinix-vk-probe" "$DESTDIR/usr/bin/trinix-vk-probe"

    install -d "$DESTDIR/usr/lib/systemd/system"
    install -m 644 "$RECIPE_DIR/trinix-vk-probe.service" \
                   "$DESTDIR/usr/lib/systemd/system/trinix-vk-probe.service"
}

trinix_check() {
    local binary="$DESTDIR/usr/bin/trinix-vk-probe"
    [ -x "$binary" ] || { echo 'trinix-vk-probe: not built' >&2; return 1; }

    local want
    case "$TRINIX_ARCH" in
        arm64)  want='AArch64' ;;
        x86_64) want='X86-64'  ;;
    esac
    llvm-readelf --file-header "$binary" | grep -q "$want" \
        || { echo 'trinix-vk-probe: wrong machine type' >&2; return 1; }

    # It must reach Vulkan through the loader rather than through a driver it
    # linked directly — that is the whole architecture, and linking libvulkan
    # is what proves the probe is testing the same path an application takes.
    llvm-readelf --dynamic "$binary" | grep -q 'libvulkan' \
        || { echo 'trinix-vk-probe: does not link libvulkan' >&2; return 1; }
}
