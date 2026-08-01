# shellcheck shell=bash
# libxcrypt — crypt(3).
#
# glibc removed libcrypt upstream, so on a from-scratch system nothing provides
# crypt() until this is built. systemd links it unconditionally, and PAM and
# shadow will need it when real authentication replaces the current autologin.
#
# Built without the obsolete API: that exists to keep binaries linked against
# glibc's ancient DES-based libcrypt.so.1 working. Trinix has no such binaries —
# nothing predates this tree — so shipping the legacy ABI and its weak hashes
# would be carrying compatibility for a past that does not exist here.

RECIPE_SOURCE="libxcrypt"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    # The second LLD-versus-GNU-ld difference in the tree, and the same root
    # cause as zlib's (see base/recipes/README.md). libxcrypt's libcrypt.map
    # declares the whole XCRYPT_2.0 version node — xcrypt, xcrypt_r,
    # xcrypt_gensalt and friends — unconditionally, but those symbols only
    # exist when the obsolete API is compiled in. GNU ld ignores a version
    # script entry naming a symbol that is not defined; LLD makes it an error.
    #
    # So the choice is between shipping the legacy DES ABI purely to satisfy a
    # linker check, or restoring the GNU behaviour for this one link. The
    # latter, for the reason in the header comment: there are no old binaries
    # here to be compatible with.
    "$SRCDIR/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --disable-static \
        --disable-obsolete-api \
        --disable-failure-tokens \
        LDFLAGS="-Wl,--undefined-version"

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    rm -f "$DESTDIR"/usr/lib/*.la
}

trinix_check() {
    [ -e "$DESTDIR/usr/lib/libcrypt.so.2" ] \
        || { echo 'libxcrypt: libcrypt.so.2 missing' >&2; return 1; }
    [ -e "$DESTDIR/usr/include/crypt.h" ] \
        || { echo 'libxcrypt: crypt.h missing — systemd will not configure' >&2; return 1; }
}
