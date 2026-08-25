# shellcheck shell=bash
# The Linux kernel, built with LLVM=1.
#
# This recipe is deliberately unlike the others. The kernel is freestanding: it
# does not use the sysroot, does not link against glibc, and must NOT pick up
# the per-triple clang config that every userland recipe relies on. `make
# LLVM=1 ARCH=<arch>` handles cross-compilation on its own — clang is a native
# cross compiler, so no CROSS_COMPILE prefix is needed, which is exactly the
# simplification the all-LLVM decision was chosen for.
#
# What it installs:
#   /usr/lib/modules/<release>/...   kernel modules
#   /usr/lib/trinix/vmlinuz          the kernel image, for image assembly
#   /usr/lib/trinix/kernel-release   the version string, so later stages need
#                                    not re-derive it

RECIPE_SOURCE="linux"
RECIPE_DEPENDS=""

trinix_build() {
    local fragment="$RECIPE_DIR/config/trinix.config"

    # The kernel itself links against nothing, but its *host* tools do:
    # certs/extract-cert wants libcrypto and asks pkg-config for it. The driver
    # points pkg-config at the target sysroot, which on an arm64 build machine
    # producing an arm64 target is almost right — the host tool links happily
    # against Trinix's libcrypto and then dies on an absolute path inside the
    # sysroot's libc.so linker script. Nothing in a kernel build wants the
    # target's pkg-config, so it is removed rather than redirected.
    unset PKG_CONFIG_SYSROOT_DIR PKG_CONFIG_LIBDIR

    # Out-of-tree (O=) because both architectures share one unpacked source.
    #
    # LLVM=1 selects clang plus the whole llvm-* binutils replacement set. The
    # kernel's build system is one of the few that genuinely wants LLVM=1 rather
    # than individual CC/LD assignments, and it is officially supported.
    local kmake=(make -C "$SRCDIR" O="$BUILDDIR" ARCH="$KERNEL_ARCH" LLVM=1)

    "${kmake[@]}" defconfig

    # merge_config.sh applies the fragment and reports anything the kernel
    # silently dropped — usually a symbol renamed between versions, which is
    # precisely the drift a full checked-in .config would hide.
    ( cd "$BUILDDIR" && "$SRCDIR/scripts/kconfig/merge_config.sh" -m .config "$fragment" )
    "${kmake[@]}" olddefconfig

    "${kmake[@]}" -j"$JOBS"
    "${kmake[@]}" -j"$JOBS" modules

    local release
    release="$("${kmake[@]}" -s kernelrelease)"

    # INSTALL_MOD_PATH=$DESTDIR/usr lands modules in /usr/lib/modules, which is
    # where a merged-/usr system expects them.
    "${kmake[@]}" INSTALL_MOD_PATH="$DESTDIR/usr" INSTALL_MOD_STRIP=1 modules_install

    # The kernel build leaves symlinks into the build and source trees; neither
    # exists on the target and both would dangle in the image.
    rm -f "$DESTDIR/usr/lib/modules/$release/build" \
          "$DESTDIR/usr/lib/modules/$release/source"

    install -d "$DESTDIR/usr/lib/trinix"
    local image
    case "$TRINIX_ARCH" in
        arm64)  image="$BUILDDIR/arch/arm64/boot/Image" ;;
        x86_64) image="$BUILDDIR/arch/x86/boot/bzImage" ;;
    esac
    [ -f "$image" ] || { echo "linux: no kernel image at $image" >&2; return 1; }

    install -m644 "$image" "$DESTDIR/usr/lib/trinix/vmlinuz"
    printf '%s\n' "$release" > "$DESTDIR/usr/lib/trinix/kernel-release"
    install -m644 "$BUILDDIR/.config" "$DESTDIR/usr/lib/trinix/kernel-config"

    echo "linux: built $release ($(du -h "$image" | cut -f1) image)"
}

trinix_check() {
    local release
    release="$(cat "$DESTDIR/usr/lib/trinix/kernel-release")"

    [ -s "$DESTDIR/usr/lib/trinix/vmlinuz" ] \
        || { echo 'linux: vmlinuz is missing or empty' >&2; return 1; }
    [ -d "$DESTDIR/usr/lib/modules/$release" ] \
        || { echo "linux: no modules directory for $release" >&2; return 1; }

    # The options this distribution cannot boot without. A defconfig change
    # upstream, or a symbol renamed between kernel versions, silently drops one
    # of these — and the failure then looks like a mysterious hang at boot.
    local config="$DESTDIR/usr/lib/trinix/kernel-config" missing=''
    local required=(
        CONFIG_VIRTIO_BLK CONFIG_VIRTIO_PCI     # the VM's disk
        CONFIG_DEVTMPFS_MOUNT                   # /dev before userspace
        CONFIG_CGROUPS CONFIG_SECCOMP_FILTER    # systemd will not start without
        CONFIG_DM_VERITY                        # the immutable base image
        CONFIG_EFI_STUB                         # how systemd-boot loads it
        CONFIG_EXT4_FS                          # the root filesystem, built in:
                                                # there is no initramfs to load
                                                # a module from
        CONFIG_BTRFS_FS                         # /data, and for the same
                                                # reason built in: /var lives
                                                # on it, so systemd needs it
                                                # before it could load anything
        CONFIG_BTRFS_FS_POSIX_ACL               # has no `default y` upstream,
                                                # unlike ext4's — and journald
                                                # sets an ACL on /var/log/journal
        CONFIG_INPUT_EVDEV                      # the Phase 4 compositor's only
                                                # route to a keyboard
        CONFIG_DRM_VIRTIO_GPU                   # and its only route to a screen
        CONFIG_VT                               # no VTs, no seat0, no session
        CONFIG_EROFS_FS                         # what a .tdi distribution image
        CONFIG_BLK_DEV_LOOP                     # is, and how it gets mounted
        CONFIG_FS_VERITY                        # what seals an installed app
    )
    for option in "${required[@]}"; do
        grep -qx "$option=y" "$config" || missing="$missing $option"
    done
    [ -z "$missing" ] || { echo "linux: kernel config lost:$missing" >&2; return 1; }
}
