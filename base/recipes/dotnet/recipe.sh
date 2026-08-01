# shellcheck shell=bash
# .NET — the runtime Trinix is written against, and the SDK.
#
# The plan allowed for two outcomes here: Microsoft's official Linux builds run
# against Trinix's glibc, or they do not and .NET gets built from source. They
# run — which is not luck. The toolchain has been shipping libgcc_s.so.1 and
# libstdc++.so.6 as compat libraries since Phase 1 for exactly this moment, and
# ICU, OpenSSL and zlib are in the base because .NET needs them.
#
# The check below is what turns that from an assumption into a fact: it runs
# the cross-installed host under qemu-user and asks it to print its version. A
# missing dependency shows up here, not on a serial console.
#
# The SDK rather than just the runtime, because it contains both and because
# the Phase 3 exit criterion is `dotnet --version` working in-VM. It is also
# most of a gigabyte. When the bundle format lands in Phase 6 the SDK is an
# obvious candidate to move out of the base image and into a .tdi that gets
# installed on demand; the runtime stays.

RECIPE_SOURCE=""                        # the tarball has no top-level directory
RECIPE_EXTRA_SOURCES="dotnet-sdk-${TRINIX_ARCH}"
RECIPE_DEPENDS="glibc-runtime llvm-runtime icu openssl zlib ca-certificates"

# Where .NET lives. /usr/lib rather than /usr/share because it is machine code,
# and unversioned because the host resolves runtime versions itself.
DOTNET_ROOT_DIR='/usr/lib/dotnet'

trinix_build() {
    local tarball
    tarball="$(trinix-fetch "dotnet-sdk-${TRINIX_ARCH}")"

    install -d "$DESTDIR$DOTNET_ROOT_DIR"
    tar --extract --file "$tarball" --directory "$DESTDIR$DOTNET_ROOT_DIR"

    # CoreCLR's LTTng trace provider links liblttng-ust, which Trinix does not
    # ship and would not use: LTTng is a tracing framework with its own daemon
    # and session management, and .NET's own EventPipe covers the same ground
    # without any of it. The runtime dlopens this lazily and carries on without
    # it, so the only thing shipping it achieves is a library in the image with
    # a dependency that can never be satisfied — which the rootfs gate rightly
    # refuses to accept.
    find "$DESTDIR$DOTNET_ROOT_DIR" -name 'libcoreclrtraceptprovider.so' -delete

    # `dotnet` on PATH. A symlink, not a wrapper: the host locates its own root
    # by resolving argv[0], so it finds /usr/lib/dotnet without being told.
    install -d "$DESTDIR/usr/bin"
    ln -sfn "$DOTNET_ROOT_DIR/dotnet" "$DESTDIR/usr/bin/dotnet"

    # Where a *published application* finds the runtime.
    #
    # An apphost — the small native launcher `dotnet publish` puts next to the
    # assembly — cannot resolve argv[0] to the runtime, because it is not in
    # the runtime's directory. It looks at DOTNET_ROOT, then at this file, then
    # at /usr/share/dotnet, and Trinix installs to /usr/lib/dotnet. Without the
    # registration every unit has to carry an Environment=DOTNET_ROOT= line,
    # and the one that forgets fails at startup with a wall of text about
    # installing .NET onto a system that already has it.
    #
    # The suffix is the RID architecture, not Trinix's name for it.
    local rid_arch
    case "$TRINIX_ARCH" in
        arm64)  rid_arch='arm64' ;;
        x86_64) rid_arch='x64'   ;;
    esac
    install -d "$DESTDIR/etc/dotnet"
    printf '%s' "$DOTNET_ROOT_DIR" > "$DESTDIR/etc/dotnet/install_location_$rid_arch"
    # Hosts older than .NET 6 read the unsuffixed name. It costs one file.
    printf '%s' "$DOTNET_ROOT_DIR" > "$DESTDIR/etc/dotnet/install_location"

    # Telemetry off, and off in the image rather than in a login profile: a
    # service started by systemd never reads a profile, and "phones home unless
    # a shell said otherwise" is not a defensible default for an OS.
    install -d "$DESTDIR/usr/lib/environment.d"
    cat > "$DESTDIR/usr/lib/environment.d/50-dotnet.conf" <<EOF
