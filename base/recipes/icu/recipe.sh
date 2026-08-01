# shellcheck shell=bash
# ICU — Unicode and globalization data.
#
# Here for one reason: .NET's globalization support is ICU, and the runtime
# refuses to start without it unless invariant mode is forced. Trinix's own
# assemblies *are* invariant (see src/Directory.Build.props) precisely so the
# boot path does not depend on a 30 MB locale database — but the SDK is not,
# and neither is PowerShell, so the base image ships ICU anyway.
#
# It is also the largest single library in the base by some margin. The data is
# the bulk of it, and trimming it with ICU's filtering tools is a worthwhile
# exercise for the day the image size matters more than the certainty that
# every locale a user asks for is present.

RECIPE_SOURCE="icu"
RECIPE_DEPENDS="glibc-runtime llvm-runtime"

trinix_build() {
    # ICU's data is compiled by tools built from the same source, and those
    # tools have to run on the build machine. Cross-compiling therefore means
    # building ICU twice: once natively to get the tools, then once for the
    # target with --with-cross-build pointing at the first. This is upstream's
    # documented approach, not a workaround.
    local hostdir="$BUILDDIR/build-host"
    mkdir -p "$hostdir"
    (
        cd "$hostdir"
        CC=gcc CXX=g++ AR=ar RANLIB=ranlib \
        "$SRCDIR/source/configure" \
            --disable-tests --disable-samples --disable-extras >/dev/null
        make -j"$JOBS" >/dev/null
    )

    "$SRCDIR/source/configure" \
        --host="$TARGET_TRIPLE" \
        --prefix=/usr \
        --with-cross-build="$hostdir" \
        --disable-tests \
        --disable-samples \
        --disable-extras \
        --enable-static=no \
        --enable-shared

    make -j"$JOBS"
    make DESTDIR="$DESTDIR" install

    # icu-config is the same cross-compilation trap as ncurses' *-config
    # scripts: a script that answers questions about the *target* while sitting
    # on the build machine's PATH. pkg-config files describe the same thing and
    # the driver already scopes those to the sysroot.
    rm -f "$DESTDIR/usr/bin/icu-config"
}

trinix_check() {
    local missing='' version=77
    # .NET dlopens these by soname with the major version appended, so the
    # numbers matter: a bump that changes them will make the runtime fall back
    # to invariant mode silently rather than fail loudly.
    for required in "libicuuc.so.$version" "libicui18n.so.$version" "libicudata.so.$version"; do
        [ -e "$DESTDIR/usr/lib/$required" ] || missing="$missing $required"
    done
    [ -z "$missing" ] || { echo "icu: missing$missing" >&2; return 1; }
}
