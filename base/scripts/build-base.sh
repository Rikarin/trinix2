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
mkdir -p "$ROOTFS/usr/"{bin,lib,share,include} \
         "$ROOTFS/"{etc,var,run,proc,sys,dev,tmp,root}
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

build_recipe() {
    local name="$1"
    local stamp="$stampdir/$name.stamp"
    local destdir="$destroot/$name"

    local source_name depends
    source_name="$(recipe_field "$name" RECIPE_SOURCE)"
    depends="$(recipe_field "$name" RECIPE_DEPENDS)"

    # A recipe is rebuilt when its recipe.sh changes or its pinned version does.
    local key
    key="$(sha256sum "$RECIPES_DIR/$name/recipe.sh" | cut -d' ' -f1)"
    if [ -n "$source_name" ]; then
        key="$key $(trinix-fetch --version "$source_name")"
    fi
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

    (
        set -euo pipefail
        # shellcheck disable=SC1090
        . "$RECIPES_DIR/$name/recipe.sh"

        if [ -n "${RECIPE_SOURCE:-}" ]; then
            SRCDIR="$(trinix-extract "$RECIPE_SOURCE" "$srcroot")"

            # Patches are applied to the shared source tree, so they must happen
            # exactly once even though both architectures use that tree.
            if [ -d "$RECIPES_DIR/$name/patches" ] && [ ! -e "$SRCDIR/.trinix-patched" ]; then
                applied=0
                for patchfile in "$RECIPES_DIR/$name/patches"/*.patch; do
                    [ -e "$patchfile" ] || continue
                    step "patch: ${patchfile##*/}"
                    patch -d "$SRCDIR" -p1 < "$patchfile"
                    applied=$((applied + 1))
                done
                [ "$applied" -gt 0 ] && touch "$SRCDIR/.trinix-patched"
            fi
        else
            # Synthetic recipe: assembles its output from what is already in the
            # sysroot rather than from an upstream tarball.
            SRCDIR=''
        fi

        BUILDDIR="$objroot/$name"
        DESTDIR="$destdir"
        rm -rf "$BUILDDIR"
        mkdir -p "$BUILDDIR"
        export SRCDIR BUILDDIR DESTDIR

        cd "$BUILDDIR"
        trinix_build

        if declare -F trinix_check >/dev/null; then
            step 'running recipe checks'
            trinix_check
        fi
    )

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
install_recipe() {
    local name="$1" destdir="$2"
    rsync -a "$destdir/" "$SYSROOT/"
    rsync -a "$destdir/" "$ROOTFS/"
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

log "Base build complete for $TRINIX_ARCH: $(du -sh "$ROOTFS" | cut -f1) in $ROOTFS"