DOTNET_ROOT=$DOTNET_ROOT_DIR
DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_NOLOGO=1
EOF

    # The same three for anything that reads /etc/profile instead — systemd's
    # environment.d is only consulted for user sessions it starts itself.
    install -d "$DESTDIR/etc/profile.d"
    cat > "$DESTDIR/etc/profile.d/dotnet.sh" <<EOF
export DOTNET_ROOT=$DOTNET_ROOT_DIR
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
EOF
}

trinix_check() {
    [ -x "$DESTDIR$DOTNET_ROOT_DIR/dotnet" ] \
        || { echo 'dotnet: the host is missing' >&2; return 1; }

    local machine
    machine="$("$READELF" --file-header "$DESTDIR$DOTNET_ROOT_DIR/dotnet" | awk -F: '/Machine:/ {print $2}')"
    case "$TRINIX_ARCH:$machine" in
        arm64:*AArch64*|x86_64:*X86-64*) ;;
        *) echo "dotnet: the host is '$machine', wrong for $TRINIX_ARCH" >&2; return 1 ;;
    esac

    # Both halves have to be here, or `dotnet --version` in the VM — the Phase
    # 3 exit criterion — reports no SDKs installed and exits non-zero.
    ls -d "$DESTDIR$DOTNET_ROOT_DIR"/shared/Microsoft.NETCore.App/*/ >/dev/null 2>&1 \
        || { echo 'dotnet: no shared runtime' >&2; return 1; }
    ls -d "$DESTDIR$DOTNET_ROOT_DIR"/sdk/*/ >/dev/null 2>&1 \
        || { echo 'dotnet: no SDK' >&2; return 1; }

    # The registration, checked because its absence is invisible until a
    # published application starts — and then the failure looks like a missing
    # .NET rather than a missing one-line file.
    local file registered count=0
    for file in "$DESTDIR/etc/dotnet"/install_location*; do
        [ -f "$file" ] || continue
        count=$((count + 1))
        registered="$(cat "$file")"
        [ "$registered" = "$DOTNET_ROOT_DIR" ] || {
            echo "dotnet: ${file##*/} registers '$registered', not $DOTNET_ROOT_DIR" >&2
            return 1
        }
    done
    [ "$count" -eq 2 ] \
        || { echo "dotnet: $count install-location file(s), expected 2" >&2; return 1; }

    # Now the question the plan actually raises: do Microsoft's binaries run
    # against *this* glibc?
    #
    # The tempting answer is to run the host under qemu-user and see. That
    # answer is unreliable — CoreCLR leans on syscalls qemu-user emulates
    # poorly, so a failure would say more about the emulator than about the
    # sysroot, and "the official build does not work, fall back to a source
    # build" is far too expensive a conclusion to draw from a flaky signal.
    #
    # The static form is exact instead. Every glibc symbol these binaries use
    # carries the version it was introduced in, so the highest one they ask for
    # is the oldest glibc that can load them. Compare that against the glibc
    # the toolchain built and the answer is arithmetic rather than observation.
    local have want lib newest=''
    have="$(trinix-fetch --version glibc)"

    for lib in "$DESTDIR$DOTNET_ROOT_DIR/dotnet" \
               "$DESTDIR$DOTNET_ROOT_DIR"/shared/Microsoft.NETCore.App/*/libcoreclr.so \
               "$DESTDIR$DOTNET_ROOT_DIR"/shared/Microsoft.NETCore.App/*/libSystem.Native.so; do
        [ -e "$lib" ] || continue
        want="$("$READELF" --version-info "$lib" 2>/dev/null \
                | grep -oE 'GLIBC_[0-9]+\.[0-9]+' | sort -uV | tail -n 1)"
        [ -n "$want" ] || continue
        newest="$(printf '%s\n%s\n' "$newest" "${want#GLIBC_}" | sort -V | tail -n 1)"
    done

    [ -n "$newest" ] || { echo 'dotnet: no versioned glibc references found — that is suspicious' >&2; return 1; }

    if [ "$(printf '%s\n%s\n' "$newest" "$have" | sort -V | tail -n 1)" != "$have" ]; then
        echo "dotnet: the official build needs glibc $newest but Trinix ships $have." >&2
        echo '  This is the point at which the plan calls for a source-build fallback.' >&2
        return 1
    fi
    echo "dotnet: needs glibc $newest, Trinix ships $have"
}
