# shellcheck shell=bash
# libwayland — the protocol library, both halves.
#
# libwayland-server is what the compositor is built on; libwayland-client is
# what every application links against. The library itself is small: it is a
# wire-format marshaller plus an object registry, and all the interesting
# semantics live in the protocol XML that wayland-protocols ships.
#
# libffi is not an optional nicety here — libwayland dispatches every incoming
# message through a libffi call, because the argument list is only known from
# the XML at runtime.

RECIPE_SOURCE="wayland"
RECIPE_DEPENDS="glibc-runtime libffi expat wayland-scanner"

trinix_build() {
    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --native-file "$MESON_NATIVE" \
        --prefix=/usr \
        --buildtype=release \
        -Dlibraries=true \
        `# The scanner is a build-machine tool and was already built as one.` \
        `# Asking for it again here would produce an aarch64 binary that meson` \
        `# would then try to run.` \
        -Dscanner=false \
        -Dtests=false \
        -Ddocumentation=false \
        -Ddtd_validation=false

    meson compile -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR" --no-rebuild
}

trinix_check() {
    local missing=''
    for required in usr/lib/libwayland-server.so.0 usr/lib/libwayland-client.so.0 \
                    usr/lib/pkgconfig/wayland-server.pc; do
        [ -e "$DESTDIR/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "wayland: missing$missing" >&2; return 1; }

    # A target build that quietly re-enabled the scanner would ship a binary
    # here that cannot run on the machine that needs it.
    [ ! -e "$DESTDIR/usr/bin/wayland-scanner" ] \
        || { echo 'wayland: a cross-built wayland-scanner was installed' >&2; return 1; }
}
