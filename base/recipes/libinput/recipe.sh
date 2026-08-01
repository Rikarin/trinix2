# shellcheck shell=bash
# libinput — input device handling and policy.
#
# This is the component that decides what a two-finger drag means, how far a
# pointer travels for a given amount of physical movement, and which of a
# laptop's four input devices is the touchpad. All of that is policy, all of it
# is wrong in a hundred device-specific ways, and libinput carries the quirks
# database that encodes the corrections. A compositor that reimplements this
# reimplements the bugs.
#
# wlroots' libinput backend is what turns these into Wayland input events; the
# C# compositor never sees libinput directly.

RECIPE_SOURCE="libinput"
RECIPE_DEPENDS="glibc-runtime systemd libevdev mtdev"

trinix_build() {
    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --prefix=/usr \
        --buildtype=release \
        `# udev rules live with the rest of them, under /usr.` \
        -Dudev-dir=/usr/lib/udev \
        \
        `# libwacom identifies graphics tablets, and brings in glib to do it.` \
        `# Nothing in a Phase 4 base image draws with a stylus.` \
        -Dlibwacom=false \
        `# The debug GUI is a GTK application. In a base system image.` \
        -Ddebug-gui=false \
        -Dtests=false \
        -Dinstall-tests=false \
        -Ddocumentation=false \
        -Dzshcompletiondir=no

    meson compile -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR" --no-rebuild

    # Half of libinput's helper tools are Python, and Trinix has no Python
    # interpreter. A tool that cannot start is worse than an absent one,
    # because it is discovered by someone who is already debugging something
    # else. The C ones — list-devices, debug-events, record — stay: they are
    # how an input problem gets diagnosed on a machine with no desktop yet.
    local tool shebang
    for tool in "$DESTDIR/usr/libexec/libinput/"*; do
        [ -f "$tool" ] || continue
        read -r shebang < "$tool" || continue
        case "$shebang" in
            '#!'*python*) rm -f "$tool" ;;
        esac
    done

    rm -rf "$DESTDIR/usr/share/man"
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libinput.so.10" ] \
        || { echo 'libinput: libinput.so.10 missing' >&2; return 1; }

    # The quirks files are the part with actual knowledge in them; a build that
    # installed the library without them would work and misbehave.
    local quirks
    quirks="$(find "$DESTDIR/usr/share/libinput" -name '*.quirks' 2>/dev/null | wc -l)"
    [ "$quirks" -gt 0 ] || { echo 'libinput: quirks database missing' >&2; return 1; }
}
