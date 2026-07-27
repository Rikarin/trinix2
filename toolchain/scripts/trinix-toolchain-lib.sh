# shellcheck shell=bash
# Common prelude for the Phase 1 toolchain build scripts.
#
# Sourced, never executed. Defines the arch table (the shell-side twin of
# scripts/lib/Trinix.Build.psm1) plus a few logging helpers.

set -euo pipefail

: "${TRINIX_TOOLCHAIN:=/opt/trinix/toolchain}"
: "${TRINIX_SYSROOTS:=/opt/trinix/sysroots}"
: "${TRINIX_BUILD:=/build}"
# Parallelism is bounded by memory, not by cores.
#
# Compiling LLVM/Clang is the memory-hungriest thing in the tree: the heavy
# translation units (Sema, *ISelLowering) peak well over a gigabyte each, so
# `-j$(nproc)` reliably OOMs a Docker Desktop VM with its default 8 GB even on a
# 10-core machine. Derive the job count from RAM and take the lower of the two.
: "${MEM_PER_JOB_MB:=1536}"
if [ -z "${JOBS:-}" ]; then
    _cpu_jobs="$(nproc)"
    _mem_total_mb="$(awk '/MemTotal/ {print int($2 / 1024)}' /proc/meminfo)"
    _mem_jobs="$((_mem_total_mb / MEM_PER_JOB_MB))"
    [ "$_mem_jobs" -lt 1 ] && _mem_jobs=1
    if [ "$_mem_jobs" -lt "$_cpu_jobs" ]; then JOBS="$_mem_jobs"; else JOBS="$_cpu_jobs"; fi
    if [ "$_mem_jobs" -lt "$_cpu_jobs" ]; then
        printf '\033[1;33mnote: limiting to %s jobs (%s cores, %s MiB RAM). Giving Docker more memory speeds this up substantially.\033[0m\n' \
               "$JOBS" "$_cpu_jobs" "$_mem_total_mb"
    fi
fi
export JOBS

# Linking LLVM peaks around 4 GB per link job.
: "${LINK_JOBS:=2}"

# trinix_prepare_objdir <objdir> <key...> — make an out-of-tree build directory
# ready, reusing it only if its inputs are unchanged.
#
# Build trees live in a BuildKit cache mount so an interrupted build can resume,
# and steps skip `configure` when a Makefile already exists. That combination is
# a trap: bump a pinned version and the stale tree gets reused, so the build
# silently produces the *previous* version. Keying the directory on its inputs
# keeps resumability while making a changed pin wipe the tree.
trinix_prepare_objdir() {
    local objdir="$1"; shift
    local key="$*"
    local keyfile="$objdir/.trinix-inputs"

    if [ -f "$keyfile" ] && [ "$(cat "$keyfile")" = "$key" ]; then
        return 0
    fi

    [ -e "$objdir" ] && step "inputs changed — discarding stale build tree $objdir"
    rm -rf "$objdir"
    mkdir -p "$objdir"
    printf '%s' "$key" > "$keyfile"
}

log()  { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
step() { printf '\033[1;33m  -> %s\033[0m\n' "$*"; }
die()  { printf '\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

# GNU make's built-in link rule is `LINK.o = $(CC) $(LDFLAGS) $(TARGET_ARCH)`,
# and make imports variables from the environment. An exported TARGET_ARCH=arm64
# therefore appends a bare `arm64` to every implicit link command, which the
# linker reads as a missing input file — glibc fails deep in its build with
# "cannot find arm64". Hence TRINIX_ARCH, and hence this guard: any inherited
# TARGET_ARCH is removed before a single make is invoked.
unset TARGET_ARCH

# trinix_set_arch <arm64|x86_64> — export everything downstream needs.
trinix_set_arch() {
    TRINIX_ARCH="${1:?usage: trinix_set_arch <arm64|x86_64>}"

    case "$TRINIX_ARCH" in
        arm64)
            TARGET_TRIPLE='aarch64-trinix-linux-gnu'
            KERNEL_ARCH='arm64'
            QEMU='qemu-aarch64-static'
            ARCH_FLAGS='-march=armv8.2-a'
            ;;
        x86_64)
            TARGET_TRIPLE='x86_64-trinix-linux-gnu'
            KERNEL_ARCH='x86_64'
            QEMU='qemu-x86_64-static'
            ARCH_FLAGS='-march=x86-64-v2'
            ;;
        *)
            die "unknown architecture '$TRINIX_ARCH' (expected arm64 or x86_64)"
            ;;
    esac

    SYSROOT="$TRINIX_SYSROOTS/$TARGET_TRIPLE"
    export TRINIX_ARCH TARGET_TRIPLE KERNEL_ARCH QEMU ARCH_FLAGS SYSROOT
}

# The minimum kernel glibc will assume. Nothing in Trinix targets anything
# older, and raising it removes a pile of legacy syscall fallbacks.
export TRINIX_MIN_KERNEL='5.15'
