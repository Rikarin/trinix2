# shellcheck shell=bash
# wayland-protocols — the protocol definitions themselves.
#
# Installs XML and a pkg-config file and nothing else: xdg-shell (windows),
# presentation-time, viewporter, linux-dmabuf, and the rest of the vocabulary a
# desktop actually needs, none of which is in libwayland's core protocol.
#
# Nothing is compiled, so nothing is architecture-specific. It still goes
# through a recipe rather than being unpacked somewhere convenient, because
# wlroots and every client find it by pkg-config and the version is a real
# compatibility constraint.

RECIPE_SOURCE="wayland-protocols"
RECIPE_DEPENDS="wayland wayland-scanner"

trinix_build() {
    # Installs no code and still wants the scanner: its build checks that every
    # protocol file it ships can actually be scanned.
    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --native-file "$MESON_NATIVE" \
        --prefix=/usr \
        --buildtype=release \
        -Dtests=false

    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR"

    # The XML is a build-time input and ships anyway, because a recipe's
    # staging tree goes to both the sysroot and the rootfs and there is no
    # "sysroot only" to ask for. It is a few hundred kilobytes; when the image
    # gets a prune pass for headers and static libraries, this joins it.
}

trinix_check() {
    [ -e "$DESTDIR/usr/share/wayland-protocols/stable/xdg-shell/xdg-shell.xml" ] \
        || { echo 'wayland-protocols: xdg-shell.xml missing — no windows without it' >&2; return 1; }
    [ -e "$DESTDIR/usr/share/pkgconfig/wayland-protocols.pc" ] \
        || { echo 'wayland-protocols: no pkg-config file' >&2; return 1; }
}
