# CMake toolchain file: cross-compile to Trinix/arm64.
#
#   cmake -DCMAKE_TOOLCHAIN_FILE=/usr/local/share/trinix/cmake/aarch64-trinix-linux-gnu.cmake ...

set(TRINIX_TARGET_TRIPLE "aarch64-trinix-linux-gnu")
set(CMAKE_SYSTEM_PROCESSOR "aarch64")

# Baseline: ARMv8.2-A. Chosen to cover Apple Silicon under virtualisation and
# every server/laptop ARM64 part worth supporting, while staying below the
# features that would exclude Cortex-A55-class hardware.
set(TRINIX_ARCH_FLAGS "-march=armv8.2-a")

include("${CMAKE_CURRENT_LIST_DIR}/trinix-common.cmake")

string(APPEND CMAKE_C_FLAGS_INIT   " ${TRINIX_ARCH_FLAGS}")
string(APPEND CMAKE_CXX_FLAGS_INIT " ${TRINIX_ARCH_FLAGS}")
