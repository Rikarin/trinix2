#!/usr/bin/env bash
# build-sysroot.sh <arm64|x86_64> — build one target sysroot.
#
# Order matters and each step exists for a specific reason:
#
#   1. linux headers   glibc cannot be configured without them
#   2. binutils        glibc's build needs a GNU assembler/linker for the target
#   3. GCC pass 1      a compiler that can build glibc, but cannot yet link
#                      against it (no shared libs, no libstdc++)
#   4. glibc           the actual C library; from here the sysroot is usable
#   5. GCC pass 2      rebuilt against real glibc, purely to harvest
#                      libgcc_s.so.1 and libstdc++.so.6 — Microsoft's official
#                      .NET binaries link against them, so they ship as compat
#                      libraries even though nothing Trinix builds uses them
#   6. LLVM runtimes   compiler-rt / libunwind / libc++ — what Clang-built
#                      components actually use
#   7. clang config    per-triple .cfg so `clang --target=<triple>` is
#                      self-configuring for every recipe from here on
#
# GCC exists in this pipeline only because glibc cannot be built by Clang. That
# is the one permanent GCC dependency in the tree, and it is contained here.

# shellcheck source=./trinix-toolchain-lib.sh
. /usr/local/lib/trinix/scripts/trinix-toolchain-lib.sh

trinix_set_arch "${1:?usage: build-sysroot.sh <arm64|x86_64>}"

srcroot="$TRINIX_BUILD/src"
objroot="$TRINIX_BUILD/obj/$TARGET_ARCH"
mkdir -p "$srcroot" "$objroot"

log "Building sysroot for $TARGET_ARCH ($TARGET_TRIPLE)"
step "sysroot: $SYSROOT"
step "jobs:    $JOBS"

# ---------------------------------------------------------------------------
# Merged-/usr layout, established before anything installs into the sysroot.
# systemd has required merged-/usr for years and it removes a whole class of
# "which lib directory is this in" problems from every later recipe.
# ---------------------------------------------------------------------------
mkdir -p "$SYSROOT/usr/"{bin,lib,include,share} "$SYSROOT/etc"
ln -sfn usr/bin "$SYSROOT/bin"
ln -sfn usr/bin "$SYSROOT/sbin"
ln -sfn usr/lib "$SYSROOT/lib"
ln -sfn bin     "$SYSROOT/usr/sbin"
# x86_64's ABI hardcodes /lib64/ld-linux-x86-64.so.2 into every dynamic binary.
[ "$TARGET_ARCH" = 'x86_64' ] && ln -sfn usr/lib "$SYSROOT/lib64"

# ---------------------------------------------------------------------------
# 1. Linux API headers
# ---------------------------------------------------------------------------
log '1/7  Linux API headers'
linux_src="$(trinix-extract linux "$srcroot")"
linux_obj="$objroot/linux-headers"
mkdir -p "$linux_obj"
# O= keeps the shared source tree clean so the other architecture can reuse it.
make -C "$linux_src" O="$linux_obj" ARCH="$KERNEL_ARCH" \
     INSTALL_HDR_PATH="$SYSROOT/usr" headers_install >/dev/null
step "installed $(find "$SYSROOT/usr/include" -name '*.h' | wc -l) headers"

# ---------------------------------------------------------------------------
# 2. binutils (cross) — used only by GCC and glibc
# ---------------------------------------------------------------------------
log '2/7  binutils'
binutils_src="$(trinix-extract binutils "$srcroot")"
binutils_obj="$objroot/binutils"
mkdir -p "$binutils_obj"
(
    cd "$binutils_obj"
    [ -f Makefile ] || "$binutils_src/configure" \
        --prefix="$TRINIX_TOOLCHAIN" \
        --target="$TARGET_TRIPLE" \
        --with-sysroot="$SYSROOT" \
        --enable-deterministic-archives \
        --disable-nls --disable-werror --disable-gprofng \
        --enable-64-bit-bfd >/dev/null
    make -j"$JOBS" >/dev/null
    make install >/dev/null
)
step "$("$TRINIX_TOOLCHAIN/bin/$TARGET_TRIPLE-as" --version | head -1)"

