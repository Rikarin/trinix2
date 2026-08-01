# shellcheck shell=bash
# seatd — seat management for a system with no PAM.
#
# A compositor needs privileges it should not keep: open the DRM device, become
# DRM master, open every /dev/input/event*, and hand them all back when the
# user switches to another VT. On a conventional desktop, systemd-logind does
# this, and the compositor reaches it because pam_systemd registered a session
# at login.
#
# Trinix has no PAM stack — shadow's login authenticates against /etc/shadow
# through crypt(3) directly — so no session is ever registered and logind has
# nothing to attach a seat to. seatd is the daemon written for exactly this
# case: it owns the devices, and libseat's seatd backend talks to it over a
# socket. Membership of the `_seatd` group is the whole authorisation model,
# which is honest about what it is.
#
# Both backends are built. The logind one is dead code today and becomes live
# the moment Trinix grows a PAM stack or a session manager of its own, and
# libseat picks between them at runtime.

RECIPE_SOURCE="seatd"
RECIPE_DEPENDS="glibc-runtime systemd"

trinix_build() {
    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --prefix=/usr \
        --buildtype=release \
        -Dserver=enabled \
        -Dlibseat-seatd=enabled \
        -Dlibseat-logind=systemd \
        `# The built-in backend runs the seat logic inside the compositor and` \
        `# needs it to be root. The entire point of running a daemon is not` \
        `# doing that.` \
        -Dlibseat-builtin=disabled \
        -Dexamples=disabled \
        -Dman-pages=disabled

    meson compile -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR" --no-rebuild

    trinix_merge_usr

    # Upstream ships no unit. This one is deliberately not socket-activated:
    # libseat connects, and a compositor that finds no seat does not retry, it
    # exits — so the daemon has to be up before the graphical target, not on
    # first contact.
    install -d "$DESTDIR/usr/lib/systemd/system"
    cat > "$DESTDIR/usr/lib/systemd/system/seatd.service" <<'EOF'
[Unit]
Description=Seat management daemon
Documentation=man:seatd(1)
After=systemd-udevd.service
Wants=systemd-udevd.service

[Service]
Type=simple
ExecStart=/usr/bin/seatd -g _seatd
Restart=always
RestartSec=1

[Install]
WantedBy=multi-user.target
EOF
    # The `_seatd` group itself is baked into /etc/group by the trinix-system
    # recipe rather than declared here as a sysusers.d fragment. /etc is
    # read-only on a running Trinix, so systemd-sysusers has nowhere to write:
    # every identity this system has must exist before the image is sealed.
}

trinix_check() {
    local missing=''
    for required in usr/bin/seatd usr/lib/libseat.so.1 usr/lib/pkgconfig/libseat.pc; do
        [ -e "$DESTDIR/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "seatd: missing$missing" >&2; return 1; }
}
