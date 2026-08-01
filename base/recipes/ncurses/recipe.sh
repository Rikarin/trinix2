# shellcheck shell=bash
# ncurses — terminfo, and the termcap library readline needs.
#
# Two things come out of this recipe and only one of them is a library. The
# other is the terminfo database, which is what tells anything running on the
# serial console how to clear a line or move the cursor. PowerShell's console
# host in Phase 3 reads terminfo directly, so this is on the path to the
# distro's actual shell, not just to bash.
#
# The database upstream ships describes some 2500 terminals, essentially all of
# them extinct. Trinix keeps the handful that a VM, a serial console or a
# terminal emulator will actually claim to be — see the prune list below.

RECIPE_SOURCE="ncurses"
RECIPE_DEPENDS="glibc-runtime"

# Terminals a Trinix system can plausibly meet: the kernel's own console, a
# serial line, QEMU's, and what a modern terminal emulator sets $TERM to.
TERMINFO_KEEP='ansi dumb linux screen screen-256color tmux tmux-256color vt100 vt102 vt220 xterm xterm-color xterm-256color'

trinix_build() {
    # `tic` compiles the terminfo source into the database, and `make install`
    # runs it — so it has to be a *build machine* binary while everything else
    # here is cross-compiled. Configuring a second, native tree solely to
    # produce it is the standard answer; the alternative, running the
    # cross-built tic under qemu-user, drags the emulator into the install path
    # of every future ncurses bump for no gain.
    local hostdir="$BUILDDIR/build-host"
    mkdir -p "$hostdir"
    (
        cd "$hostdir"
        CC=gcc CXX=g++ AR=ar RANLIB=ranlib STRIP=strip \
        "$SRCDIR/configure" --without-shared --without-cxx --without-cxx-binding \
                            --without-ada --without-manpages --without-tests AWK=gawk >/dev/null
        make -C include
        make -C progs tic
    )

    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --with-shared \
        --without-normal \
        --without-debug \
        --without-ada \
        --without-cxx-binding \
        --without-tests \
        --without-manpages \
        --enable-widec \
        --enable-pc-files \
        --with-pkg-config-libdir=/usr/lib/pkgconfig \
        --disable-stripping \
        --enable-symlinks \
        AWK=gawk \
        TIC_PATH="$hostdir/progs/tic"

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" TIC_PATH="$hostdir/progs/tic" install

    # Trim the database. Entries live in /usr/share/terminfo/<first-letter>/<name>,
    # so this walks the tree and keeps only what the list above names.
    local keep_re
    # shellcheck disable=SC2086  # deliberate word splitting: one term per line
    keep_re="^($(printf '%s\n' $TERMINFO_KEEP | paste -sd'|' -))\$"
    find "$DESTDIR/usr/share/terminfo" \( -type f -o -type l \) | while IFS= read -r entry; do
        printf '%s\n' "${entry##*/}" | grep -Eq "$keep_re" || rm -f "$entry"
    done
    find "$DESTDIR/usr/share/terminfo" -type d -empty -delete

    # Everything Trinix builds uses the wide-character library; the plain name
    # exists because third-party binaries (and .NET's terminal support, when it
    # is linked rather than reading terminfo itself) ask for libncurses.so.6.
    # The soname symlink is what those binaries load; the bare .so is what
    # `-lncurses` resolves to at link time — bash's configure insists on
    # spelling it that way.
    ln -sfn libncursesw.so.6 "$DESTDIR/usr/lib/libncurses.so.6"
    ln -sfn libncursesw.so   "$DESTDIR/usr/lib/libncurses.so"

    rm -f "$DESTDIR"/usr/lib/*.a
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libncursesw.so.6" ] \
        || { echo 'ncurses: libncursesw.so.6 missing' >&2; return 1; }

    # A database with no linux entry means a blank or garbled serial console,
    # which is precisely the thing Phase 2 has to demonstrate working.
    [ -e "$DESTDIR/usr/share/terminfo/l/linux" ] \
        || { echo 'ncurses: terminfo has no `linux` entry — the console will misbehave' >&2; return 1; }
}
