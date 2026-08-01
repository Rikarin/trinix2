# shellcheck shell=bash
# bash — the interactive rescue shell.
#
# Trinix has three shells and each has one job: PowerShell is what a user logs
# into, dash is /bin/sh for the thousands of scripts that hardcode it, and bash
# is what you get when either of the other two is the thing that is broken. It
# is also what the `trinix-rescue` boot target will drop into in Phase 3.
#
# Not /bin/sh: bash in POSIX mode is still bash, and having /bin/sh be a shell
# with a decade of extensions available is how scripts acquire accidental
# bashisms that then fail somewhere else.

RECIPE_SOURCE="bash"
RECIPE_DEPENDS="glibc-runtime ncurses readline"

trinix_build() {
    # Autotools has no way to run a target binary, and bash's configure wants to
    # run about a dozen — so every test that would execute something has to be
    # answered here instead. The values are not guesses: they are what these
    # probes return on any modern glibc/Linux system, which is the only kind of
    # system Trinix targets. A wrong answer here does not fail the build, it
    # produces a subtly broken shell, so each one is worth naming.
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --without-bash-malloc \
        --with-installed-readline \
        --with-curses \
        --disable-nls \
        `# job control, /dev/fd and POSIX signals: present since forever` \
        bash_cv_job_control_missing=present \
        bash_cv_sys_named_pipes=present \
        bash_cv_dev_fd=standard \
        bash_cv_func_sigsetjmp=present \
        bash_cv_unusable_rtsigs=no \
        bash_cv_must_reinstall_sighandlers=no \
        `# glibc specifics` \
        bash_cv_getcwd_malloc=yes \
        bash_cv_printf_a_format=yes \
        bash_cv_ulimit_maxfds=yes \
        bash_cv_getenv_redef=yes \
        bash_cv_func_strcoll_broken=no \
        bash_cv_wcontinued_broken=no \
        `# glibc 2.32 removed sys_siglist; bash must use strsignal()` \
        bash_cv_under_sys_siglist=no \
        bash_cv_sys_siglist=no \
        `# terminfo comes from ncurses; see base/recipes/ncurses` \
        bash_cv_termcap_lib=libncurses \
        ac_cv_func_mmap_fixed_mapped=yes \
        ac_cv_func_working_mktime=yes \
        gt_cv_int_divbyzero_sigfpe=yes

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    # No /bin/sh symlink here on purpose: that is dash's, and pointing it at
    # bash is how a distribution ends up with bashisms in its boot path.
}

trinix_check() {
    [ -x "$DESTDIR/usr/bin/bash" ] || { echo 'bash: /usr/bin/bash missing' >&2; return 1; }

    # A cross-built shell that cannot run a command is worth catching here
    # rather than from a serial console at first boot.
    local out
    out="$("$QEMU" -L "$SYSROOT" "$DESTDIR/usr/bin/bash" -c 'echo $((6*7))' 2>&1)" || {
        echo "bash: did not run under qemu-user: $out" >&2; return 1; }
    [ "$out" = '42' ] || { echo "bash: expected 42, got '$out'" >&2; return 1; }
}
