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
# the top of src/trinix-wlr.h. The two Trinix protocol extensions it implements
# are in base/recipes/trinix-protocols, and the contract they express is in
# docs/vixen-platform-contract.md.

RECIPE_SOURCE=""
RECIPE_DEPENDS="wlroots wayland wayland-protocols wayland-scanner trinix-protocols \
                libxkbcommon pixman"

trinix_build() {
    local wayland_protocols trinix_protocols
    wayland_protocols="$(pkg-config --variable=pkgdatadir wayland-protocols)"
    trinix_protocols="$(pkg-config --variable=pkgdatadir trinix-protocols)"

    # wlroots' installed <wlr/types/wlr_xdg_shell.h> includes
    # "xdg-shell-protocol.h", which wlroots generates during its own build and
    # does not install. Every wlroots consumer therefore regenerates it — from
    # the same XML, with the same scanner, so it is the same file.
    local generated=()
    wayland-scanner server-header \
        "$wayland_protocols/stable/xdg-shell/xdg-shell.xml" \
        "$BUILDDIR/xdg-shell-protocol.h"

    # And the xdg-shell *code*, not just its header, because Trinix's own
    # protocols take an xdg_toplevel as an argument. wayland-scanner emits a
    # reference to xdg_toplevel_interface for that and defines it nowhere;
    # wlroots has a definition but `private-code` gave it hidden visibility, on
    # purpose, so that two libraries embedding the same protocol cannot collide.
    # The reference therefore resolves to nothing, the library loads no further,
    # and .NET reports it as a missing shared library — which is a long way from
    # what actually happened.
    wayland-scanner private-code \
        "$wayland_protocols/stable/xdg-shell/xdg-shell.xml" \
        "$BUILDDIR/xdg-shell-protocol.c"
    generated+=("$BUILDDIR/xdg-shell-protocol.c")

    # Trinix's own protocols. The generated glue is compiled in rather than
    # linked from somewhere: it is a few hundred bytes of dispatch tables, and
    # a shared library for it would be a versioning problem in exchange for
    # nothing.
    local protocol name
    for protocol in "$trinix_protocols"/*.xml; do
        name="$(basename "$protocol" .xml)"
        wayland-scanner server-header "$protocol" "$BUILDDIR/$name-protocol.h"
        wayland-scanner private-code  "$protocol" "$BUILDDIR/$name-protocol.c"
        generated+=("$BUILDDIR/$name-protocol.c")
    done
    [ "${#generated[@]}" -gt 0 ] \
        || { echo 'trinix-wlr: no Trinix protocol XML found' >&2; return 1; }

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
        "$RECIPE_DIR/src/trinix-shell.c" \
        "${generated[@]}" \
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
                  trinix_wlr_cursor_position trinix_wlr_pointer_passthrough \
                  trinix_wlr_toplevel_control_at trinix_wlr_toplevel_in_drag_region \
                  trinix_wlr_toplevel_send_control_hover \
                  trinix_wlr_toplevel_send_control_activated \
                  trinix_wlr_menu_send_activated trinix_wlr_menu_send_about_to_show \
                  trinix_wlr_menu_send_closed; do
        grep -qx "$symbol" <<<"$symbols" || missing="$missing $symbol"
    done
    [ -z "$missing" ] || { echo "trinix-wlr: not exported:$missing" >&2; return 1; }

    # The protocol glue is linked in, not left out: these are the interface
    # descriptions the globals are advertised from, and without them the
    # compositor starts and simply offers nothing.
    #
    # Looked for in the full symbol table rather than the dynamic one, because
    # `wayland-scanner private-code` gives them hidden visibility on purpose —
    # two libraries in one process embedding the same protocol must not export
    # colliding definitions of it. Hidden is the correct outcome here; absent
    # would not be.
    local internal
    internal="$(llvm-nm --defined-only "$lib" | awk '{ print $NF }')"
    for symbol in trinix_shell_v1_interface trinix_shell_surface_v1_interface \
                  trinix_menu_manager_v1_interface trinix_menu_v1_interface; do
        grep -qx "$symbol" <<<"$internal" \
            || { echo "trinix-wlr: $symbol is missing — protocol glue not linked" >&2; return 1; }
    done

    llvm-readelf --dynamic "$lib" | grep -q 'libwlroots-0.19' \
        || { echo 'trinix-wlr: not linked against wlroots' >&2; return 1; }

    # No dangling protocol references.
    #
    # A wl_interface this library names but does not define resolves to nothing
    # at load time, and the whole library then fails to load — which .NET
    # reports as "unable to load shared library 'trinix-wlr' or one of its
    # dependencies", pointing at everything except the cause. Interfaces are
    # always defined by the generated code, never imported, so an undefined one
    # means a protocol whose glue was not compiled in.
    # Resolved against the sysroot rather than guessed at by name: the core
    # wl_* interfaces really are imported, from libwayland-server, while every
    # extension's interface has to be compiled in. Only the sysroot knows which
    # is which, and asking it is both exact and cheap.
    #
    # Every pipeline here is allowed to come back empty: grep exits non-zero
    # when it matches nothing, and llvm-nm exits non-zero on the ASCII linker
    # scripts that sit among the sysroot's .so files. Neither is a failure, and
    # under errexit both would abort the check without printing anything —
    # which is a worse outcome than the bug being looked for.
    local wanted provided dangling
    wanted="$(llvm-nm --undefined-only --dynamic "$lib" | awk '{ print $NF }' \
              | sed 's/@.*//' | grep '_interface$' | sort -u || true)"
    if [ -z "$wanted" ]; then
        return 0
    fi

    provided="$(find "$SYSROOT/usr/lib" -name '*.so*' -type f -print0 2>/dev/null \
                | xargs -0 -n1 llvm-nm --defined-only --dynamic 2>/dev/null \
                | awk '{ print $NF }' | sed 's/@.*//' | sort -u || true)"
    dangling="$(comm -23 <(printf '%s\n' "$wanted") <(printf '%s\n' "$provided") || true)"

    [ -z "$dangling" ] || {
        echo "trinix-wlr: undefined protocol interfaces:" $dangling >&2
        echo "trinix-wlr: generate that protocol's private-code into this library" >&2
        return 1
    }
}
