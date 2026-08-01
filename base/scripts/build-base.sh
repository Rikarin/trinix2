#!/usr/bin/env bash
# build-base.sh <arm64|x86_64> [recipe...] — cross-build the base system.
#
# Executes the recipe contract documented in base/recipes/README.md: source each
# recipe.sh, resolve dependencies, build into a staging DESTDIR, then install
# that staging tree to two places.
#
#   $SYSROOT   headers, libraries and pkg-config files, so the *next* recipe can
#              link against what this one produced
#   $ROOTFS    what actually ships in the image
#
# Keeping those separate is what lets the image drop headers and static
# libraries later without breaking the build, and it means a recipe never has to
# know which of its outputs are for building versus for shipping.
#
# With no recipe names given, everything under base/recipes/ is built.

# shellcheck source=../../toolchain/scripts/trinix-toolchain-lib.sh
. /usr/local/lib/trinix/scripts/trinix-toolchain-lib.sh

trinix_set_arch "${1:?usage: build-base.sh <arm64|x86_64> [recipe...]}"
shift

RECIPES_DIR="${TRINIX_RECIPES:-/usr/local/lib/trinix/recipes}"
ROOTFS="${TRINIX_ROOTFS:-/opt/trinix/rootfs}/$TARGET_TRIPLE"

srcroot="$TRINIX_BUILD/src"
objroot="$TRINIX_BUILD/obj/$TRINIX_ARCH"
destroot="$TRINIX_BUILD/dest/$TRINIX_ARCH"
stampdir="$TRINIX_BUILD/stamps/$TRINIX_ARCH"
mkdir -p "$srcroot" "$objroot" "$destroot" "$stampdir"

# ---------------------------------------------------------------------------
# The rootfs uses the same merged-/usr layout as the sysroot.
# ---------------------------------------------------------------------------
#
# Note what is *not* created here: /var, /home and /root. The root filesystem
# is read-only, so those are symlinks onto the writable /data partition, and the
# trinix-system recipe installs them as such. A directory of the same name
# created here would be in the way when that recipe's staging tree is rsynced
# across, and rsync would refuse to replace it.
mkdir -p "$ROOTFS/usr/"{bin,lib,share,include} \
         "$ROOTFS/"{etc,run,proc,sys,dev,tmp,data,boot}
ln -sfn usr/bin "$ROOTFS/bin"
ln -sfn usr/bin "$ROOTFS/sbin"
ln -sfn usr/lib "$ROOTFS/lib"
ln -sfn bin     "$ROOTFS/usr/sbin"
[ "$TRINIX_ARCH" = 'x86_64' ] && ln -sfn usr/lib "$ROOTFS/lib64"
chmod 1777 "$ROOTFS/tmp"

# ---------------------------------------------------------------------------
# Recipe metadata.
#
# recipe.sh only *defines* things, so it is safe to source in a subshell purely
# to read its declarations — which is how dependencies are resolved without
# building anything.
# ---------------------------------------------------------------------------
recipe_field() {
    local name="$1" field="$2"
    (
        set +u
        # shellcheck disable=SC1090
        . "$RECIPES_DIR/$name/recipe.sh"
        printf '%s' "${!field}"
    )
}

all_recipes() {
    local dir
    for dir in "$RECIPES_DIR"/*/; do
        dir="${dir%/}"; dir="${dir##*/}"
        case "$dir" in _*) continue ;; esac   # _template and friends
        [ -f "$RECIPES_DIR/$dir/recipe.sh" ] && printf '%s\n' "$dir"
    done
}

# Depth-first topological sort. Small graphs, so clarity beats cleverness; a
# cycle is a bug in the recipes and should say so plainly rather than hang.
declare -A _visit_state=()
_order=()

resolve() {
    local name="$1" dep
    case "${_visit_state[$name]:-}" in
        done) return 0 ;;
        visiting) die "dependency cycle detected at recipe '$name'" ;;
    esac
    [ -f "$RECIPES_DIR/$name/recipe.sh" ] || die "no such recipe: $name"

    _visit_state[$name]=visiting
    for dep in $(recipe_field "$name" RECIPE_DEPENDS); do
        resolve "$dep"
    done
    _visit_state[$name]=done
    _order+=("$name")
}

