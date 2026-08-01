# shellcheck shell=bash
# libevdev — a sane wrapper over the kernel's evdev character devices.
#
# Reading /dev/input/event* directly is possible and nobody sane does it: the
# protocol is stateful, events arrive in frames that must be assembled, and the
# device's capabilities are queried through a wall of ioctls. libevdev is the
# layer libinput is built on, and the only reason it appears here.

RECIPE_SOURCE="libevdev"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --prefix=/usr \
        --buildtype=release \
        -Dtests=disabled \
        `# The tools are evtest-alikes; useful on a developer's machine and` \
        `# not part of a base image.` \
        -Dtools=disabled \
        -Ddocumentation=disabled

    meson compile -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR" --no-rebuild
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libevdev.so.2" ] || { echo 'libevdev: libevdev.so.2 missing' >&2; return 1; }
}
