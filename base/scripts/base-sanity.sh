#!/usr/bin/env bash
# base-sanity.sh <arm64|x86_64> — check the cross-built rootfs without booting it.
#
# The Phase 2 exit criterion is a boot to a login prompt, and that needs QEMU.
# This covers the failures that are cheap to detect and expensive to debug from
# a serial console: wrong machine type, a binary whose interpreter is missing,
# an unresolvable shared library.

# shellcheck source=../../toolchain/scripts/trinix-toolchain-lib.sh
. /usr/local/lib/trinix/scripts/trinix-toolchain-lib.sh

trinix_set_arch "${1:?usage: base-sanity.sh <arm64|x86_64>}"

ROOTFS="${TRINIX_ROOTFS:-/opt/trinix/rootfs}/$TARGET_TRIPLE"
failures=0

case "$TRINIX_ARCH" in
    arm64)  expect_machine='AArch64'; loader='ld-linux-aarch64.so.1' ;;
    x86_64) expect_machine='X86-64';  loader='ld-linux-x86-64.so.2'  ;;
esac

log "Base rootfs sanity — $TRINIX_ARCH ($ROOTFS)"

fail() { printf '  \033[1;31mFAIL\033[0m %s\n' "$*"; failures=$((failures + 1)); }
pass() { printf '  \033[1;32mok\033[0m   %s\n' "$*"; }

# --- Layout ---------------------------------------------------------------
log 'Filesystem layout'
for path in bin sbin lib usr/bin usr/lib etc; do
    [ -e "$ROOTFS/$path" ] && pass "$path" || fail "$path is missing"
done
[ -L "$ROOTFS/bin" ] && pass 'merged /usr (bin is a symlink)' || fail '/bin is not a symlink — merged /usr is broken'

# --- The dynamic loader ---------------------------------------------------
log 'Dynamic loader'
if [ -e "$ROOTFS/usr/lib/$loader" ]; then
    pass "$loader"
else
    fail "$loader missing — every dynamic binary would fail with a misleading 'No such file or directory'"
fi

# --- Every shipped ELF is the right machine, and its needs are satisfiable --
log 'Executables and libraries'

# An index of every shared object in the rootfs, by soname. Not just
# /usr/lib: systemd installs libsystemd-shared-257.so into /usr/lib/systemd and
# finds it through a RUNPATH, and a check that only looked in the default
# library path would call every systemd binary broken.
declare -A available=()
while IFS= read -r lib; do
    available["${lib##*/}"]=1
done < <(find "$ROOTFS" -name '*.so' -o -name '*.so.*')

checked=0
while IFS= read -r binary; do
    # Only ELF objects. The obvious test — "did readelf produce output" — was
    # true for far too much once .NET arrived: a managed assembly is a PE/COFF
    # file, llvm-readelf parses those too, and every one of them reports the
    # i386 machine type that the CLI header has carried since 2002. They are
    # architecture-neutral IL and identical on both arches, so reading four
    # magic bytes is both cheaper and more accurate than asking a parser.
    [ "$(head -c 4 "$binary" | od -An -tx1 | tr -d ' \n')" = '7f454c46' ] || continue

    header="$(llvm-readelf --file-header "$binary" 2>/dev/null || true)"
    [ -n "$header" ] || continue
    checked=$((checked + 1))

    machine="$(printf '%s\n' "$header" | awk -F: '/Machine:/ {gsub(/^ +/, "", $2); print $2}')"
    case "$machine" in
        *"$expect_machine"*) ;;
        *) fail "${binary#"$ROOTFS"}: built for '$machine', expected $expect_machine"; continue ;;
    esac

    # Every NEEDED library must exist in the rootfs. This is what catches a
    # recipe that linked against something only present in the build container.
    while IFS= read -r needed; do
        [ -n "$needed" ] || continue
        [ -n "${available[$needed]:-}" ] \
            || fail "${binary#"$ROOTFS"}: needs $needed, which is not in the rootfs"
    done < <(llvm-readelf --dynamic "$binary" 2>/dev/null | awk '/NEEDED/ {print $NF}' | tr -d '[]' || true)
done < <(find "$ROOTFS" \( -type f -perm -u+x \) -o \( -type f -name '*.so*' \) | sort)
pass "$checked ELF object(s) are $expect_machine with all NEEDED libraries present"

# --- /bin/sh actually runs ------------------------------------------------
log 'Shell'
if [ -e "$ROOTFS/usr/bin/sh" ]; then
    if out="$("$QEMU" -L "$ROOTFS" "$ROOTFS/usr/bin/sh" -c 'echo shell-works' 2>&1)" \
       && [ "$out" = 'shell-works' ]; then
        pass '/bin/sh runs in the rootfs'
    else
        fail "/bin/sh did not run: $out"
    fi
else
    fail '/bin/sh is missing'
fi

# --- The boot path, end to end -------------------------------------------
# These are the assertions no single recipe can make, because each one spans
# two of them: the shell named in /etc/passwd is installed by a different
# recipe from the one that names it, and the kernel knows nothing about the
# init it will exec.
log 'Boot path'
for essential in usr/lib/systemd/systemd usr/bin/login usr/bin/agetty \
                 usr/lib/trinix/vmlinuz etc/fstab etc/passwd etc/shadow; do
    [ -e "$ROOTFS/$essential" ] && pass "$essential" || fail "$essential is missing"