# ---------------------------------------------------------------------------
# Build environment handed to every recipe.
#
# Note what is absent: no --target, no --sysroot, no -rtlib, no -fuse-ld. The
# triple-prefixed clang driver and its config file supply all of that, so a
# recipe looks like an ordinary autotools/meson cross build.
# ---------------------------------------------------------------------------
export CC="$TARGET_TRIPLE-clang"
export CXX="$TARGET_TRIPLE-clang++"
# LD is deliberately NOT exported. Linking goes through the compiler driver, and
# packages that do consult $LD expect a real linker rather than a driver —
# libtool in particular mis-detects when handed one.
export AR=llvm-ar
export RANLIB=llvm-ranlib
export NM=llvm-nm
export STRIP=llvm-strip
export OBJCOPY=llvm-objcopy
export OBJDUMP=llvm-objdump
export READELF=llvm-readelf
export PKG_CONFIG_SYSROOT_DIR="$SYSROOT"
export PKG_CONFIG_LIBDIR="$SYSROOT/usr/lib/pkgconfig:$SYSROOT/usr/share/pkgconfig"
export CMAKE_TOOLCHAIN="/usr/local/share/trinix/cmake/$TARGET_TRIPLE.cmake"
export MESON_CROSS="/usr/local/share/trinix/meson/$TARGET_TRIPLE.ini"
export TARGET_TRIPLE KERNEL_ARCH SYSROOT ROOTFS

# ---------------------------------------------------------------------------
# The meson native file.
#
# A cross build occasionally asks pkg-config about the *build* machine —
# `dependency('wayland-scanner', native: true)` for the code generator,
# `dependency('hwdata', native: true)` for a table that gets compiled in. Meson
# resolves those with whatever PKG_CONFIG_LIBDIR is in the environment, and the
# line above points that at the target sysroot, which is right for everything
# else and exactly wrong here. The symptom is not an error: meson reports the
# dependency as "not found" and the project quietly falls back to a default
# path belonging to the build distribution.
#
# Meson takes the host machine's search path from the cross file, so restoring
# the container's own path in a native file separates the two cleanly. Recipes
# with a build-machine dependency pass --native-file "$MESON_NATIVE".
# ---------------------------------------------------------------------------
MESON_NATIVE="$TRINIX_BUILD/meson-native.ini"
export MESON_NATIVE
{
    printf '# Generated by build-base.sh — the build machine, not the target.\n'
    printf '[properties]\n'
    # PKG_CONFIG_SYSROOT_DIR is exported for the target and leaks into native
    # lookups the same way PKG_CONFIG_LIBDIR does. Meson only overrides it from
    # a machine file that names one, so the build machine has to say out loud
    # that its root is the root.
    printf "sys_root = '/'\n"
    printf "pkg_config_libdir = ['/usr/local/share/pkgconfig', '/usr/local/lib/pkgconfig'"
    # pkg-config's own compiled-in default, asked for with the cross settings
    # out of the way so it answers for the container.
    IFS=: read -ra _pc_dirs < <(
        env -u PKG_CONFIG_LIBDIR -u PKG_CONFIG_SYSROOT_DIR pkg-config --variable pc_path pkg-config
    )
    for _pc_dir in "${_pc_dirs[@]}"; do printf ", '%s'" "$_pc_dir"; done
    printf ']\n'
} > "$MESON_NATIVE"

# trinix_freeze_autotools [dir] — stop make from regenerating configure.
#
# Release tarballs ship pre-generated autotools output. If those files end up
# looking older than configure.ac, make helpfully re-runs aclocal/autoconf —
# which then needs the full autotools *plus* whatever third-party m4 macros the
# project uses. kmod wants gtk-doc.m4 that way, and carrying a documentation
# toolchain in a cross-build container to satisfy a rule that should never fire
# is the wrong trade. Touching the generated files to now settles it.
trinix_freeze_autotools() {
    local dir="${1:-$SRCDIR}"
    find "$dir" \( -name 'aclocal.m4' -o -name 'configure' -o -name 'config.h.in' \
                -o -name 'Makefile.in' -o -name '*.m4' \) -exec touch {} + 2>/dev/null || true
}

