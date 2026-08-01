# shellcheck shell=bash
# PowerShell — the interactive shell.
#
# This is the one that makes Trinix Trinix. dash stays /bin/sh for the scripts
# that hardcode it and bash stays the rescue shell, but a user who logs in
# lands here.
#
# Self-contained rather than framework-dependent, even though the base image
# has a perfectly good shared runtime a few directories away. pwsh has to start
# when the shared runtime is what is broken — which is exactly the moment
# someone needs a shell — and 70 MB is a cheap price for a shell that does not
# share a failure mode with the thing it is being used to repair.

RECIPE_SOURCE=""                        # the tarball has no top-level directory
RECIPE_EXTRA_SOURCES="powershell-${TRINIX_ARCH}"
RECIPE_DEPENDS="glibc-runtime llvm-runtime icu openssl ca-certificates ncurses"

PWSH_DIR='/usr/lib/powershell'

trinix_build() {
    local tarball
    tarball="$(trinix-fetch "powershell-${TRINIX_ARCH}")"

    install -d "$DESTDIR$PWSH_DIR"
    tar --extract --file "$tarball" --directory "$DESTDIR$PWSH_DIR"
    chmod 755 "$DESTDIR$PWSH_DIR/pwsh"

    # Self-contained means pwsh carries its own copy of CoreCLR, and therefore
    # its own copy of the LTTng trace provider that Trinix does not ship a
    # tracing framework for. See the dotnet recipe.
    find "$DESTDIR$PWSH_DIR" -name 'libcoreclrtraceptprovider.so' -delete

    install -d "$DESTDIR/usr/bin"
    ln -sfn "$PWSH_DIR/pwsh" "$DESTDIR/usr/bin/pwsh"

    # PowerShell's own telemetry, off for the same reason .NET's is: a login
    # shell that reports usage by default is not a defensible OS default.
    install -d "$DESTDIR/usr/lib/environment.d"
    printf 'POWERSHELL_TELEMETRY_OPTOUT=1\nPOWERSHELL_UPDATECHECK=Off\n' \
        > "$DESTDIR/usr/lib/environment.d/50-powershell.conf"

    # Modules that ship with the system rather than with a user. Trinix's own
    # module is installed here by the trinix-dotnet recipe.
    install -d "$DESTDIR$PWSH_DIR/Modules"
}

trinix_check() {
    [ -x "$DESTDIR$PWSH_DIR/pwsh" ] || { echo 'powershell: pwsh is missing' >&2; return 1; }

    local machine
    machine="$("$READELF" --file-header "$DESTDIR$PWSH_DIR/pwsh" | awk -F: '/Machine:/ {print $2}')"
    case "$TRINIX_ARCH:$machine" in
        arm64:*AArch64*|x86_64:*X86-64*) ;;
        *) echo "powershell: pwsh is '$machine', wrong for $TRINIX_ARCH" >&2; return 1 ;;
    esac

    # Self-contained means the runtime is in the same directory. If it is not,
    # the tarball was the framework-dependent variant and pwsh will fail to
    # start on a system that has no shared runtime — which is the scenario this
    # recipe exists to survive.
    [ -e "$DESTDIR$PWSH_DIR/libcoreclr.so" ] \
        || { echo 'powershell: no runtime alongside pwsh — that is the fx-dependent tarball' >&2; return 1; }
}
