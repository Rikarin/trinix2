# shellcheck shell=bash
# readline — line editing for bash.
#
# In a distribution whose interactive shell is PowerShell this is a rescue-path
# dependency rather than a daily one: it exists so that the fallback shell is
# usable when pwsh will not start, which is exactly when a usable shell matters
# most.

RECIPE_SOURCE="readline"
RECIPE_DEPENDS="glibc-runtime ncurses"

trinix_build() {
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --disable-static \
        --with-curses

    # readline calls tgetent() and friends but does not link a termcap provider
    # into its own shared object unless told to. Left alone, the undefined
    # symbols surface only when something dlopens libreadline — so name the
    # library here and let the link fail now if ncurses is missing.
    make -j"$JOBS" SHLIB_LIBS='-lncursesw'
    make DESTDIR="$DESTDIR" SHLIB_LIBS='-lncursesw' install
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libreadline.so.8" ] \
        || { echo 'readline: libreadline.so.8 missing' >&2; return 1; }

    "$READELF" --dynamic "$DESTDIR/usr/lib/libreadline.so.8" | grep -q 'libncursesw' \
        || { echo 'readline: not linked against ncurses — tgetent will be undefined at runtime' >&2; return 1; }
}