# Source preparation. The default is "apply patches/*.patch in sorted order
# with -p1", which covers almost everything; a recipe whose source needs
# something a diff expresses badly — deleting a vendored header, regenerating a
# build system — defines trinix_patch instead and gets that run in its place.
patch_recipe() {
    local name="$1" srcdir="$2"

    set +e
    (
        set -euo pipefail
        # shellcheck disable=SC1090
        . "$RECIPES_DIR/$name/recipe.sh"

        SRCDIR="$srcdir"
        RECIPE_DIR="$RECIPES_DIR/$name"
        export SRCDIR RECIPE_DIR

        if declare -F trinix_patch >/dev/null; then
            step 'patch: trinix_patch'
            trinix_patch
        else
            for patchfile in "$RECIPE_DIR/patches"/*.patch; do
                [ -e "$patchfile" ] || continue
                step "patch: ${patchfile##*/}"
                patch -d "$SRCDIR" -p1 < "$patchfile"
            done
        fi
    )
    local rc=$?
    set -e

    # A half-patched tree is worse than no tree: it is shared between both
    # architectures and would be reused as-is on the next run.
    [ "$rc" -eq 0 ] || { rm -rf "$srcdir"; die "patching recipe '$name' failed"; }
}

# trinix_merge_usr — fold sbin directories in the staging tree into /usr/bin.
#
# Trinix has one binary directory. Upstreams that still separate "system"
# binaries install into $DESTDIR/usr/sbin or $DESTDIR/sbin regardless of
# --sbindir — shadow puts useradd there, systemd puts `init` there — and while
# install_recipe's rsync follows the rootfs symlink and puts them in the right
# place anyway, the staging tree, the per-recipe file list and the recipe's own
# trinix_check would all still describe a layout that does not ship. Calling
# this at the end of trinix_build keeps all four in agreement.
trinix_merge_usr() {
    local dir
    for dir in "$DESTDIR/sbin" "$DESTDIR/usr/sbin"; do
        [ -d "$dir" ] || continue
        install -d "$DESTDIR/usr/bin"
        find "$dir" -mindepth 1 -maxdepth 1 -exec mv -t "$DESTDIR/usr/bin/" {} +
        rmdir "$dir"
    done
}