# ---------------------------------------------------------------------------
# GCC source, with its in-tree prerequisites unpacked once and shared.
# ---------------------------------------------------------------------------
gcc_src="$(trinix-extract gcc "$srcroot")"
for prereq in gmp mpfr mpc isl; do
    if [ ! -d "$gcc_src/$prereq" ]; then
        extracted="$(trinix-extract "$prereq" "$srcroot")"
        mv "$extracted" "$gcc_src/$prereq"
    fi
done

glibc_version="$(trinix-fetch --version glibc)"

# ---------------------------------------------------------------------------
# 3. GCC pass 1 — just enough compiler to build glibc
# ---------------------------------------------------------------------------
log '3/7  GCC pass 1 (bootstrap compiler for glibc)'
gcc1_obj="$objroot/gcc-pass1"
mkdir -p "$gcc1_obj"
(
    cd "$gcc1_obj"
    [ -f Makefile ] || "$gcc_src/configure" \
        --prefix="$TRINIX_TOOLCHAIN" \
        --target="$TARGET_TRIPLE" \
        --with-sysroot="$SYSROOT" \
        --with-glibc-version="$glibc_version" \
        --with-newlib --without-headers \
        --enable-languages=c,c++ \
        --enable-default-pie --enable-default-ssp \
        --disable-nls --disable-shared --disable-multilib \
        --disable-threads --disable-libatomic --disable-libgomp \
        --disable-libquadmath --disable-libssp --disable-libvtv \
        --disable-libstdcxx --disable-decimal-float \
        --disable-bootstrap >/dev/null
    make -j"$JOBS" all-gcc all-target-libgcc >/dev/null
    make install-gcc install-target-libgcc >/dev/null
)

# GCC's internal limits.h is a stub until glibc's headers exist. glibc's own
# build reads it, so stitch the full version together now (standard LFS step).
libgcc_dir="$(dirname "$("$TRINIX_TOOLCHAIN/bin/$TARGET_TRIPLE-gcc" -print-libgcc-file-name)")"
mkdir -p "$libgcc_dir/include"
cat "$gcc_src/gcc/limitx.h" "$gcc_src/gcc/glimits.h" "$gcc_src/gcc/limity.h" \
    > "$libgcc_dir/include/limits.h"
step "$("$TRINIX_TOOLCHAIN/bin/$TARGET_TRIPLE-gcc" --version | head -1)"

# ---------------------------------------------------------------------------
# 4. glibc — the one component that genuinely requires GCC
# ---------------------------------------------------------------------------
log "4/7  glibc $glibc_version"
glibc_src="$(trinix-extract glibc "$srcroot")"
glibc_obj="$objroot/glibc"
mkdir -p "$glibc_obj"
(
    cd "$glibc_obj"
    [ -f Makefile ] || \
    CC="$TRINIX_TOOLCHAIN/bin/$TARGET_TRIPLE-gcc" \
    CXX="$TRINIX_TOOLCHAIN/bin/$TARGET_TRIPLE-g++" \
    AR="$TRINIX_TOOLCHAIN/bin/$TARGET_TRIPLE-ar" \
    RANLIB="$TRINIX_TOOLCHAIN/bin/$TARGET_TRIPLE-ranlib" \
    "$glibc_src/configure" \
        --prefix=/usr \
        --host="$TARGET_TRIPLE" \
        --build="$("$glibc_src/scripts/config.guess")" \
        --enable-kernel="$TRINIX_MIN_KERNEL" \
        --with-headers="$SYSROOT/usr/include" \
        --disable-nscd \
        --disable-werror \
        libc_cv_slibdir=/usr/lib >/dev/null
    make -j"$JOBS" >/dev/null
    make DESTDIR="$SYSROOT" install >/dev/null
)

