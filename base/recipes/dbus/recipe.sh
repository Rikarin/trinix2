# shellcheck shell=bash
# D-Bus — the system message bus.
#
# systemd talks its own dialect of the protocol through sd-bus and does not
# need dbus-daemon to boot. This is here for everything above it: logind
# clients, the Phase 4 seat/session handshake, and the Phase 5 global menu bar,
# which is a D-Bus interface in every desktop that has one.
#
# Built after systemd rather than before it, because the unit files and the
# `at_console` support want systemd present at configure time — the dependency
# runs the opposite way from the intuitive one.

RECIPE_SOURCE="dbus"
RECIPE_DEPENDS="glibc-runtime expat systemd"

trinix_build() {
    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --prefix=/usr \
        --sysconfdir=/etc \
        --localstatedir=/var \
        --buildtype=release \
        -Dsystemd=enabled \
        -Dsystemd_system_unitdir=/usr/lib/systemd/system \
        -Dsystemd_user_unitdir=/usr/lib/systemd/user \
        -Duser_session=true \
        -Dmessage_bus=true \
        -Dtools=true \
        `# /run, not /var/run: /var is a symlink onto the writable partition` \
        `# and the bus has to exist before that is mounted.` \
        -Druntime_dir=/run \
        -Dsystem_socket=/run/dbus/system_bus_socket \
        -Dsystem_pid_file=/run/dbus/pid \
        \
        `# Declined, matching the systemd recipe: not in the base image.` \
        -Dselinux=disabled \
        -Dapparmor=disabled \
        -Dlibaudit=disabled \
        -Dx11_autolaunch=disabled \
        -Dkqueue=disabled \
        -Dlaunchd=disabled \
        \
        `# Documentation and tests are build time the image never sees.` \
        -Ddoxygen_docs=disabled \
        -Dducktype_docs=disabled \
        -Dxml_docs=disabled \
        -Dqt_help=disabled \
        -Dmodular_tests=disabled \
        -Dinstalled_tests=false \
        -Dasserts=false

    meson compile -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR" --no-rebuild

    # The bus runs as `messagebus`; the account is created by systemd-sysusers
    # from this file at first boot rather than baked into /etc/passwd, so the
    # image stays identical whatever UID happens to be free.
    install -d "$DESTDIR/usr/lib/sysusers.d"
    cat > "$DESTDIR/usr/lib/sysusers.d/dbus.conf" <<'EOF'
u messagebus - "System Message Bus" - -
EOF
}

trinix_check() {
    local missing=''
    for required in usr/bin/dbus-daemon usr/lib/libdbus-1.so.3 usr/bin/dbus-send; do
        [ -e "$DESTDIR/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "dbus: missing$missing" >&2; return 1; }

    [ -e "$DESTDIR/usr/lib/systemd/system/dbus.socket" ] \
        || { echo 'dbus: no systemd socket unit — the bus would never be activated' >&2; return 1; }
}
