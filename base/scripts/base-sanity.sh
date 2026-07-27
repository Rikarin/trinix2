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
checked=0
while IFS= read -r binary; do
    # readelf failing is how a non-ELF file (a script, a config) is recognised;
    # `|| true` keeps that from tripping pipefail and aborting the whole check.
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
        [ -e "$ROOTFS/usr/lib/$needed" ] \
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

echo
[ "$failures" -eq 0 ] || die "$failures rootfs check(s) failed for $TRINIX_ARCH"
log "Base rootfs sanity passed for $TRINIX_ARCH ($(du -sh "$ROOTFS" | cut -f1))"
