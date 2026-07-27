#!/usr/bin/env bash
# toolchain-sanity.sh <arm64|x86_64> — the Phase 1 exit criteria, as a test.
#
# "Both sysroots produce a static and dynamic hello world (C and C++) that runs,
# and identical recipe scripts work for both arches."
#
# Everything is executed under qemu-user, including for the container's native
# architecture, so the test is genuinely identical for both targets.

# shellcheck source=./trinix-toolchain-lib.sh
. /usr/local/lib/trinix/scripts/trinix-toolchain-lib.sh

trinix_set_arch "${1:?usage: toolchain-sanity.sh <arm64|x86_64>}"

work="$TRINIX_BUILD/sanity/$TARGET_ARCH"
rm -rf "$work"; mkdir -p "$work"
cd "$work"

clang="$TRINIX_TOOLCHAIN/bin/clang"
clangxx="$TRINIX_TOOLCHAIN/bin/clang++"
failures=0

case "$TARGET_ARCH" in
    arm64)  expect_machine='AArch64' ;;
    x86_64) expect_machine='X86-64'  ;;
esac

log "Toolchain sanity suite — $TARGET_ARCH ($TARGET_TRIPLE)"

cat > hello.c <<'EOF'
#include <stdio.h>
#include <string.h>
int main(void) {
    const char *msg = "hello from C";
    printf("%s (%zu bytes)\n", msg, strlen(msg));
    return 0;
}
EOF

# Exceptions and iostreams together exercise the parts of the C++ runtime that
# actually differ between libstdc++ and libc++/libunwind — a plain main() would
# pass even if the unwinder were broken.
cat > hello.cpp <<'EOF'
#include <iostream>
#include <stdexcept>
#include <string>
int main() {
    try {
        throw std::runtime_error("thrown and caught");
    } catch (const std::exception &e) {
        std::cout << "hello from C++ (" << e.what() << ")\n";
    }
    return 0;
}
EOF

# check <name> <binary> <expected substring in output>
check() {
    local name="$1" binary="$2" expected="$3"

    local machine
    machine="$("$TRINIX_TOOLCHAIN/bin/llvm-readelf" --file-header "$binary" \
               | awk -F: '/Machine:/ {gsub(/^ +/, "", $2); print $2}')"
    if [[ "$machine" != *"$expect_machine"* ]]; then
        printf '  \033[1;31mFAIL\033[0m %-24s wrong machine: %s (expected %s)\n' \
               "$name" "$machine" "$expect_machine"
        failures=$((failures + 1))
        return
    fi

    local output
    if ! output="$("$QEMU" -L "$SYSROOT" "./$binary" 2>&1)"; then
        printf '  \033[1;31mFAIL\033[0m %-24s did not run: %s\n' "$name" "$output"
        failures=$((failures + 1))
        return
    fi

    if [[ "$output" != *"$expected"* ]]; then
        printf '  \033[1;31mFAIL\033[0m %-24s unexpected output: %s\n' "$name" "$output"
        failures=$((failures + 1))
        return
    fi

    printf '  \033[1;32mok\033[0m   %-24s %s\n' "$name" "$output"
}

log 'Compiling'
# No --sysroot, no -rtlib, no -fuse-ld: the per-triple clang config supplies all
# of it. If these four commands need extra flags, the config files are wrong.
"$clang"    --target="$TARGET_TRIPLE" -O2 hello.c   -o c-dynamic
"$clang"    --target="$TARGET_TRIPLE" -O2 hello.c   -o c-static   -static
"$clangxx"  --target="$TARGET_TRIPLE" -O2 hello.cpp -o cxx-dynamic
"$clangxx"  --target="$TARGET_TRIPLE" -O2 hello.cpp -o cxx-static -static

log 'Running under qemu-user'
check 'C, dynamic'   c-dynamic   'hello from C'
check 'C, static'    c-static    'hello from C'
check 'C++, dynamic' cxx-dynamic 'thrown and caught'
check 'C++, static'  cxx-static  'thrown and caught'

# The all-LLVM runtime is a design decision, not an accident — assert the
# dynamic C++ binary really did pick libc++ and libunwind rather than silently
# falling back to the GCC compat libraries.
log 'Runtime library selection'
needed="$("$TRINIX_TOOLCHAIN/bin/llvm-readelf" --dynamic cxx-dynamic | awk '/NEEDED/ {print $NF}' | tr -d '[]')"
step "NEEDED: $(echo "$needed" | tr '\n' ' ')"
for lib in libc++.so libunwind.so; do
    if ! grep -q "$lib" <<<"$needed"; then
        printf '  \033[1;31mFAIL\033[0m expected %s in NEEDED\n' "$lib"
        failures=$((failures + 1))
    else
        printf '  \033[1;32mok\033[0m   links against %s\n' "$lib"
    fi
done
if grep -qE 'libstdc\+\+|libgcc_s' <<<"$needed"; then
    printf '  \033[1;31mFAIL\033[0m clang-built C++ fell back to the GCC runtime\n'
    failures=$((failures + 1))
fi

# The compat libraries must nonetheless be present in the sysroot, because
# Microsoft's .NET binaries need them at runtime.
log 'GCC compat libraries present for .NET'
for lib in libgcc_s.so.1 libstdc++.so.6; do
    if [ -e "$SYSROOT/usr/lib/$lib" ]; then
        printf '  \033[1;32mok\033[0m   %s\n' "$lib"
    else
        printf '  \033[1;31mFAIL\033[0m %s missing from sysroot\n' "$lib"
        failures=$((failures + 1))
    fi
done

echo
if [ "$failures" -ne 0 ]; then
    die "$failures sanity check(s) failed for $TARGET_ARCH"
fi
log "Sanity suite passed for $TARGET_ARCH"