build_recipe() {
    local name="$1"
    local stamp="$stampdir/$name.stamp"
    local destdir="$destroot/$name"

    local source_name depends
    source_name="$(recipe_field "$name" RECIPE_SOURCE)"
    depends="$(recipe_field "$name" RECIPE_DEPENDS)"

    # A recipe is rebuilt when anything it owns changes, or its pinned version
    # does. Everything it owns, not just recipe.sh: the kernel's config
    # fragment and a package's patches are inputs to the build in exactly the
    # same way, and hashing only the script means editing one of those changes
    # nothing at all — which is a long afternoon of wondering why.
    local key extra
    key="$(find "$RECIPES_DIR/$name" -type f -exec sha256sum {} + | sort | sha256sum | cut -d' ' -f1)"
    if [ -n "$source_name" ]; then
        key="$key $(trinix-fetch --version "$source_name")"
    fi
    # Sources the recipe fetches for itself. Three kinds of pin cannot be
    # expressed as RECIPE_SOURCE: one that differs per architecture (.NET and
    # PowerShell ship a separate tarball for each), one whose archive has no
    # top-level directory for the driver to unpack into, and one that is not an
    # archive at all (the CA bundle is a single .pem). Those recipes call
    # trinix-fetch themselves — and their pinned versions still have to reach
    # the stamp, or bumping one would rebuild nothing.
    for extra in $(recipe_field "$name" RECIPE_EXTRA_SOURCES); do
        key="$key $extra=$(trinix-fetch --version "$extra")"
    done
    # Skipping the *build* is safe; skipping the *install* is not.
    #
    # Stamps and staging trees live in a cache mount, but $SYSROOT and $ROOTFS
    # live in the image layer, which is empty every time the layer is rebuilt.
    # An up-to-date stamp therefore means "no need to compile", never "no need
    # to install" — so the install below runs unconditionally, from the staging
    # tree that persisted. If that tree is gone too, rebuild from scratch.
    if [ -f "$stamp" ] && [ "$(cat "$stamp")" = "$key" ] && [ -d "$destdir" ]; then
        printf '  %-18s up to date — reinstalling from staging\n' "$name"
        install_recipe "$name" "$destdir"
        return 0
    fi

    log "recipe: $name${source_name:+ ($source_name $(trinix-fetch --version "$source_name"))}"
    [ -n "$depends" ] && step "depends on: $depends"

    rm -rf "$destdir"
    mkdir -p "$destdir"

    # Source preparation happens outside the build subshell so that a failure
    # can clean up after itself — see the trap below.
    local srcdir=''
    if [ -n "$source_name" ]; then
        srcdir="$(trinix-extract "$source_name" "$srcroot")"

        # Patches are applied to the shared source tree, so they must happen
        # exactly once even though both architectures use that tree.
        if [ ! -e "$srcdir/.trinix-patched" ]; then
            patch_recipe "$name" "$srcdir"
            touch "$srcdir/.trinix-patched"
        fi
    fi

    # Why the recipe subshell is not simply `if ! ( ... ); then`:
    #
    # bash ignores `set -e` for any command in a condition position, and that
    # suppression propagates *into* a subshell placed there — its own `set -e`
    # notwithstanding. A recipe whose `make` failed would therefore carry on to
    # `make install` and `trinix_check`, and be recorded as successful as long
    # as the last command happened to succeed. libxcrypt is what exposed this:
    # its link failed, install re-ran the same failing link, and only the
    # recipe's own check caught it.
    #
    # Running the subshell as a plain command with errexit temporarily off in
    # the parent keeps the failure where it belongs — at the command that
    # failed — while still letting this function handle it.
    set +e
    (
        set -euo pipefail
        # shellcheck disable=SC1090
        . "$RECIPES_DIR/$name/recipe.sh"

        # A host-only recipe builds a tool that runs on the *build* machine, so
        # every cross setting the driver exported above is wrong for it. The
        # variables are reset rather than merely unset: an autotools build that
        # finds no $CC picks `cc` anyway, but one that finds a stale
        # PKG_CONFIG_LIBDIR silently searches the target's sysroot and links a
        # host tool against aarch64 libraries.
        if [ "${RECIPE_HOST_ONLY:-}" = '1' ]; then
            export CC=cc CXX=c++ AR=ar RANLIB=ranlib NM=nm STRIP=strip \
                   OBJCOPY=objcopy OBJDUMP=objdump READELF=readelf
            unset PKG_CONFIG_SYSROOT_DIR PKG_CONFIG_LIBDIR
            unset CMAKE_TOOLCHAIN MESON_CROSS
        fi

        # Empty for a synthetic recipe, which assembles its output from the
        # sysroot rather than from an upstream tarball.
        SRCDIR="$srcdir"
        BUILDDIR="$objroot/$name"
        DESTDIR="$destdir"
        # Recipes that carry auxiliary files — a kernel config fragment, a unit
        # file, a default configuration — need to find them.
        RECIPE_DIR="$RECIPES_DIR/$name"
        rm -rf "$BUILDDIR"
        mkdir -p "$BUILDDIR"
        export SRCDIR BUILDDIR DESTDIR RECIPE_DIR

        cd "$BUILDDIR"
        trinix_build

        if declare -F trinix_check >/dev/null; then
            step 'running recipe checks'
            trinix_check
        fi
    )
    local rc=$?
    set -e

    if [ "$rc" -ne 0 ]; then
        # A failed build can leave the *shared* source tree half-mutated —
        # autotools rewriting ltmain.sh while aclocal.m4 still comes from the
        # tarball is the case that motivated this, and it produces a libtool
        # version-mismatch on every subsequent run. Since the tree is shared
        # between architectures and reused across runs, discard it so the next
        # attempt starts from the verified tarball instead of inheriting the
        # wreckage of this one.
        if [ -n "$srcdir" ]; then
            step "discarding possibly-contaminated source tree ${srcdir##*/}"
            rm -rf "$srcdir"
        fi
        die "recipe '$name' failed"
    fi

    # Record what this recipe produced. Cheap, and the first question when two
    # recipes fight over a file is "who installed it".
    ( cd "$destdir" && find . \( -type f -o -type l \) | sed 's|^\.||' | sort ) \
        > "$stampdir/$name.files"
    step "built $(wc -l < "$stampdir/$name.files") file(s)"

    install_recipe "$name" "$destdir"
    printf '%s' "$key" > "$stamp"
}

