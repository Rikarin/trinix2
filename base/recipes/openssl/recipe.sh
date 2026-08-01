# shellcheck shell=bash
# OpenSSL — TLS and the crypto primitives underneath it.
#
# Nothing in the boot path needs this. It is in the base image for what comes
# after: .NET's System.Security.Cryptography is a thin layer over libcrypto on
# Linux, so Phase 3 fails immediately without it, and Phase 7's package manager
# fetches over HTTPS.
#
# Note what this is *not* used for: bundle and image signing (Phase 6) is pure
# C# over .NET's own X.509 and CMS implementations, deliberately, so that the
# security-critical policy layer is one language and one review.

RECIPE_SOURCE="openssl"
RECIPE_DEPENDS="glibc-runtime"

trinix_build() {
    # OpenSSL predates autotools conventions and has its own target names.
    # Cross-compilation is driven by $CC, which already carries --target and
    # --sysroot, so no --cross-compile-prefix is needed.
    local ossl_target
    case "$TRINIX_ARCH" in
        arm64)  ossl_target='linux-aarch64' ;;
        x86_64) ossl_target='linux-x86_64'  ;;
    esac

    "$SRCDIR/Configure" "$ossl_target" \
        --prefix=/usr \
        --openssldir=/etc/ssl \
        --libdir=lib \
        shared \
        no-tests \
        no-docs \
        `# Protocols and ciphers nothing should still be speaking. Each one` \
        `# removed is code that cannot be reached by a downgrade attack.` \
        no-ssl3 \
        no-ssl3-method \
        no-weak-ssl-ciphers \
        no-comp \
        enable-ktls

    make -j"$JOBS"

    # install_sw is the libraries, headers and the openssl(1) tool;
    # install_ssldirs creates /etc/ssl and drops openssl.cnf. The full `install`
    # target additionally writes man pages that no-docs already declined.
    make DESTDIR="$DESTDIR" install_sw install_ssldirs

    # The engines/modules directory is where a FIPS provider would go; empty
    # here, and an empty directory in an immutable image is just noise.
    find "$DESTDIR/usr/lib" -type d -empty -delete
}

trinix_check() {
    local missing=''
    for required in usr/lib/libssl.so.3 usr/lib/libcrypto.so.3 usr/bin/openssl; do
        [ -e "$DESTDIR/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "openssl: missing$missing" >&2; return 1; }
}
