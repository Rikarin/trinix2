# shellcheck shell=bash
# Common prelude for the Phase 1 toolchain build scripts.
#
# Sourced, never executed. Defines the arch table (the shell-side twin of
# scripts/lib/Trinix.Build.psm1) plus a few logging helpers.

set -euo pipefail

: "${TRINIX_TOOLCHAIN:=/opt/trinix/toolchain}"
: "${TRINIX_SYSROOTS:=/opt/trinix/sysroots}"
: "${TRINIX_BUILD:=/build}"
: "${JOBS:=$(nproc)}"

# Linking LLVM eats ~4 GB per link job; the build container typically has 8 GB.
: "${LINK_JOBS:=2}"

log()  { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
step() { printf '\033[1;33m  -> %s\033[0m\n' "$*"; }
die()  { printf '\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

# trinix_set_arch <arm64|x86_64> — export everything downstream needs.
trinix_set_arch() {
    TARGET_ARCH="${1:?usage: trinix_set_arch <arm64|x86_64>}"

    case "$TARGET_ARCH" in
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
            die "unknown architecture '$TARGET_ARCH' (expected arm64 or x86_64)"
            ;;
    esac

    SYSROOT="$TRINIX_SYSROOTS/$TARGET_TRIPLE"
    export TARGET_ARCH TARGET_TRIPLE KERNEL_ARCH QEMU ARCH_FLAGS SYSROOT
}

# The minimum kernel glibc will assume. Nothing in Trinix targets anything
# older, and raising it removes a pile of legacy syscall fallbacks.
export TRINIX_MIN_KERNEL='5.15'
