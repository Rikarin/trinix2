# shellcheck shell=bash
# wayland-scanner — the protocol code generator, built for the build machine.
#
# Every Wayland component compiles C that this program wrote: it turns the
# protocol XML into the marshalling glue that libwayland's client and server
# halves are used through. That makes the scanner an input to target binaries
# in exactly the way a compiler is, which is why it is built from Trinix's own
# pinned tarball instead of taken from Debian — a host package would put an
# unpinned code generator in the middle of a build that is otherwise pinned end
# to end.
#
# Same source as the `wayland` recipe, different machine. The target build then
# asks for `scanner=false` and picks this one up through native pkg-config.

RECIPE_SOURCE="wayland"
RECIPE_DEPENDS="expat-host"
RECIPE_HOST_ONLY=1

trinix_build() {
    # /usr/local, because that is where the build container's own pkg-config
    # and PATH look and this is a tool for the container, not for Trinix.
    meson setup "$BUILDDIR" "$SRCDIR" \
        --prefix=/usr/local \
        `# Debian's meson defaults libdir to lib/<multiarch triple>. Everything` \
        `# downstream finds this through pkg-config either way, but a path that` \
        `# depends on what the container is makes for confusing failures.` \
        --libdir=lib \
        --buildtype=release \
        -Dscanner=true \
        -Dlibraries=false \
        -Dtests=false \
        -Ddocumentation=false \
        `# The DTD check is the one thing here that wants libxml2, and it` \
        `# validates the protocol files rather than the generated code.` \
        -Ddtd_validation=false

    meson compile -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR" --no-rebuild
}

trinix_check() {
    [ -x "$DESTDIR/usr/local/bin/wayland-scanner" ] \
        || { echo 'wayland-scanner: not built' >&2; return 1; }

    # It has to run *here*. A cross-built scanner would install happily and
    # then fail at the first recipe that tried to generate anything.
    "$DESTDIR/usr/local/bin/wayland-scanner" --version >/dev/null \
        || { echo 'wayland-scanner: built, but does not run on the build machine' >&2; return 1; }

    # wlroots and libwayland itself both find the scanner through
    # `dependency('wayland-scanner', native: true)`, not through $PATH.
    [ -e "$DESTDIR/usr/local/share/pkgconfig/wayland-scanner.pc" ] \
        || [ -e "$DESTDIR/usr/local/lib/pkgconfig/wayland-scanner.pc" ] \
        || { echo 'wayland-scanner: no pkg-config file — cross builds will not find it' >&2; return 1; }
}
