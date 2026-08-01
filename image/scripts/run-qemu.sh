#!/usr/bin/env bash
# run-qemu <arm64|x86_64> [--check | --login-check [seconds]] [extra qemu args...]
#
# Boots a Trinix disk image. Runs inside the vm container (docker/vm.Dockerfile),
# with the image directory bind-mounted at /images.
#
# Three modes:
#   interactive    serial console on stdio — this is `run-vm.ps1 -Arch arm64`
#   --check        boot unattended and assert that a login prompt appeared.
#                  The Phase 2 exit criterion as a test.
#   --login-check  everything --check does, then log in and drive the session:
#                  does it land in PowerShell, does .NET work, did the C#
#                  service start. The Phase 3 exit criteria as a test.

set -euo pipefail

arch="${1:?usage: run-qemu <arm64|x86_64> [--check|--login-check [seconds]] [qemu args...]}"
shift

check=0
login_check=0
timeout_s=0
case "${1:-}" in
    --check)       check=1; shift ;;
    --login-check) check=1; login_check=1; shift ;;
esac
if [ "$check" -eq 1 ]; then
    case "${1:-}" in
        ''|-*) ;;
        *) timeout_s="$1"; shift ;;
    esac
fi

# The account the login check drives. Matches the trinix-system recipe, which
# is where these are actually decided.
TRINIX_LOGIN_USER="${TRINIX_LOGIN_USER:-trinix}"
TRINIX_LOGIN_PASSWORD="${TRINIX_LOGIN_PASSWORD:-trinix}"

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
        # Enough for the whole sequence, not just the boot: reaching a login
        # prompt is well under a minute, but -LoginCheck then waits for a
        # PowerShell session and a self-test that starts a second runtime, all
        # under emulation.
        default_timeout=900
        ;;
    x86_64)
        qemu=qemu-system-x86_64
        code="$(first_existing /usr/share/OVMF/OVMF_CODE_4M.fd /usr/share/OVMF/OVMF_CODE.fd /usr/share/ovmf/OVMF.fd)"
        vars_template="$(first_existing /usr/share/OVMF/OVMF_VARS_4M.fd /usr/share/OVMF/OVMF_VARS.fd)"
        # -cpu max for the same reason: -march=x86-64-v2 needs SSE4.2, and
        # QEMU's default qemu64 model does not have it.
        machine=(-machine q35 -cpu max)
        # Roughly double arm64's: emulating a different instruction set costs
        # more than emulating the host's own.
        default_timeout=1800
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

    # A fifo for the console's input side. --check never writes to it, but
    # --login-check has to type, and QEMU wants a real file descriptor rather
    # than something appearing on stdin later.
    console_in="$(mktemp -d)/console.in"
    mkfifo "$console_in"

    # QEMU's own diagnostics go to a separate file rather than /dev/null: a
    # missing firmware blob or an unsupported machine type produces no serial
    # output at all, and "the console log is empty" is a much worse error
    # message than the one QEMU already wrote.
    "$qemu" "${args[@]}" -serial stdio < "$console_in" > "$serial_log" 2>"$qemu_log" &
    qemu_pid=$!

    # Held open for the life of the run: closing it would send EOF to the
    # guest's console, which agetty reads as the session ending.
    exec 3>"$console_in"

    # Watch the console rather than waiting out the clock. Nothing tells QEMU
    # that the boot succeeded, so a plain `timeout` would make every passing
    # run take as long as a failing one — which is the difference between a
    # check that gets run and one that gets skipped.
    #
    # agetty writes /etc/issue and then "<hostname> login:" with no newline
    # after it, so the prompt is the last thing in the log rather than a line
    # of its own — hence a plain substring match.
    deadline=$((SECONDS + timeout_s))

    # await <pattern> — wait for something to appear on the console.
    #
    # The console is a log file being appended to, so this greps it rather than
    # reading a stream: a prompt written without a trailing newline never
    # arrives as a "line", and half of what this waits for is a prompt.
    # An optional second argument bounds one wait, so a single failed
    # assertion cannot spend the entire run's budget waiting for a line that is
    # never coming. The overall deadline still wins.
    await() { await_count "$1" 1 "${2:-0}"; }

    # await_count <pattern> <occurrences> [seconds]
    #
    # The count exists because this check logs in twice, and "wait for a login
    # prompt" means the *second* one the second time. Matching on a whole file
    # that already contains the first would otherwise return immediately.
    await_count() {
        local pattern="$1" want="$2" limit="${3:-0}" until_t="$deadline"
        if [ "$limit" -gt 0 ] && [ $((SECONDS + limit)) -lt "$deadline" ]; then
            until_t=$((SECONDS + limit))
        fi

        while kill -0 "$qemu_pid" 2>/dev/null; do
            [ "$(grep -ac "$pattern" "$serial_log" 2>/dev/null || echo 0)" -ge "$want" ] && return 0
            [ "$SECONDS" -ge "$until_t" ] && return 1
            sleep 2
        done
        return 1
    }

    # type <text> — send a line to the guest's console.
    #
    # \r, not \n: this is a terminal line discipline, and it expects the
    # carriage return a real terminal sends.
    type_line() { printf '%s\r' "$1" >&3; sleep 1; }

    reached=0
    verdict=''

    if await 'login:'; then
        reached=1
    fi

    if [ "$reached" -eq 1 ] && [ "$login_check" -eq 1 ]; then
        # Two logins, in this order for a reason.
        #
        # root first, because root's shell is bash and bash can be driven. An
        # interactive PowerShell cannot: its console host asks the terminal
        # where the cursor is each time it draws a prompt, nothing on a
        # scripted serial line answers, and the reader then eats the following
        # keystrokes waiting for a reply -- so every command after the first
        # arrives as line noise, and PSReadLine reads that noise as control
        # characters. bash has no such conversation with the terminal.
        #
        # That the rescue shell is the one a script can drive is not a
        # coincidence; it is the same property that makes it the rescue shell.
        #
        # The user login goes last precisely so that nothing has to be typed
        # into it. PowerShell's banner appearing after the password *is* the
        # assertion that a user logging in lands in PowerShell.
        echo "==> logging in as root to read the boot self-test"
        type_line 'root'

        if await 'Password' 120; then
            type_line "${TRINIX_ROOT_PASSWORD:-trinix}"

            # Wait for bash's prompt before typing at it. Characters sent
            # earlier sit in the tty buffer and are replayed once the shell
            # attaches, arriving doubled.
            if await 'root@trinix' 240; then
                # The platform verdict comes from the journal, not the console:
                # systemd stops writing service output to the console once a
                # getty owns it, so trinix-selftest.service's result reliably
                # reaches the journal and only incidentally anywhere else.
                #
                # `systemctl start` before reading, because the login prompt
                # appears at getty.target — well before multi-user.target,
                # which is when the self-test runs. Querying immediately finds
                # an empty journal and proves nothing. Starting a oneshot that
                # has already run is a no-op that returns at once, and starting
                # one that has not blocks until it finishes; either way the
                # journal has an answer by the time the next command runs.
                #
                # Unfiltered, because there is no grep in the base image: the
                # Phase 2 base set is the plan's list and text utilities are
                # not on it, PowerShell's Select-String being the intended
                # tool. It makes no difference here — the marker appears in
                # the journal's output and not in the command that asks for
                # it, so the shell's echo still cannot satisfy the assertion.
                type_line 'systemctl start trinix-selftest.service; journalctl -u trinix-selftest -o cat --no-pager'

                if ! await 'TRINIX-SELFTEST-True' 300; then
                    verdict="$verdict platform-selftest"
                fi

                type_line 'exit'
            else
                verdict="$verdict root-shell"
            fi
        else
            verdict="$verdict root-password-prompt"
        fi

        echo "==> logging in as ${TRINIX_LOGIN_USER} to check the login shell"
        if await_count 'login:' 2 300; then
            type_line "$TRINIX_LOGIN_USER"

            if await_count 'Password' 2 120; then
                type_line "$TRINIX_LOGIN_PASSWORD"

                if ! await 'PowerShell 7' 300; then
                    verdict="$verdict powershell-login-shell"
                fi
            else
                verdict="$verdict password-prompt"
            fi
        else
            verdict="$verdict second-login-prompt"
        fi
    fi

    kill "$qemu_pid" 2>/dev/null || true
    wait "$qemu_pid" 2>/dev/null || true
    exec 3>&-

    echo '--- serial console ---'
    cat "$serial_log" 2>/dev/null || true
    echo '--- end of console ---'

    if [ -s "$qemu_log" ]; then
        echo '--- qemu ---'
        cat "$qemu_log"
        echo '--- end of qemu ---'
    fi

    if [ "$reached" -ne 1 ]; then
        echo "FAIL: no login prompt appeared within ${timeout_s}s." >&2
        exit 1
    fi

    if [ "$login_check" -eq 1 ] && [ -n "$verdict" ]; then
        echo "FAIL: the session did not satisfy:$verdict" >&2
        exit 1
    fi

    if [ "$login_check" -eq 1 ]; then
        echo "PASS: trinix-$arch logged in to PowerShell with a working .NET in ${SECONDS}s."
    else
        echo "PASS: trinix-$arch reached a login prompt in ${SECONDS}s."
    fi
    exit 0
fi

# mon:stdio multiplexes the QEMU monitor onto the same console: Ctrl-a c
# switches to it, Ctrl-a x quits. Without it there is no way out of a VM whose
# userland has stopped responding.
exec "$qemu" "${args[@]}" -serial mon:stdio "$@"
