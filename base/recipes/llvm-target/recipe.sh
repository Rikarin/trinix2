# shellcheck shell=bash
# libLLVM for the *target* — because a software rasteriser is a JIT.
#
# ---------------------------------------------------------------------------
# Why an image with no GPU needs a compiler in it
# ---------------------------------------------------------------------------
#
# Phase 4 shipped no Mesa on the argument that wlroots' GL renderer wants a DRM
# render node the VM tier cannot supply, and composited through pixman instead.
# That argument still holds for the compositor. It does not hold for Vixen:
# Vixen.Ui.Desktop renders through a VulkanDevice and its UI renderer has no CPU
# path at all, so an application needs an ICD whether or not there is a GPU.
#
# The ICD that runs without one is Mesa's lavapipe, and lavapipe is a compiler:
# it translates shaders into x86 or AArch64 machine code at draw time through
# LLVM's ORC JIT. So libLLVM.so ships, in an image whose whole toolchain story
# is that compilers live in the *build container*. It is here as a runtime
# library, the way a language runtime is, and nothing in the image compiles C.
#
# ---------------------------------------------------------------------------
# This is the same tarball Phase 1 builds, and that is the point
# ---------------------------------------------------------------------------
#
# toolchain/scripts/build-llvm.sh builds LLVM for the *build machine* — one
# clang that cross-compiles to both triples. This builds the same pinned source
# for *one* target machine, as a library and nothing else: no clang, no lld, no
# tools beyond the llvm-config the Mesa recipe interrogates. One pin, two very
# different outputs, and a version bump moves them together.

RECIPE_SOURCE="llvm-project"
RECIPE_DEPENDS="glibc-runtime llvm-runtime"

trinix_build() {
    local llvm_targets
    case "$TRINIX_ARCH" in
        arm64)  llvm_targets='AArch64' ;;
        x86_64) llvm_targets='X86'     ;;
        *) echo "llvm-target: unknown architecture '$TRINIX_ARCH'" >&2; return 1 ;;
    esac

    # ⚠ Only the machine this image runs on. The host toolchain builds
    # AArch64;X86;BPF because one compiler serves every target; a JIT compiles
    # for the processor it is running on and can have no use for a second
    # backend, which is thirty megabytes of libLLVM.so each.
    cmake -G Ninja -S "$SRCDIR/llvm" -B "$BUILDDIR" \
        -DCMAKE_TOOLCHAIN_FILE="$CMAKE_TOOLCHAIN" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX=/usr \
        -DLLVM_TARGETS_TO_BUILD="$llvm_targets" \
        -DLLVM_HOST_TRIPLE="$TARGET_TRIPLE" \
        -DLLVM_DEFAULT_TARGET_TRIPLE="$TARGET_TRIPLE" \
        \
        `# TableGen runs on the build machine and generates source, so a cross` \
        `# build needs a native copy of it. Phase 1 already installed one —` \
        `# LLVM_NATIVE_TOOL_DIR is probed with EXISTS per tool, so llvm-tblgen` \
        `# is taken from there and llvm-min-tblgen, which Phase 1 does not` \
        `# install, falls back to LLVM's own native sub-build. That sub-build` \
        `# is a few minutes and it is why this recipe does not need a change to` \
        `# the toolchain stage, which would cost a full LLVM rebuild.` \
        -DLLVM_NATIVE_TOOL_DIR="$TRINIX_TOOLCHAIN/bin" \
        -DLLVM_TABLEGEN="$TRINIX_TOOLCHAIN/bin/llvm-tblgen" \
        \
        `# One shared library rather than eighty static ones: Mesa links` \
        `# against LLVM, and so would anything else that ever wants it.` \
        -DLLVM_BUILD_LLVM_DYLIB=ON \
        -DLLVM_LINK_LLVM_DYLIB=ON \
        -DLLVM_ENABLE_PIC=ON \
        \
        `# RTTI is off in an upstream LLVM and on in every distribution's,` \
        `# because the things that embed LLVM want it. Mesa's gallivm does not` \
        `# strictly need it and rusticl does; enabling it costs a few per cent` \
        `# of a library that is already large, and the alternative is finding` \
        `# out after a forty-minute build.` \
        -DLLVM_ENABLE_RTTI=ON \
        \
        `# Nothing but LLVM itself. The projects and runtimes belong to the` \
        `# toolchain stage, and building them here would be building a second` \
        `# compiler to run on a machine that does not compile anything.` \
        -DLLVM_ENABLE_PROJECTS='' \
        -DLLVM_ENABLE_RUNTIMES='' \
        \
        `# Every optional dependency answered explicitly. cmake would` \
        `# otherwise decide from what happens to be in the sysroot, and the` \
        `# rootfs would differ between a warm cache and a clean build —` \
        `# base/recipes/README.md § The build container is not the target.` \
        -DLLVM_ENABLE_ZLIB=OFF \
        -DLLVM_ENABLE_ZSTD=OFF \
        -DLLVM_ENABLE_LIBXML2=OFF \
        -DLLVM_ENABLE_TERMINFO=OFF \
        -DLLVM_ENABLE_LIBEDIT=OFF \
        -DLLVM_ENABLE_LIBPFM=OFF \
        -DLLVM_ENABLE_BINDINGS=OFF \
        -DLLVM_ENABLE_OCAMLDOC=OFF \
        -DLLVM_ENABLE_ASSERTIONS=OFF \
        -DLLVM_INCLUDE_TESTS=OFF \
        -DLLVM_INCLUDE_BENCHMARKS=OFF \
        -DLLVM_INCLUDE_EXAMPLES=OFF \
        -DLLVM_INCLUDE_DOCS=OFF \
        -DLLVM_INSTALL_UTILS=OFF \
        \
        `# What "install" means for this recipe. LLVM_BUILD_TOOLS is left ON` \
        `# deliberately: it controls what lands in the "all" target, and ninja` \
        `# is only ever asked for install-distribution below, so the hundred` \
        `# command-line tools are never built rather than being built and` \
        `# discarded. llvm-config is in the list because the Mesa recipe runs` \
        `# it to learn where this install put things.` \
        -DLLVM_DISTRIBUTION_COMPONENTS='LLVM;llvm-headers;llvm-config;cmake-exports'

    DESTDIR="$DESTDIR" ninja -C "$BUILDDIR" -j "$JOBS" install-distribution
}