# ldd is generated with the build-time prefix baked in; strip it so the script
# is correct on the installed system.
[ -f "$SYSROOT/usr/bin/ldd" ] && sed -i '/RTLDLIST=/s@/usr@@g' "$SYSROOT/usr/bin/ldd"
step "installed $(basename "$(echo "$SYSROOT"/usr/lib/ld-linux-*.so.*)")"

# ---------------------------------------------------------------------------
# 5. GCC pass 2 — harvest libgcc_s.so.1 and libstdc++.so.6 as compat libraries
# ---------------------------------------------------------------------------
log '5/7  GCC pass 2 (libgcc_s + libstdc++ compat libraries for .NET)'
gcc2_obj="$objroot/gcc-pass2"
mkdir -p "$gcc2_obj"
(
    cd "$gcc2_obj"
    [ -f Makefile ] || "$gcc_src/configure" \
        --prefix="$TRINIX_TOOLCHAIN" \
        --target="$TARGET_TRIPLE" \
        --with-sysroot="$SYSROOT" \
        --with-build-sysroot="$SYSROOT" \
        --enable-languages=c,c++ \
        --enable-shared --enable-threads=posix \
        --enable-default-pie --enable-default-ssp \
        --enable-__cxa_atexit \
        --disable-nls --disable-multilib --disable-libssp \
        --disable-bootstrap >/dev/null
    make -j"$JOBS" >/dev/null
    make install >/dev/null
)

# Ship only the runtime libraries into the sysroot — the GCC *driver* stays out
# of the target entirely.
for lib in libgcc_s.so.1 libstdc++.so.6 libatomic.so.1 libgomp.so.1; do
    found="$(find "$TRINIX_TOOLCHAIN/$TARGET_TRIPLE/lib" -maxdepth 2 -name "$lib*" 2>/dev/null | head -1)"
    if [ -n "$found" ]; then
        cp -P "$(dirname "$found")/$lib"* "$SYSROOT/usr/lib/" 2>/dev/null || true
        step "compat library: $lib"
    fi
done

# ---------------------------------------------------------------------------
# 6. LLVM runtimes — what everything Clang-built actually links against
# ---------------------------------------------------------------------------
log '6/7  compiler-rt, libunwind, libc++'
llvm_src="$(trinix-extract llvm-project "$srcroot")"
resource_dir="$("$TRINIX_TOOLCHAIN/bin/clang" -print-resource-dir)"

runtimes_cmake_common=(
    -G Ninja
    -DCMAKE_BUILD_TYPE=Release
    -DCMAKE_SYSTEM_NAME=Linux
    -DCMAKE_SYSTEM_PROCESSOR="${TARGET_ARCH/arm64/aarch64}"
    -DCMAKE_C_COMPILER="$TRINIX_TOOLCHAIN/bin/clang"
    -DCMAKE_CXX_COMPILER="$TRINIX_TOOLCHAIN/bin/clang++"
    -DCMAKE_ASM_COMPILER="$TRINIX_TOOLCHAIN/bin/clang"
    -DCMAKE_C_COMPILER_TARGET="$TARGET_TRIPLE"
    -DCMAKE_CXX_COMPILER_TARGET="$TARGET_TRIPLE"
    -DCMAKE_ASM_COMPILER_TARGET="$TARGET_TRIPLE"
    -DCMAKE_SYSROOT="$SYSROOT"
    -DCMAKE_C_FLAGS="$ARCH_FLAGS"
    -DCMAKE_CXX_FLAGS="$ARCH_FLAGS"
    -DCMAKE_EXE_LINKER_FLAGS=-fuse-ld=lld
    -DCMAKE_SHARED_LINKER_FLAGS=-fuse-ld=lld
    -DLLVM_INCLUDE_TESTS=OFF
    -DCMAKE_FIND_ROOT_PATH_MODE_LIBRARY=ONLY
    -DCMAKE_FIND_ROOT_PATH_MODE_INCLUDE=ONLY
)