done

if [ -e "$ROOTFS/etc/passwd" ]; then
    root_shell="$(awk -F: '$1 == "root" { print $7 }' "$ROOTFS/etc/passwd")"
    if [ -x "$ROOTFS$root_shell" ]; then
        pass "root's shell ($root_shell) is installed"
    else
        fail "root's shell is $root_shell, which is not in the rootfs — login would succeed and then fail"
    fi
fi

# /var, /home and /root are symlinks onto the writable partition. If one of
# them is a real directory, some recipe installed into it and that content will
# be shadowed the moment /data is mounted.
for link in var home root; do
    if [ -L "$ROOTFS/$link" ]; then
        pass "/$link points at $(readlink "$ROOTFS/$link")"
    else
        fail "/$link is not a symlink onto /data — it would be shadowed at boot"
    fi
done

# --- The graphical session ------------------------------------------------
# The same kind of assertion, one layer up: the compositor is published by the
# C# stage, its native half is built by a recipe, the seat comes from a third,
# and the keymap database from a fourth. Nothing but this notices when one of
# the four is missing, and the symptom in a VM is a black screen.
log 'Graphical session'
for essential in usr/lib/trinix/compositor/trinix-compositor \
                 usr/lib/libtrinix-wlr.so.1 \
                 usr/lib/libwlroots-0.19.so \
                 usr/bin/seatd \
                 usr/bin/trinix-wl-demo \
                 usr/lib/systemd/system/trinix-compositor.service \
                 usr/lib/systemd/system/seatd.service; do
    [ -e "$ROOTFS/$essential" ] && pass "$essential" || fail "$essential is missing"
done

# libxkbcommon has the config root compiled in; if the database is not at that
# path, every key produces a keycode and no keysym.
#
# Both halves, and separately, because /usr/share/X11/xkb is a symlink with an
# absolute target — following it from here would resolve against the build
# container's root, where it is not.
if [ -e "$ROOTFS/usr/share/xkeyboard-config-2/symbols/us" ]; then
    pass 'the XKB keymap database is installed'
else
    fail 'the XKB symbols are missing — no key would produce a character'
fi
if [ "$(readlink "$ROOTFS/usr/share/X11/xkb" 2>/dev/null)" = '/usr/share/xkeyboard-config-2' ]; then
    pass '/usr/share/X11/xkb points at it'
else
    fail '/usr/share/X11/xkb does not point at the keymap database libxkbcommon was built for'
fi

# The compositor runs as `trinix`, and its only privilege is membership of the
# group seatd hands devices to. Without the group it starts and finds no seat.
if awk -F: '$1 == "_seatd"' "$ROOTFS/etc/group" 2>/dev/null | grep -q 'trinix'; then
    pass 'the trinix user is in _seatd'
else
    fail 'the trinix user is not in the _seatd group — the compositor would get no devices'
fi

# --- The application format -----------------------------------------------
# Three things assembled by three different stages, and an image missing any one
# of them boots to a system that cannot install or launch an application while
# looking perfectly healthy: the tools come from the C# publish stage, the trust
# store from a build context, and the reference application from the app stage.
log 'Applications'
for essential in usr/lib/trinix/bundle/trinix-bundle \
                 usr/lib/trinix/bundle/trinix-open \
                 usr/lib/trinix/bundle/Trinix.Bundle.dll; do
    [ -e "$ROOTFS/$essential" ] && pass "$essential" || fail "$essential is missing"
done

# The symlinks are what put the two on PATH; -L rather than -e, because they
# point at absolute paths that resolve against the build container's root.
for link in usr/bin/trinix-bundle usr/bin/trinix-open; do
    [ -L "$ROOTFS/$link" ] && pass "$link" || fail "$link is not a symlink onto /usr/lib/trinix/bundle"
done

# An empty trust store is the failure this section exists for. The launcher
# treats it as a broken system rather than as a rejected application, but only
# after someone has booted the image to find out.
# `|| true` on both counts, and it is load-bearing. This script runs under
# `set -euo pipefail`, and find exits non-zero when the directory is missing —
# which is exactly the case being checked for. Without it the assignment fails,
# errexit aborts, and the check that was supposed to report a missing trust
# store instead reports nothing at all.
roots="$(find "$ROOTFS/usr/share/trinix/pki/roots" -name '*.pem' 2>/dev/null | wc -l || true)"
if [ "$roots" -gt 0 ]; then
    pass "the trust store carries $roots root certificate(s)"
else
    fail 'the trust store is empty — no application could ever be launched'
fi

images="$(find "$ROOTFS/usr/share/trinix/applications" -name '*.tdi' 2>/dev/null | wc -l || true)"
if [ "$images" -gt 0 ]; then
    pass "$images distribution image(s) shipped in /usr/share/trinix/applications"
else
    fail 'no .tdi was shipped — the app stage did not run before this one'
fi

echo
[ "$failures" -eq 0 ] || die "$failures rootfs check(s) failed for $TRINIX_ARCH"
log "Base rootfs sanity passed for $TRINIX_ARCH ($(du -sh "$ROOTFS" | cut -f1))"
