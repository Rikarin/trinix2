# shellcheck shell=bash
# hwdata — PCI, USB and PNP vendor identifier tables, for the build machine.
#
# Host-only, and unusually so: wlroots asks for it with `native: true` and then
# *compiles* pnp.ids into a lookup table, so that a display can be named from
# the three-letter manufacturer code in its EDID without reading a file at
# runtime. Nothing on the target ever opens it.
#
# It is pinned rather than apt-installed for the same reason wayland-scanner is:
# its contents end up inside a target binary.

RECIPE_SOURCE="hwdata"
RECIPE_DEPENDS=""
RECIPE_HOST_ONLY=1

trinix_build() {
    # Not autotools despite the name: a shell script that writes Makefile.inc,
    # and a Makefile that reads hwdata.spec and the data files from the current
    # directory. It cannot build out of tree, so the tree is copied — the
    # source is shared with the other architecture's build and must come away
    # unmodified.
    cp -a "$SRCDIR/." "$BUILDDIR/"

    ./configure --prefix=/usr/local --datadir=/usr/local/share --disable-blacklist
    make DESTDIR="$DESTDIR" install
}

trinix_check() {
    [ -e "$DESTDIR/usr/local/share/hwdata/pnp.ids" ] \
        || { echo 'hwdata: pnp.ids missing' >&2; return 1; }

    # wlroots reads the location out of the pkg-config file rather than
    # guessing, so a .pc that does not declare pkgdatadir fails the DRM backend
    # at configure time with a much less obvious message than this one.
    grep -q 'pkgdatadir' "$DESTDIR/usr/local/share/pkgconfig/hwdata.pc" 2>/dev/null \
        || grep -q 'pkgdatadir' "$DESTDIR/usr/local/lib/pkgconfig/hwdata.pc" 2>/dev/null \
        || { echo 'hwdata: hwdata.pc does not declare pkgdatadir' >&2; return 1; }
}
