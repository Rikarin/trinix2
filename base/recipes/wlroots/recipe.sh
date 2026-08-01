# shellcheck shell=bash
# wlroots — the parts of a Wayland compositor that are not policy.
#
# Modesetting, buffer allocation, input device handling, and correct
# implementations of two dozen protocols. The C# compositor drives all of it
# and owns every decision above it: where windows go, what has focus, what the
# keybindings do. That split is the reason wlroots is here at all — the
# protocol wire format is easy in C#, and DRM/KMS is years of work.
#
# ---------------------------------------------------------------------------
# There is no GL in this build, and that is a decision rather than an omission.
# ---------------------------------------------------------------------------
#
# wlroots' usual renderer is GLES2 through EGL, and its usual allocator is gbm.
# Both come from Mesa, and both need a DRM *render* node — a second device node
# that accepts GPU command submission. QEMU's virtio-gpu exposes one only when
# the host runs virglrenderer with a working GL context, and the host here is a
# container whose entire purpose is that the Mac needs nothing installed.
#
# So the render node does not exist, and a Mesa built for it would be an
# unexercised megabyte in an immutable image. Instead: `-Drenderers=[]` and
# `-Dallocators=[]`, which leaves wlroots' pixman renderer compositing
# in software into DRM dumb buffers — a configuration wlroots supports
# precisely because virtual machines are common. It is slow and it is correct,
# and correctness is what a Phase 4 exit criterion is made of.
#
# Mesa arrives when there is a render node worth having: real hardware, or a VM
# tier that can supply virgl. The compositor asks for a renderer by name
# through WLR_RENDERER, so that day is a recipe and an environment variable,
# not a rewrite.

RECIPE_SOURCE="wlroots"
RECIPE_DEPENDS="glibc-runtime wayland wayland-protocols libdrm pixman \
                libxkbcommon libinput libdisplay-info hwdata seatd systemd"

trinix_build() {
    meson setup "$BUILDDIR" "$SRCDIR" \
        --cross-file "$MESON_CROSS" \
        --native-file "$MESON_NATIVE" \
        --prefix=/usr \
        --buildtype=release \
        `# wlroots compiles itself with -Werror. Trinix builds it with a clang` \
        `# several major versions newer than the one this release was tested` \
        `# against, which is the same situation the kernel recipe is in and` \
        `# gets the same answer.` \
        -Dwerror=false \
        \
        `# See the header. No EGL, no gbm, no Mesa.` \
        -Drenderers=[] \
        -Dallocators=[] \
        \
        `# drm gives real modesetting; libinput gives real keyboards. The x11` \
        `# backend nests inside an X server, of which Trinix has none.` \
        -Dbackends=drm,libinput \
        -Dsession=enabled \
        \
        -Dxwayland=disabled \
        -Dexamples=false \
        `# Colour management is lcms2, and the compositor has no colour policy` \
        `# to apply yet.` \
        -Dcolor-management=disabled \
        `# libliftoff offloads composition onto hardware planes, which is a` \
        `# GPU optimisation for a build that has decided not to use the GPU.` \
        -Dlibliftoff=disabled \
        -Dxcb-errors=disabled

    meson compile -C "$BUILDDIR" -j "$JOBS"
    DESTDIR="$DESTDIR" meson install -C "$BUILDDIR" --no-rebuild
}

trinix_check() {
    local lib="$DESTDIR/usr/lib/libwlroots-0.19.so"
    [ -e "$lib" ] || { echo 'wlroots: libwlroots-0.19.so missing' >&2; return 1; }

    # The configuration above is load-bearing and silently degradable: meson
    # would happily build a wlroots with no DRM backend if libdisplay-info or
    # hwdata went missing, and the failure would surface as "no outputs found"
    # inside a VM at the end of a twenty-minute build.
    local symbols missing=''
    symbols="$(llvm-nm --defined-only --dynamic "$lib" | awk '{ print $NF }')"
    for symbol in wlr_drm_backend_create wlr_libinput_backend_create \
                  wlr_pixman_renderer_create wlr_drm_dumb_allocator_create \
                  wlr_session_create wlr_xdg_shell_create wlr_scene_create; do
        grep -qx "$symbol" <<<"$symbols" || missing="$missing $symbol"
    done
    if [ -n "$missing" ]; then
        echo "wlroots: built without$missing — check the meson options above" >&2
        echo 'wlroots: nearby symbols that were exported:' >&2
        grep -E 'session|backend_create|allocator_create' <<<"$symbols" | sed 's/^/  /' >&2
        return 1
    fi

    # And the converse: an EGL renderer that crept back in would link the image
    # against a Mesa that is not there.
    if llvm-readelf --dynamic "$lib" | grep -q 'libEGL\|libgbm'; then
        echo 'wlroots: links against EGL/gbm, which the image does not ship' >&2
        return 1
    fi
}
