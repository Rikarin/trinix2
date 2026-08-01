# shellcheck shell=bash
# trinix-wlr — the C half of Trinix's compositor.
#
# Source lives in this recipe's src/ rather than under src/ with the rest of
# Trinix's own code, and that is a deliberate trade rather than an oversight.
# The library is cross-compiled with clang against a sysroot that has wlroots
# in it, which is exactly what a recipe does and exactly what the `dotnet
# publish` stage cannot do — that stage runs against the toolchain image, whose
# sysroot predates every graphics recipe. Keeping the source here also keeps it
# in the rebuild stamp, which hashes the whole recipe directory, so editing a
# .c file rebuilds the library. Source in a directory the driver cannot see
# would need a second staleness mechanism to get that right.
#
# What it is for, and where the line between C and C# falls, is documented at
# the top of src/trinix-wlr.h.

RECIPE_SOURCE=""
RECIPE_DEPENDS="wlroots wayland wayland-protocols wayland-scanner libxkbcommon pixman"

trinix_build() {
    # wlroots' installed <wlr/types/wlr_xdg_shell.h> includes
    # "xdg-shell-protocol.h", which wlroots generates during its own build and
    # does not install. Every wlroots consumer therefore regenerates it — from
    # the same XML, with the same scanner, so it is the same file.
    local protocol_dir
    protocol_dir="$(pkg-config --variable=pkgdatadir wayland-protocols)"
    wayland-scanner server-header \
        "$protocol_dir/stable/xdg-shell/xdg-shell.xml" \
        "$BUILDDIR/xdg-shell-protocol.h"

    local cflags=() ldflags=()
    # One pkg-config call, so a missing dependency fails here rather than as an
    # undefined symbol at the end of the link.
    mapfile -t cflags < <(pkg-config --cflags wlroots-0.19 wayland-server xkbcommon pixman-1 | tr ' ' '\n' | grep -v '^$')
    mapfile -t ldflags < <(pkg-config --libs wlroots-0.19 wayland-server xkbcommon pixman-1 | tr ' ' '\n' | grep -v '^$')

    "$CC" -shared -fPIC -O2 -std=c11 \
        `# wlroots' headers refuse to compile without an acknowledgement that` \
        `# its API is not stable. Trinix pins the version for that reason.` \
        -DWLR_USE_UNSTABLE \
        -Wall -Wextra -Werror \
        -I"$BUILDDIR" \
        "${cflags[@]}" \
        -o "$BUILDDIR/libtrinix-wlr.so" \
        "$RECIPE_DIR/src/trinix-wlr.c" \
        "${ldflags[@]}" \
        -Wl,-soname,libtrinix-wlr.so.1

    install -d "$DESTDIR/usr/lib" "$DESTDIR/usr/include"
    install -m 755 "$BUILDDIR/libtrinix-wlr.so" "$DESTDIR/usr/lib/libtrinix-wlr.so.1"
    ln -sfn libtrinix-wlr.so.1 "$DESTDIR/usr/lib/libtrinix-wlr.so"
    install -m 644 "$RECIPE_DIR/src/trinix-wlr.h" "$DESTDIR/usr/include/trinix-wlr.h"
}

trinix_check() {
    local lib="$DESTDIR/usr/lib/libtrinix-wlr.so.1"

    # Everything the managed side calls by name. A typo in a DllImport is a
    # runtime EntryPointNotFoundException inside a compositor with no console,
    # so the names are checked while there is still somewhere to print.
    local symbols missing=''
    symbols="$(llvm-nm --defined-only --dynamic "$lib" | awk '{ print $NF }')"
    for symbol in trinix_wlr_create trinix_wlr_start trinix_wlr_socket \
                  trinix_wlr_run trinix_wlr_terminate trinix_wlr_destroy \
                  trinix_wlr_output_size \
                  trinix_wlr_toplevel_title trinix_wlr_toplevel_app_id \
                  trinix_wlr_toplevel_set_position trinix_wlr_toplevel_set_size \
                  trinix_wlr_toplevel_get_box trinix_wlr_toplevel_focus \
                  trinix_wlr_toplevel_close trinix_wlr_toplevel_at \
                  trinix_wlr_cursor_position trinix_wlr_pointer_passthrough; do
        grep -qx "$symbol" <<<"$symbols" || missing="$missing $symbol"
    done
    [ -z "$missing" ] || { echo "trinix-wlr: not exported:$missing" >&2; return 1; }

    llvm-readelf --dynamic "$lib" | grep -q 'libwlroots-0.19' \
        || { echo 'trinix-wlr: not linked against wlroots' >&2; return 1; }
}
