# shellcheck shell=bash
# trinix-protocols — Trinix's own Wayland protocol extensions.
#
# XML and a pkg-config file, and nothing else: exactly the shape
# wayland-protocols has, for exactly the same reason. The definitions are the
# contract between the compositor and everything that draws a window, so both
# halves have to generate their code from one file rather than from two copies
# that agree until they do not.
#
# Consumers find it the way they find wayland-protocols:
#
#   pkg-config --variable=pkgdatadir trinix-protocols
#
# and generate against it with wayland-scanner. That is also why this ships in
# the image rather than living only in the build: an application built on
# Trinix — Vixen, or anything else — needs the XML, and a system whose private
# protocol is only available to its own build tree is a system with a private
# protocol nobody can implement.
#
# The contract these express, and the reasoning behind each choice, is in
# docs/vixen-platform-contract.md.

RECIPE_SOURCE=""
# wayland-protocols because these extend xdg-shell; wayland-scanner because
# the check below is the only real validation protocol XML ever gets.
RECIPE_DEPENDS="wayland-protocols wayland-scanner"

TRINIX_PROTOCOL_DIR='/usr/share/trinix-protocols'

trinix_build() {
    install -d "$DESTDIR$TRINIX_PROTOCOL_DIR"
    install -m 644 "$RECIPE_DIR/protocol"/*.xml "$DESTDIR$TRINIX_PROTOCOL_DIR/"

    # Architecture-independent, so share/pkgconfig rather than lib/pkgconfig —
    # the same place wayland-protocols puts its own.
    install -d "$DESTDIR/usr/share/pkgconfig"
    cat > "$DESTDIR/usr/share/pkgconfig/trinix-protocols.pc" <<EOF
prefix=/usr
datarootdir=\${prefix}/share
pkgdatadir=\${pc_sysrootdir}$TRINIX_PROTOCOL_DIR

Name: trinix-protocols
Description: Trinix Wayland protocol extensions
Version: 1
EOF
}

trinix_check() {
    local missing=''
    for required in trinix-shell-v1.xml trinix-menu-v1.xml; do
        [ -e "$DESTDIR$TRINIX_PROTOCOL_DIR/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "trinix-protocols: missing$missing" >&2; return 1; }

    # The scanner is the only judge of whether a protocol file is well formed,
    # and it is already here. Running it now means a typo fails this recipe
    # rather than the two recipes that generate from it.
    local xml
    for xml in "$DESTDIR$TRINIX_PROTOCOL_DIR"/*.xml; do
        wayland-scanner client-header "$xml" /dev/null \
            || { echo "trinix-protocols: ${xml##*/} is not valid protocol XML" >&2; return 1; }
    done
}
