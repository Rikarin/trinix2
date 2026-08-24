# shellcheck shell=bash
# Mesa, built for exactly one driver: lavapipe.
#
# ---------------------------------------------------------------------------
# What changed since Phase 4 said there would be no Mesa
# ---------------------------------------------------------------------------
#
# base/recipes/wlroots/recipe.sh argues at length that Mesa has no place in an
# image whose only GPU is a virtio-gpu with no render node, and that argument is
# still correct *about the compositor*. wlroots is unchanged: it still
# composites through pixman into DRM dumb buffers, it still links no EGL, and
# its check still fails if that changes.
#
# What is new is a second consumer with a different requirement. Vixen renders
# its interface through a VulkanDevice and has no CPU path — see
# docs/vixen-platform-contract.md — so an application needs an ICD to start at
# all. lavapipe is the ICD that needs no GPU: it JITs shaders to machine code
# and rasterises on the processor. The premise "no render node, therefore no
# Mesa" turns out to have been "no render node, therefore no *hardware* Mesa".
#
# So: one gallium driver, one Vulkan driver, one window system, and no GL.
#
# ---------------------------------------------------------------------------
# The llvm-config problem, which is the interesting part of this recipe
# ---------------------------------------------------------------------------
#
# Mesa finds LLVM by running `llvm-config` and believing what it says. In a
# cross build there are two of them and both answers are wrong:
#
#   the container's        right architecture to *run*, wrong everything else —
#                          it reports /opt/trinix/toolchain, which is the build
#                          machine's LLVM, and Mesa would link the image against it
#   the sysroot's          right answers, wrong machine — it is a target binary
#                          and the build machine cannot execute it
#
# Meson searches the machine file first and PATH second, so leaving this alone
# means silently getting the first one. The shim below is the third option: run
# the *target's* llvm-config under qemu-user, then rewrite the /usr paths it
# reports into sysroot paths, which is precisely the translation a cross build
# needs and the only thing the target binary gets wrong. It is handed to meson
# through a second cross file rather than by putting it on PATH, so that the
# substitution is visible in the build log instead of being a search-order
# accident.

RECIPE_SOURCE="mesa"
RECIPE_DEPENDS="glibc-runtime llvm-runtime llvm-target libdrm wayland \
                wayland-protocols glslang expat zlib zstd"

trinix_build() {
    local qemu
    case "$TRINIX_ARCH" in
        arm64)  qemu='qemu-aarch64-static' ;;
        x86_64) qemu='qemu-x86_64-static'  ;;
        *) echo "mesa: unknown architecture '$TRINIX_ARCH'" >&2; return 1 ;;
    esac

    # See the header. Bash rather than sh for pipefail: without it the shim
    # reports success when llvm-config segfaults under emulation, and meson
    # reads an empty --libs as "LLVM has no components".
    local shim="$BUILDDIR/trinix-llvm-config"
    cat > "$shim" <<SHIM
#!/usr/bin/env bash
set -o pipefail
"$qemu" -L "$SYSROOT" "$SYSROOT/usr/bin/llvm-config" "\$@" | sed 's|/usr/|$SYSROOT/usr/|g'
SHIM
    chmod +x "$shim"

    # Prove it before meson depends on it: a shim that cannot run produces a
    # "LLVM not found" three hundred lines into a configure log.
    local reported
    reported="$("$shim" --version)" \
        || { echo 'mesa: the llvm-config shim does not run — is qemu-user present?' >&2; return 1; }
    echo "==> target llvm-config reports $reported"

    cat > "$BUILDDIR/llvm-cross.ini" <<INI
[binaries]
llvm-config = '$shim'
INI

    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --cross-file "$BUILDDIR/llvm-cross.ini" \
        --native-file "$MESON_NATIVE" \
        --prefix=/usr \
        --buildtype=release \
        \
        `# One driver, and gallium-drivers is deliberately empty. The swrast` \
        `# Vulkan driver *is* the llvmpipe rasteriser with a Vulkan frontend` \
        `# on it, and Mesa's own meson knows that: with_any_llvmpipe is` \
        `# "gallium llvmpipe OR swrast vk", so naming llvmpipe here as well` \
        `# would add a Gallium GL driver that nothing in this image can reach.` \
        -Dgallium-drivers=[] \
        -Dvulkan-drivers=swrast \
        -Dllvm=enabled \
        -Dshared-llvm=enabled \
        \
        `# No GL, and this is the wlroots argument surviving intact: the` \
        `# compositor cannot use a GL renderer without a render node, so an` \
        `# OpenGL that only Mesa's own tests would exercise is dead weight in` \
        `# an immutable image. Vixen asks for Vulkan and gets Vulkan.` \
        -Dopengl=false \
        -Dgles1=disabled \
        -Dgles2=disabled \
        -Degl=disabled \
        -Dglx=disabled \
        -Dglvnd=disabled \
        -Dgbm=disabled \
        \
        `# One window system, for the loader's reason: there is no X server.` \
        -Dplatforms=wayland \
        \
        `# Everything optional, answered. Left on auto these follow whatever` \
        `# happens to be in the sysroot when this recipe runs.` \
        -Dvideo-codecs=[] \
        -Dgallium-va=disabled \
        -Dgallium-rusticl=false \
        -Dlibunwind=disabled \
        -Dlmsensors=disabled \
        -Dvalgrind=disabled \
        -Dzstd=enabled \
        -Dbuild-tests=false

    meson compile -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR" --no-rebuild
}

trinix_check() {
    local icd="$DESTDIR/usr/share/vulkan/icd.d"
    local manifest
    manifest="$(find "$icd" -name 'lvp_icd.*.json' -print -quit 2>/dev/null)"
    [ -n "$manifest" ] \
        || { echo 'mesa: no lavapipe ICD manifest — the loader would find no driver' >&2; return 1; }

    # The manifest names the library by a path the *target* resolves, so the
    # check is that the named file exists in the staging tree, not that some
    # libvulkan_lvp.so does.
    local named
    named="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["ICD"]["library_path"])' "$manifest")"
    case "$named" in
        /*) named="$DESTDIR$named" ;;
        *)  named="$icd/$named" ;;
    esac
    [ -e "$named" ] \
        || { echo "mesa: the ICD manifest names $named, which was not installed" >&2; return 1; }

    # ⚠ The failure this recipe exists to prevent. If the llvm-config shim had
    # not worked, meson would have found the *container's* LLVM and linked
    # lavapipe against a build-machine library — which produces a driver that
    # loads on the developer's machine and not in the image.
    llvm-readelf --dynamic "$named" | grep -q 'libLLVM' \
        || { echo "mesa: $named does not link libLLVM — lavapipe was built without a JIT" >&2; return 1; }

    local want
    case "$TRINIX_ARCH" in
        arm64)  want='AArch64' ;;
        x86_64) want='X86-64'  ;;
    esac
    llvm-readelf --file-header "$named" | grep -q "$want" \
        || { echo "mesa: $named is not $want" >&2; return 1; }

    # And the converse of the no-GL decision, so that a future -Dopengl=true
    # has to be a deliberate edit rather than something that drifts in.
    if [ -e "$DESTDIR/usr/lib/libGL.so.1" ] || [ -e "$DESTDIR/usr/lib/libEGL.so.1" ]; then
        echo 'mesa: a GL library was installed — see the header for why there is none' >&2
        return 1
    fi
}