# 6a. compiler-rt builtins go into Clang's *resource directory*, not the
#     sysroot — that is where the driver looks for them.
step 'compiler-rt builtins -> clang resource dir'
crt_obj="$objroot/compiler-rt"
cmake -S "$llvm_src/runtimes" -B "$crt_obj" \
    "${runtimes_cmake_common[@]}" \
    -DCMAKE_INSTALL_PREFIX="$resource_dir" \
    -DLLVM_ENABLE_RUNTIMES='compiler-rt' \
    -DLLVM_ENABLE_PER_TARGET_RUNTIME_DIR=ON \
    -DCOMPILER_RT_DEFAULT_TARGET_ONLY=ON \
    -DCOMPILER_RT_BUILD_SANITIZERS=OFF \
    -DCOMPILER_RT_BUILD_XRAY=OFF \
    -DCOMPILER_RT_BUILD_LIBFUZZER=OFF \
    -DCOMPILER_RT_BUILD_MEMPROF=OFF \
    -DCOMPILER_RT_BUILD_ORC=OFF \
    -DCOMPILER_RT_BUILD_PROFILE=ON \
    -DCOMPILER_RT_BUILD_CTX_PROFILE=OFF >/dev/null
ninja -C "$crt_obj" -j"$JOBS" >/dev/null
ninja -C "$crt_obj" install >/dev/null

# 6b. The C++ stack is part of the target system, so it installs into the
#     sysroot — and with PER_TARGET_RUNTIME_DIR off, so the libraries land in
#     the real /usr/lib rather than /usr/lib/<triple>. These are shipped system
#     libraries, not compiler-private ones.
step 'libunwind + libc++abi + libc++ -> sysroot'
cxx_obj="$objroot/libcxx"
cmake -S "$llvm_src/runtimes" -B "$cxx_obj" \
    "${runtimes_cmake_common[@]}" \
    -DCMAKE_INSTALL_PREFIX="$SYSROOT/usr" \
    -DLLVM_ENABLE_PER_TARGET_RUNTIME_DIR=OFF \
    -DLLVM_ENABLE_RUNTIMES='libunwind;libcxxabi;libcxx' \
    -DLIBCXX_USE_COMPILER_RT=ON \
    -DLIBCXXABI_USE_COMPILER_RT=ON \
    -DLIBCXXABI_USE_LLVM_UNWINDER=ON \
    -DLIBUNWIND_USE_COMPILER_RT=ON \
    -DLIBCXX_HAS_ATOMIC_LIB=OFF >/dev/null
ninja -C "$cxx_obj" -j"$JOBS" >/dev/null
ninja -C "$cxx_obj" install >/dev/null

# ---------------------------------------------------------------------------
# 7. Per-triple clang configuration
#
# With these in place, `clang --target=aarch64-trinix-linux-gnu foo.c` is fully
# configured — sysroot, runtime library, unwinder, linker, arch baseline. Every
# base recipe gets the same compiler behaviour without repeating a flag soup,
# which is precisely the uniformity the all-Clang decision was chosen for.
# ---------------------------------------------------------------------------
log '7/7  clang configuration files'
cfgdir="$TRINIX_TOOLCHAIN/etc/clang"
mkdir -p "$cfgdir"

cat > "$cfgdir/$TARGET_TRIPLE.cfg" <<EOF
# Trinix $TARGET_ARCH — generated by build-sysroot.sh, do not edit.
--sysroot=$SYSROOT
$ARCH_FLAGS
-rtlib=compiler-rt
-unwindlib=libunwind
-fuse-ld=lld
EOF

# clang++ needs the C++ standard library selected too. A driver-specific config
# fully replaces the generic one, so include it explicitly rather than
# duplicating the flags.
cat > "$cfgdir/$TARGET_TRIPLE-clang++.cfg" <<EOF
# Trinix $TARGET_ARCH C++ — generated by build-sysroot.sh, do not edit.
@$TARGET_TRIPLE.cfg
-stdlib=libc++
EOF

step "wrote $cfgdir/$TARGET_TRIPLE.cfg"
log "Sysroot for $TARGET_ARCH complete: $(du -sh "$SYSROOT" | cut -f1)"
