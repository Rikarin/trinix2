# shellcheck shell=bash
# The CA trust store.
#
# Mozilla's root list, as extracted by the curl project — the same set every
# other distribution ends up shipping, obtained without running Mozilla's
# certdata parser at build time.
#
# Nothing in Phase 2 needed this. Phase 3 does: .NET validates TLS chains
# through OpenSSL, which looks in the directory this installs into, and Phase
# 7's package manager fetches over HTTPS.
#
# The pin is a dated URL rather than the floating cacert.pem, whose contents
# change under a fixed address — a trust store is the last thing that should be
# able to change without the digest noticing.

RECIPE_SOURCE=""                        # a single .pem, not an archive
RECIPE_EXTRA_SOURCES="cacert"
RECIPE_DEPENDS="glibc-runtime openssl"

trinix_build() {
    local bundle
    bundle="$(trinix-fetch cacert)"

    # /etc/ssl/certs is where the openssl recipe's --openssldir puts its
    # certificate directory, and ca-certificates.crt is the filename .NET,
    # curl and everything else look for.
    install -d "$DESTDIR/etc/ssl/certs"
    install -m644 "$bundle" "$DESTDIR/etc/ssl/certs/ca-certificates.crt"

    # OpenSSL's default CAfile is <openssldir>/cert.pem; the same bundle under
    # the name each library expects, rather than two copies of 200 kB.
    ln -sfn certs/ca-certificates.crt "$DESTDIR/etc/ssl/cert.pem"

    # No c_rehash symlink farm. That exists so a library can find one
    # certificate by subject hash without reading the whole bundle; everything
    # in this image reads the bundle, and the farm would be 150 symlinks that
    # have to stay in step with it.
}

trinix_check() {
    local store="$DESTDIR/etc/ssl/certs/ca-certificates.crt"
    [ -s "$store" ] || { echo 'ca-certificates: the trust store is missing or empty' >&2; return 1; }

    # A truncated download that still hashed correctly is not possible, but a
    # store with no certificates in it is a silent TLS failure everywhere, so
    # it is worth one grep to know the file is what it claims to be.
    local count
    count="$(grep -c 'BEGIN CERTIFICATE' "$store")"
    [ "$count" -ge 100 ] \
        || { echo "ca-certificates: only $count certificates in the store — that cannot be right" >&2; return 1; }
    echo "ca-certificates: $count roots"
}