# Into the sysroot (so later recipes can link against it) and into the rootfs
# (so it ships). rsync keeps symlinks and permissions intact.
#
# RECIPE_ROOTFS_ONLY exists for the one case where those two destinations
# genuinely disagree: configuration that ships but that nothing builds against.
# trinix-system installs /var as a symlink onto the writable partition, and the
# sysroot has a real /var that later recipes install into — copying one over
# the other would either fail or quietly break the rest of the build.
#
# --keep-dirlinks is what makes merged /usr work without every recipe having to
# know about it. Plenty of upstreams install "system" binaries into /usr/sbin —
# systemd puts `init` there, and the kernel's first exec depends on finding it —
# while the rootfs has /usr/sbin as a symlink to bin. Without -K rsync would
# replace that symlink with a real directory and quietly unmerge /usr; with it,
# the install follows the symlink and lands in /usr/bin, which is where a
# merged-/usr system wanted it in the first place.
install_recipe() {
    local name="$1" destdir="$2"

    # A host-only recipe produces a tool for the build container and nothing
    # for the target, so neither destination applies: it installs into the
    # container itself. wayland-scanner is the case this exists for. Building
    # it from the same pinned tarball as the target library — rather than
    # taking Debian's — is the point: the scanner emits C that is compiled
    # into target binaries, so a host package would be an unpinned input to
    # every Wayland component in the image.
    if [ "$(recipe_field "$name" RECIPE_HOST_ONLY)" = '1' ]; then
        rsync -a "$destdir/" /
        return 0
    fi

    if [ "$(recipe_field "$name" RECIPE_ROOTFS_ONLY)" != '1' ]; then
        rsync -aK "$destdir/" "$SYSROOT/"
    fi
    rsync -aK "$destdir/" "$ROOTFS/"
}

# ---------------------------------------------------------------------------
# Go.
# ---------------------------------------------------------------------------
requested=("$@")
if [ "${#requested[@]}" -eq 0 ]; then
    mapfile -t requested < <(all_recipes)
fi

for name in "${requested[@]}"; do resolve "$name"; done

log "Building ${#_order[@]} recipe(s) for $TRINIX_ARCH"
step "order: ${_order[*]}"
step "rootfs: $ROOTFS"

for name in "${_order[@]}"; do
    build_recipe "$name"
done

# ---------------------------------------------------------------------------
# The mutable directories.
#
# /var, /home and friends end up as symlinks onto /data, the only writable
# partition. But recipes install into /var perfectly legitimately — systemd
# wants /var/lib/systemd, dbus wants /var/lib/dbus — and a directory that
# arrives that way cannot simply be deleted: those files have to exist on the
# running system.
#
# So they become *factory* content, stored under /usr/share/factory/data, which
# is part of the immutable image and seeded into the data partition when the
# image is assembled. That is systemd's own convention for this problem, and it
# is the difference between a shipped file being present at first boot and
# being silently shadowed the moment /data is mounted.
#
# This is the driver's job rather than a recipe's for the same reason the
# merged-/usr skeleton above is: it is a property of the layout, and it has to
# happen after every recipe has had its say.
# ---------------------------------------------------------------------------
log 'Finalising the rootfs layout'
factory="$ROOTFS/usr/share/factory/data"
mkdir -p "$factory"
for dir in var home root srv opt Applications; do
    if [ -d "$ROOTFS/$dir" ] && [ ! -L "$ROOTFS/$dir" ]; then
        mkdir -p "$factory/$dir"
        rsync -a "$ROOTFS/$dir/" "$factory/$dir/"
        rm -rf "${ROOTFS:?}/$dir"
        step "/$dir was installed into — relocated to the factory image ($(du -sh "$factory/$dir" | cut -f1))"
    fi
    ln -sfn "data/$dir" "$ROOTFS/$dir"
done

log "Base build complete for $TRINIX_ARCH: $(du -sh "$ROOTFS" | cut -f1) in $ROOTFS"
