# Shared body of the Trinix cross-compilation toolchain files.
#
# Included by the per-arch files, which set TRINIX_TARGET_TRIPLE and
# CMAKE_SYSTEM_PROCESSOR first. Never use this file directly.
#
# Design: there is one Clang install for all targets, so cross-compiling is
# purely a matter of --target + --sysroot. No per-arch binutils prefix exists;
# the LLVM equivalents are used unconditionally.

if(NOT DEFINED TRINIX_TARGET_TRIPLE)
    message(FATAL_ERROR "Include a per-arch toolchain file, not trinix-common.cmake directly.")
endif()

set(CMAKE_SYSTEM_NAME Linux)

if(DEFINED ENV{TRINIX_TOOLCHAIN})
    set(TRINIX_TOOLCHAIN_DIR "$ENV{TRINIX_TOOLCHAIN}")
else()
    set(TRINIX_TOOLCHAIN_DIR "/opt/trinix/toolchain")
endif()

if(DEFINED ENV{TRINIX_SYSROOTS})
    set(TRINIX_SYSROOT "$ENV{TRINIX_SYSROOTS}/${TRINIX_TARGET_TRIPLE}")
else()
    set(TRINIX_SYSROOT "/opt/trinix/sysroots/${TRINIX_TARGET_TRIPLE}")
endif()

set(CMAKE_SYSROOT       "${TRINIX_SYSROOT}")
set(CMAKE_FIND_ROOT_PATH "${TRINIX_SYSROOT}")

set(CMAKE_C_COMPILER   "${TRINIX_TOOLCHAIN_DIR}/bin/clang")
set(CMAKE_CXX_COMPILER "${TRINIX_TOOLCHAIN_DIR}/bin/clang++")
set(CMAKE_ASM_COMPILER "${TRINIX_TOOLCHAIN_DIR}/bin/clang")

set(CMAKE_C_COMPILER_TARGET   "${TRINIX_TARGET_TRIPLE}")
set(CMAKE_CXX_COMPILER_TARGET "${TRINIX_TARGET_TRIPLE}")
set(CMAKE_ASM_COMPILER_TARGET "${TRINIX_TARGET_TRIPLE}")

# binutils replacements — one set, target-independent.
set(CMAKE_AR      "${TRINIX_TOOLCHAIN_DIR}/bin/llvm-ar")
set(CMAKE_RANLIB  "${TRINIX_TOOLCHAIN_DIR}/bin/llvm-ranlib")
set(CMAKE_NM      "${TRINIX_TOOLCHAIN_DIR}/bin/llvm-nm")
set(CMAKE_OBJCOPY "${TRINIX_TOOLCHAIN_DIR}/bin/llvm-objcopy")
set(CMAKE_OBJDUMP "${TRINIX_TOOLCHAIN_DIR}/bin/llvm-objdump")
set(CMAKE_STRIP   "${TRINIX_TOOLCHAIN_DIR}/bin/llvm-strip")
set(CMAKE_READELF "${TRINIX_TOOLCHAIN_DIR}/bin/llvm-readelf")

# LLD everywhere; the all-LLVM runtime (compiler-rt + libunwind + libc++) is the
# default, with libgcc_s/libstdc++ shipped only as compat libraries for .NET.
add_link_options(-fuse-ld=lld)

# Look for headers and libraries in the sysroot, but keep using the *host* for
# executables — cmake needs to run pkg-config, python, generators and so on.
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM BOTH)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)

set(ENV{PKG_CONFIG_SYSROOT_DIR} "${TRINIX_SYSROOT}")
set(ENV{PKG_CONFIG_LIBDIR} "${TRINIX_SYSROOT}/usr/lib/pkgconfig:${TRINIX_SYSROOT}/usr/share/pkgconfig")
