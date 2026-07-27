#!/usr/bin/env bash
# build-llvm.sh — build the one LLVM/Clang/LLD install that cross-compiles
# everything Trinix ships, for every target, from this single container.
#
# This is the payoff of the all-LLVM decision: no per-arch cross-GCC bootstrap,
# no per-arch binutils. One compiler, `--target=<triple>` selects the machine.
#
# Runtimes (compiler-rt, libunwind, libc++) are NOT built here — they are per
# target and need a sysroot with glibc in it, so they belong to build-sysroot.sh.

# shellcheck source=./trinix-toolchain-lib.sh
. /usr/local/lib/trinix/scripts/trinix-toolchain-lib.sh

# AArch64 and X86 are the shipping targets. BPF is included because the kernel
# build wants it for BTF/eBPF objects, and it is nearly free to carry.
: "${LLVM_TARGETS:=AArch64;X86;BPF}"

srcdir="$(trinix-extract llvm-project "$TRINIX_BUILD/src")"
builddir="$TRINIX_BUILD/obj/llvm"

log "Building LLVM $(trinix-fetch --version llvm-project) for targets: $LLVM_TARGETS"
step "source:  $srcdir"
step "build:   $builddir"
step "install: $TRINIX_TOOLCHAIN"
step "jobs:    $JOBS compile / $LINK_JOBS link"

cmake -G Ninja -S "$srcdir/llvm" -B "$builddir" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_INSTALL_PREFIX="$TRINIX_TOOLCHAIN" \
    -DLLVM_ENABLE_PROJECTS='clang;lld' \
    -DLLVM_TARGETS_TO_BUILD="$LLVM_TARGETS" \
    -DLLVM_PARALLEL_LINK_JOBS="$LINK_JOBS" \
    -DLLVM_ENABLE_ASSERTIONS=OFF \
    -DLLVM_INCLUDE_TESTS=OFF \
    -DLLVM_INCLUDE_BENCHMARKS=OFF \
    -DLLVM_INCLUDE_EXAMPLES=OFF \
    -DLLVM_INCLUDE_DOCS=OFF \
    -DLLVM_ENABLE_BINDINGS=OFF \
    -DLLVM_ENABLE_OCAMLDOC=OFF \
    -DLLVM_ENABLE_TERMINFO=OFF \
    -DLLVM_ENABLE_LIBXML2=OFF \
    -DLLVM_ENABLE_ZSTD=OFF \
    -DLLVM_ENABLE_LIBEDIT=OFF \
    -DLLVM_INSTALL_UTILS=ON \
    `# No binutils symlinks: this prefix goes on PATH ahead of the host's, and` \
    `# shadowing ar/nm/ranlib/strip would silently rewire the GCC and glibc` \
    `# bootstrap. Recipes call the llvm-* names explicitly instead.` \
    -DLLVM_INSTALL_BINUTILS_SYMLINKS=OFF \
    -DCLANG_DEFAULT_LINKER=lld \
    -DCLANG_CONFIG_FILE_SYSTEM_DIR="$TRINIX_TOOLCHAIN/etc/clang" \
    -DCMAKE_INSTALL_RPATH="$TRINIX_TOOLCHAIN/lib" \
    -DLLVM_BUILD_LLVM_DYLIB=ON \
    -DLLVM_LINK_LLVM_DYLIB=ON

log 'Compiling (this is the long one)'
ninja -C "$builddir" -j "$JOBS"

log 'Installing'
ninja -C "$builddir" install

# Per-target clang config files are written by build-sysroot.sh, once there is
# a sysroot for them to point at. Create the directory now so clang does not
# warn about a missing config dir in the meantime.
mkdir -p "$TRINIX_TOOLCHAIN/etc/clang"

log 'LLVM install summary'
"$TRINIX_TOOLCHAIN/bin/clang" --version
"$TRINIX_TOOLCHAIN/bin/ld.lld" --version
# `| head` would SIGPIPE clang and, under pipefail, fail the build for no reason.
"$TRINIX_TOOLCHAIN/bin/clang" --print-targets

# The whole premise is one compiler for both machines — assert it rather than
# discovering otherwise in Phase 2.
for triple in aarch64-trinix-linux-gnu x86_64-trinix-linux-gnu; do
    printf 'int main(void){return 0;}\n' > "$TRINIX_BUILD/probe.c"
    "$TRINIX_TOOLCHAIN/bin/clang" --target="$triple" -c "$TRINIX_BUILD/probe.c" \
        -o "$TRINIX_BUILD/probe-$triple.o" -nostdinc \
        || die "clang cannot generate code for $triple"
    step "code generation OK for $triple"
done
rm -f "$TRINIX_BUILD"/probe*.o "$TRINIX_BUILD/probe.c"

log 'LLVM stage complete'
