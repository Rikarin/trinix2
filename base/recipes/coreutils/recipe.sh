# shellcheck shell=bash
# coreutils — ls, cp, rm, mkdir, cat, and the rest of the vocabulary.
#
# Chosen over busybox for the shipped system: an immutable image is built once
# and lived in for a long time, and the difference between GNU ls and busybox ls
# stops being cosmetic the moment someone is debugging over a serial console.
# busybox stays pinned for the initramfs, where size is the argument that wins.

RECIPE_SOURCE="coreutils"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    # coreutils' configure refuses to run as root, because its *test suite*
    # does destructive things that are only safe as an unprivileged user. The
    # tests are not run here, and everything in a build container is root, so
    # the check has nothing left to protect.
    export FORCE_UNSAFE_CONFIGURE=1

    # gnulib probes two filesystem behaviours by running a program, so they have
    # to be answered for a cross build. Both are what Linux/glibc does.
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --disable-nls \
        --without-openssl \
        --enable-no-install-program=kill,uptime \
        fu_cv_sys_stat_statfs2_bsize=yes \
        gl_cv_macro_MB_CUR_MAX_good=yes

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install
}

trinix_check() {
    local missing=''
    for required in ls cp mv rm mkdir cat chmod chown ln df dd stat sync; do
        [ -x "$DESTDIR/usr/bin/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "coreutils: missing$missing" >&2; return 1; }

    # `kill` and `uptime` are deliberately not installed — systemd and procps
    # own those names. Catch it if that ever silently changes, because two
    # recipes writing the same path is how a rootfs quietly becomes wrong.
    [ ! -e "$DESTDIR/usr/bin/kill" ] \
        || { echo 'coreutils: installed kill, which belongs to util-linux' >&2; return 1; }
}
