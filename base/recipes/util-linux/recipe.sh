# shellcheck shell=bash
# util-linux — libmount/libblkid/libuuid, plus mount, agetty and login.
#
# Two distinct reasons this is in the base:
#
#   libmount, libblkid, libuuid   hard systemd dependencies; it will not
#                                 configure without them
#   agetty, login                 the Phase 2 exit criterion is a login prompt
#                                 on a serial console, and these produce it
#
# Deliberately built without PAM, which has a consequence worth stating plainly:
# util-linux's `login` has *required* PAM since 2.34, so it cannot be built here
# and is disabled along with `su` and `runuser`. The serial console therefore
# runs agetty with autologin rather than presenting a password prompt.
#
# That is a real, if temporary, reduction: the first boot lands in a root shell
# instead of authenticating. Restoring proper login means adding linux-pam and
# libxcrypt as recipes, which is worth doing before anything resembling a
# release, but not before the system has booted once.

RECIPE_SOURCE="util-linux"
RECIPE_DEPENDS="glibc-runtime zlib libcap ncurses"

trinix_build() {
    # A `*-config` script on the build machine's PATH is a cross-compilation
    # trap, and this is the one that springs it. util-linux looks for
    # ncursesw6-config, finds the *host's*, and believes what it says — which
    # on Debian is "-lncursesw -ltinfo", because Debian splits terminfo into
    # its own library. Trinix's ncurses does not, so the link then fails on a
    # library that was never going to exist.
    #
    # The sysroot ships its own copy of the script, describing the ncurses that
    # was actually built. Naming it here is the difference between configuring
    # against the target and configuring against the container.
    export NCURSESW6_CONFIG="$SYSROOT/usr/bin/ncursesw6-config"

    # scanf_cv_alloc_modifier cannot be probed when cross-compiling: the test
    # runs a program. glibc supports the 'm' modifier, so answer it directly
    # rather than letting configure guess 'as'.
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --bindir=/usr/bin \
        --sbindir=/usr/bin \
        --libdir=/usr/lib \
        --sysconfdir=/etc \
        --localstatedir=/var \
        --runstatedir=/run \
        --disable-static \
        --disable-nls \
        --disable-rpath \
        --without-python \
        --without-systemd \
        --without-udev \
        --disable-pam-lastlog \
        --disable-liblastlog2 \
        --without-selinux \
        --without-audit \
        --enable-libmount \
        --enable-libblkid \
        --enable-libuuid \
        --enable-agetty \
        `# All three require PAM; see the header comment.` \
        --disable-login \
        --disable-su \
        --disable-runuser \
        --disable-chfn-chsh \
        --disable-makeinstall-chown \
        --disable-makeinstall-setuid \
        `# Terminal handling, answered explicitly rather than autodetected.` \
        `# Left to configure this becomes order-dependent: build util-linux` \
        `# before ncurses and it finds neither; build it after and it links` \
        `# -ltinfo, which Trinix's ncurses does not produce (no --with-termlib,` \
        `# because one library for terminfo and curses is one fewer thing to` \
        `# get out of step). The same rootfs would then build or not depending` \
        `# on what was already in the sysroot — which is the opposite of the` \
        `# reproducibility the pinned-sources discipline is for.` \
        --without-tinfo \
        --with-ncursesw \
        --without-slang \
        --without-readline \
        scanf_cv_alloc_modifier=ms

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    rm -f "$DESTDIR"/usr/lib/*.la
}

trinix_check() {
    local missing=''
    # The three libraries systemd needs, plus agetty (which drives the serial
    # console) and mount.
    for required in usr/lib/libmount.so.1 usr/lib/libblkid.so.1 usr/lib/libuuid.so.1 \
                    usr/bin/agetty usr/bin/mount; do
        [ -e "$DESTDIR/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "util-linux: missing$missing" >&2; return 1; }
}
