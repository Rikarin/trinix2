# shellcheck shell=bash
# dash — /bin/sh.
#
# PowerShell is the user-facing shell; this is the one that thousands of build
# scripts, systemd generators and third-party tools invoke via #!/bin/sh without
# asking. It is small, fast to start, and POSIX-correct, which is the entire job
# description.
#
# Also the first autotools recipe, so it establishes the --host cross pattern
# the rest of the base set follows.

RECIPE_SOURCE="dash"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --bindir=/usr/bin \
        --mandir=/usr/share/man \
        --disable-static

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    # /bin is a symlink to usr/bin, so this lands at the /bin/sh every script
    # in the world hardcodes.
    ln -sfn dash "$DESTDIR/usr/bin/sh"
}

trinix_check() {
    [ -L "$DESTDIR/usr/bin/sh" ] || { echo 'dash: /bin/sh symlink missing' >&2; return 1; }

    # Actually run it. A cross-built shell that cannot execute a command is a
    # far more useful thing to catch here than at first boot.
    "$QEMU" -L "$SYSROOT" "$DESTDIR/usr/bin/dash" -c 'exit 42' && rc=0 || rc=$?
    [ "$rc" -eq 42 ] || { echo "dash: expected exit 42 from the cross-built shell, got $rc" >&2; return 1; }
}
