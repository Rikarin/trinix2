#!/usr/bin/env bash
# app-sanity.sh <appsdir> <trustdir> — the Phase 6 acceptance gate, minus the VM.
#
# The real exit criterion is an application installed and launched on a Trinix
# machine, and that needs QEMU. What can be checked here is everything about the
# artifact: that it verifies, and — more usefully — that each of the specific
# ways of breaking it is actually detected. A verifier that accepts everything
# passes the first test and fails every one of the others.
#
# Each tamper below is a real attack in miniature:
#   * change a file            — the signature covers content
#   * add a file               — the signature covers the file *set*
#   * remove a file            — likewise, in the other direction
#   * make a file executable   — the signature covers the execute bit
#   * re-sign under a foreign  — the trust store, not the signature, is what
#     root                       decides who may publish
#   * edit the image payload   — the .tdi's own signature, checked before it is
#                                ever handed to a kernel filesystem driver

set -euo pipefail

appsdir="${1:?usage: app-sanity.sh <appsdir> <trustdir>}"
trustdir="${2:?usage: app-sanity.sh <appsdir> <trustdir>}"
tool="${TRINIX_BUNDLE_TOOL:-/tmp/trinix-bundle-tool}/trinix-bundle"

[ -x "$tool" ] || { echo "app-sanity: no trinix-bundle at $tool" >&2; exit 1; }

failures=0
fail() { printf '  \033[1;31mFAIL\033[0m %s\n' "$*"; failures=$((failures + 1)); }
pass() { printf '  \033[1;32mok\033[0m   %s\n' "$*"; }
log()  { printf '\033[1;36m==> %s\033[0m\n' "$*"; }

# refuses <description> <command...> — the command must fail.
#
# `if ! cmd` rather than `cmd || fail`, because under errexit the second form
# is the one that works by accident and stops working when someone adds a pipe.
refuses() {
    local what="$1"; shift
    if "$@" > /dev/null 2>&1; then
        fail "$what was ACCEPTED"
    else
        pass "$what is refused"
    fi
}

log "Application bundles — $appsdir"

shopt -s nullglob
images=("$appsdir"/*.tdi)
shopt -u nullglob

[ "${#images[@]}" -gt 0 ] || { echo 'app-sanity: no .tdi images were built' >&2; exit 1; }

for image in "${images[@]}"; do
    name="$(basename "$image" .tdi)"
    app="$appsdir/$name.app"

    log "$name"

    if "$tool" verify "$image" --trust "$trustdir" > /dev/null; then
        pass "$name.tdi verifies against the trust store"
    else
        fail "$name.tdi does not verify"
        continue
    fi

    if "$tool" verify "$app" --trust "$trustdir" > /dev/null; then
        pass "$name.app verifies"
    else
        fail "$name.app does not verify"
        continue
    fi

    # Every tamper happens on a copy, so the artifact that gets shipped is the
    # one that was verified above rather than one that was put back afterwards.
    work="$(mktemp -d)"
    cp -a "$app" "$work/"
    copy="$work/$(basename "$app")"

    printf 'tampered\n' >> "$copy/Contents/Resources/greeting.txt"
    refuses 'a bundle with a modified file' "$tool" verify "$copy" --trust "$trustdir"

    rm -rf "$copy"; cp -a "$app" "$work/"
    printf 'extra\n' > "$copy/Contents/Resources/extra.txt"
    refuses 'a bundle with an added file' "$tool" verify "$copy" --trust "$trustdir"

    rm -rf "$copy"; cp -a "$app" "$work/"
    rm -f "$copy/Contents/Resources/greeting.txt"
    refuses 'a bundle with a removed file' "$tool" verify "$copy" --trust "$trustdir"

    rm -rf "$copy"; cp -a "$app" "$work/"
    chmod +x "$copy/Contents/Resources/greeting.txt"
    refuses 'a bundle whose data file became executable' "$tool" verify "$copy" --trust "$trustdir"

    rm -rf "$copy"; cp -a "$app" "$work/"
    rm -rf "$copy/Contents/_Signature"
    refuses 'an unsigned bundle' "$tool" verify "$copy" --trust "$trustdir"

    # A complete, valid signature from a certificate authority this system has
    # never heard of. This is the check that distinguishes "is it signed" from
    # "is it signed by someone we trust", and the two are not the same question.
    rm -rf "$copy"; cp -a "$app" "$work/"
    "$tool" pki init --certificate "$work/foreign-root.pub.pem" --key "$work/foreign-root.key.pem" \
        --name 'Untrusted Root' > /dev/null
    "$tool" pki identity --root-certificate "$work/foreign-root.pub.pem" --root-key "$work/foreign-root.key.pem" \
        --certificate "$work/foreign.pub.pem" --key "$work/foreign.key.pem" \
        --name 'Untrusted Developer' > /dev/null
    "$tool" seal "$copy" --certificate "$work/foreign.pub.pem" --key "$work/foreign.key.pem" \
        --architecture any > /dev/null
    refuses 'a bundle signed by an untrusted authority' "$tool" verify "$copy" --trust "$trustdir"

    # The image's own signature, which is what gets checked before a mount.
    # One byte inside the erofs payload, so the footer still parses and the
    # difference has to be caught by the hash rather than by the format.
    cp "$image" "$work/edited.tdi"
    printf 'X' | dd of="$work/edited.tdi" bs=1 seek=4096 conv=notrunc status=none
    refuses 'a distribution image with an edited payload' "$tool" verify "$work/edited.tdi" --trust "$trustdir"

    rm -rf "$work"
done

echo
[ "$failures" -eq 0 ] || { echo "app-sanity: $failures check(s) failed" >&2; exit 1; }
log "Application bundle sanity passed (${#images[@]} image(s))"
