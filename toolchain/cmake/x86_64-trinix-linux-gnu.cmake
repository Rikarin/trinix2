# CMake toolchain file: cross-compile to Trinix/x86_64.
#
#   cmake -DCMAKE_TOOLCHAIN_FILE=/usr/local/share/trinix/cmake/x86_64-trinix-linux-gnu.cmake ...

set(TRINIX_TARGET_TRIPLE "x86_64-trinix-linux-gnu")
set(CMAKE_SYSTEM_PROCESSOR "x86_64")

# Baseline: x86-64-v2 (SSE4.2/POPCNT). Rules out pre-2009 CPUs, which is the
# right trade for a new distribution; v3 (AVX2) is tempting but excludes plenty
# of still-current low-power parts.
set(TRINIX_ARCH_FLAGS "-march=x86-64-v2")

include("${CMAKE_CURRENT_LIST_DIR}/trinix-common.cmake")

string(APPEND CMAKE_C_FLAGS_INIT   " ${TRINIX_ARCH_FLAGS}")
string(APPEND CMAKE_CXX_FLAGS_INIT " ${TRINIX_ARCH_FLAGS}")
