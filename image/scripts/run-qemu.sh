#!/usr/bin/env bash
# run-qemu <arm64|x86_64> [--check [seconds]] [extra qemu arguments...]
#
# Boots a Trinix disk image. Runs inside the vm container (docker/vm.Dockerfile),
# with the image directory bind-mounted at /images.
#
# Two modes:
#   interactive  serial console on stdio — this is `run-vm.ps1 -Arch arm64`
#   --check      boot unattended, capture the console, and assert that a login
#                prompt appeared. This is the Phase 2 exit criterion as a test.

set -euo pipefail

arch="${1:?usage: run-qemu <arm64|x86_64> [--check [seconds]] [qemu args...]}"
shift

check=0
timeout_s=0
if [ "${1:-}" = '--check' ]; then
    check=1
    shift
    case "${1:-}" in
        ''|-*) ;;
        *) timeout_s="$1"; shift ;;
    esac
fi

images="${TRINIX_IMAGES:-/images}"
image="$images/trinix-$arch.img"
[ -f "$image" ] || { echo "run-qemu: no image at $image" >&2; exit 1; }

# The firmware's variable store has to be writable, and the image ships
# read-only — so every boot starts from a fresh copy. That also means no boot
# entry survives a reboot, which is why the ESP carries the removable-media
# fallback path (see image/scripts/build-image.sh).
vars="$(mktemp -d)/vars.fd"

die() { echo "run-qemu: $*" >&2; exit 1; }

# Firmware paths move between Debian releases (OVMF_CODE.fd became
# OVMF_CODE_4M.fd, AAVMF lives in two places), so each is looked up rather than
# hardcoded — and a miss is reported as a missing package, not as an empty
# variable that QEMU turns into something less obvious three lines later.
first_existing() {
    local candidate
    for candidate in "$@"; do
        [ -f "$candidate" ] && { printf '%s' "$candidate"; return 0; }
    done
    return 1
}

case "$arch" in
    arm64)
        qemu=qemu-system-aarch64
        code="$(first_existing /usr/share/AAVMF/AAVMF_CODE.fd /usr/share/qemu-efi-aarch64/QEMU_EFI.fd)" \
            || die 'no arm64 UEFI firmware found — is qemu-efi-aarch64 installed?'
        vars_template="$(first_existing /usr/share/AAVMF/AAVMF_VARS.fd)" \
            || die 'no arm64 UEFI variable store found — is qemu-efi-aarch64 installed?'
        # cortex-a76, not the more usual a72: Trinix's userland is compiled
        # with -march=armv8.2-a and the a72 is an 8.0 core, so half the base
        # system would take an illegal instruction on the first boot.
        machine=(-machine virt -cpu cortex-a76)
        default_timeout=420
        ;;
    x86_64)
        qemu=qemu-system-x86_64
        code="$(first_existing /usr/share/OVMF/OVMF_CODE_4M.fd /usr/share/OVMF/OVMF_CODE.fd /usr/share/ovmf/OVMF.fd)"
        vars_template="$(first_existing /usr/share/OVMF/OVMF_VARS_4M.fd /usr/share/OVMF/OVMF_VARS.fd)"
        # -cpu max for the same reason: -march=x86-64-v2 needs SSE4.2, and
        # QEMU's default qemu64 model does not have it.
        machine=(-machine q35 -cpu max)
        default_timeout=900
        ;;
    *)
        echo "run-qemu: unknown architecture '$arch'" >&2; exit 1 ;;
esac

cp "$vars_template" "$vars"

args=(
    "${machine[@]}"
    -smp 4
    -m 2048
    -drive "if=pflash,format=raw,unit=0,readonly=on,file=$code"
    -drive "if=pflash,format=raw,unit=1,file=$vars"
    # snapshot=on: writes go to a temporary overlay and are discarded on exit.
    # The guest's root is read-only, but /data is not, and journald starts
    # writing to it within seconds of boot — so without this every boot would
    # modify the artifact and invalidate the digest next to it. Phase 7 will
    # want a persistent mode to exercise A/B updates; it does not exist yet
    # because nothing has needed it.
    -drive "if=none,id=trinix-root,format=raw,snapshot=on,file=$image"
    -device virtio-blk-pci,drive=trinix-root,bootindex=0
    -device virtio-rng-pci
    -netdev user,id=net0
    -device virtio-net-pci,netdev=net0
    -display none
)

if [ "$check" -eq 1 ]; then
    [ "$timeout_s" -gt 0 ] || timeout_s="$default_timeout"
    serial_log="$images/serial-$arch.log"

    qemu_log="$images/qemu-$arch.log"

    echo "==> booting trinix-$arch.img unattended (up to ${timeout_s}s); console -> ${serial_log}"

    : > "$serial_log"

    # QEMU's own diagnostics go to a separate file rather than /dev/null: a
    # missing firmware blob or an unsupported machine type produces no serial
    # output at all, and "the console log is empty" is a much worse error
    # message than the one QEMU already wrote.
    "$qemu" "${args[@]}" -serial "file:$serial_log" > "$qemu_log" 2>&1 &
    qemu_pid=$!

    # Watch the console rather than waiting out the clock. Nothing tells QEMU
    # that the boot succeeded, so a plain `timeout` would make every passing
    # run take as long as a failing one — which is the difference between a
    # check that gets run and one that gets skipped.
    #
    # agetty writes /etc/issue and then "<hostname> login:" with no newline
    # after it, so the prompt is the last thing in the log rather than a line
    # of its own — hence a plain substring match.
    reached=0
    deadline=$((SECONDS + timeout_s))
    while kill -0 "$qemu_pid" 2>/dev/null; do
        if grep -q 'login:' "$serial_log" 2>/dev/null; then reached=1; break; fi
        [ "$SECONDS" -ge "$deadline" ] && break
        sleep 2
    done

    kill "$qemu_pid" 2>/dev/null || true
    wait "$qemu_pid" 2>/dev/null || true

    echo '--- serial console ---'
    cat "$serial_log" 2>/dev/null || true
    echo '--- end of console ---'

    if [ -s "$qemu_log" ]; then
        echo '--- qemu ---'
        cat "$qemu_log"
        echo '--- end of qemu ---'
    fi

    if [ "$reached" -eq 1 ]; then
        echo "PASS: trinix-$arch reached a login prompt in ${SECONDS}s."
        exit 0
    fi
    echo "FAIL: no login prompt appeared within ${timeout_s}s." >&2
    exit 1
fi

# mon:stdio multiplexes the QEMU monitor onto the same console: Ctrl-a c
# switches to it, Ctrl-a x quits. Without it there is no way out of a VM whose
# userland has stopped responding.
exec "$qemu" "${args[@]}" -serial mon:stdio "$@"