trinix_check() {
    local lib
    lib="$(echo "$DESTDIR"/usr/lib/libLLVM.so.*)"
    [ -e "$lib" ] || { echo 'llvm-target: no libLLVM.so.* was installed' >&2; return 1; }

    # The failure this exists for: a toolchain file that did not take, giving a
    # perfectly good libLLVM.so for the build machine. It would link, and Mesa
    # would fail at the end of its own build with an architecture mismatch.
    local want
    case "$TRINIX_ARCH" in
        arm64)  want='AArch64' ;;
        x86_64) want='X86-64'  ;;
    esac
    llvm-readelf --file-header "$lib" | grep -q "$want" \
        || { echo "llvm-target: $lib is not $want" >&2; return 1; }

    # And the other silent failure: a dylib built with no backend in it. LLVM
    # configures happily with an empty LLVM_TARGETS_TO_BUILD and lavapipe would
    # then fail at runtime, inside a VM, with "no target for triple".
    local marker
    case "$TRINIX_ARCH" in
        arm64)  marker='LLVMInitializeAArch64Target' ;;
        x86_64) marker='LLVMInitializeX86Target'     ;;
    esac
    # Two things about this pipeline, both of which cost a build to find out.
    #
    # The version suffix is stripped first: libLLVM is built with a version
    # script, so every dynamic symbol reads `LLVMInitializeAArch64Target@@LLVM_21.1`
    # and an exact match against the bare name finds nothing — on a library
    # that exports it perfectly well.
    #
    # And the list is captured before it is searched rather than piped into
    # `grep -q`, which exits on the match and leaves awk holding a closed pipe:
    # a SIGPIPE that the driver's `set -o pipefail` reports as a failed check.
    local symbols
    symbols="$(llvm-nm --defined-only --dynamic "$lib" | awk '{ print $NF }' | sed 's/@@.*//')"
    grep -qx "$marker" <<<"$symbols" \
        || { echo "llvm-target: $marker is not exported — built with no backend" >&2; return 1; }

    [ -x "$DESTDIR/usr/bin/llvm-config" ] \
        || { echo 'llvm-target: llvm-config missing — the Mesa recipe needs it' >&2; return 1; }
}
