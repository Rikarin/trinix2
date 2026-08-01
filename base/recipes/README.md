# Base recipes

One directory per upstream component. A recipe describes *how* to cross-build one
package; it never says *what version* — that lives in [`base/sources.json`](../sources.json),
so a version bump is a one-line change with a checksum next to it.

```
base/recipes/<name>/
├── recipe.sh          # required — build logic
└── patches/           # optional — *.patch, applied in sorted order
    └── 0001-....patch
```

## The contract

`recipe.sh` is sourced (not executed) by the stage driver, so it must only *define*
things. Required:

| Symbol | Kind | Meaning |
|---|---|---|
| `RECIPE_SOURCE` | var | Key in `base/sources.json` to fetch and unpack. |
| `RECIPE_DEPENDS` | var | Space-separated recipe names that must be installed into the sysroot first. |
| `trinix_build` | function | Configure + compile + install into `$DESTDIR`. |

Optional:

| Symbol | Kind | Meaning |
|---|---|---|
| `RECIPE_HOST_ONLY` | var | `1` if this builds a tool for the *build* machine, not the target. |
| `RECIPE_ROOTFS_ONLY` | var | `1` to install into `$ROOTFS` but not `$SYSROOT`. For configuration that ships and that nothing builds against — and specifically for a recipe whose layout contradicts the sysroot's, as `trinix-system` does by making `/var` a symlink. |
| `RECIPE_ARCH` | var | Restrict to `arm64` / `x86_64` when a component is genuinely arch-specific (rare — and a smell). |
| `trinix_patch` | function | Replaces the default "apply `patches/*.patch` with `-p1`". For source surgery a diff expresses badly — deleting a vendored header, regenerating a build system. Runs once for the shared source tree, before either architecture builds. |
| `trinix_check` | function | Post-install assertions, run against `$DESTDIR`. Prefer cheap and specific: "is this the right machine type", not "does the test suite pass". |

Helpers the driver provides, callable from `trinix_build`:

| Helper | Use it when |
|---|---|
| `trinix_freeze_autotools [dir]` | `make` tries to re-run `aclocal`/`autoconf` on a release tarball. It then demands third-party m4 macros (kmod wants `gtk-doc.m4`) that have no business in a cross-build container. Touches the generated files so the rule never fires. |
| `trinix_merge_usr` | Upstream installs into `$DESTDIR/usr/sbin` or `$DESTDIR/sbin` whatever `--sbindir` says — shadow does it with the account tools, systemd with `init`. Folds them into `/usr/bin`, so the staging tree, the file list and `trinix_check` all describe the layout that ships. |

## Environment a recipe can rely on

| Variable | Example | Notes |
|---|---|---|
| `TARGET_TRIPLE` | `aarch64-trinix-linux-gnu` | Pass to `clang --target=`. |
| `TRINIX_ARCH` | `arm64` | Trinix arch name. |
| `KERNEL_ARCH` | `arm64` | Kernel `ARCH=`. |
| `SYSROOT` | `/opt/trinix/sysroots/aarch64-trinix-linux-gnu` | Headers/libs of already-built dependencies. |
| `DESTDIR` | `/build/dest/<name>` | Staging root — install here, **never** into `$SYSROOT` directly. |
| `SRCDIR` | `/build/src/glibc-2.41` | Unpacked source. Empty for a synthetic recipe (`RECIPE_SOURCE=""`), which assembles its output from the sysroot instead of a tarball. |
| `RECIPE_DIR` | `base/recipes/linux` | This recipe's own directory — for config fragments, unit files and other auxiliary content it ships. |
| `BUILDDIR` | `/build/obj/<name>` | Out-of-tree build directory, already `cd`'d into. |
| `JOBS` | `10` | Parallelism for `make -j`. |
| `CMAKE_TOOLCHAIN` | `/usr/local/share/trinix/cmake/<triple>.cmake` | For cmake-based components. |
| `CC` `CXX` `AR` `RANLIB` `NM` `OBJCOPY` `STRIP` `READELF` | preset | Already point at clang/LLVM with `--target` and `--sysroot` applied. |

Recipes must not reach the network — `trinix-fetch` is the only sanctioned way in,
and it verifies a pinned digest. Recipes must not write outside `$DESTDIR`.

### Do not export a variable GNU make already means something by

The arch variable is `TRINIX_ARCH`, not `TARGET_ARCH`, for a concrete reason:
make's built-in link rule is `LINK.o = $(CC) $(LDFLAGS) $(TARGET_ARCH)`, and make
imports the environment. An exported `TARGET_ARCH=arm64` silently appends a bare
`arm64` to implicit link commands, and the build dies far away with
`ld: cannot find arm64`. The same trap exists for `CFLAGS`, `LDFLAGS`, `ARCH`
(the kernel's), and `MAKEFLAGS` — prefer a `TRINIX_`-prefixed name whenever the
obvious one is something a build system might already own.

## LLD is stricter than GNU ld

Trinix links with LLD everywhere, and LLD rejects several things GNU ld accepts
silently. When a package builds under a normal distro but not here, check this
first — the symptom is usually not a link error but a *silently reduced* build:

- **`--no-undefined-version` is LLD's default** (since v17). A version script
  naming a symbol that isn't defined is an error, not a warning. zlib's
  configure trips on exactly this and concludes the compiler cannot build shared
  libraries at all, quietly shipping only `libz.a`. Fix per-recipe with
  `-Wl,--undefined-version`; do not set it globally, because the diagnostic is
  worth having.
- **`$LD` is not exported** by the driver. Linking goes through the compiler
  driver, and packages that consult `$LD` want a real linker — handing them
  `clang` makes libtool in particular mis-detect.

## The build container is not the target

Two ways the container has leaked into a cross build so far. Both produced a
build that *worked* and shipped something wrong, which is the expensive kind.

- **`*-config` scripts on `$PATH`.** util-linux runs `ncursesw6-config` to
  learn how to link ncurses and finds Debian's, which answers
  `-lncursesw -ltinfo` because Debian splits terminfo into its own library.
  Trinix's ncurses does not, so the link fails on a library that never
  existed. Point the variable at the sysroot's copy
  (`NCURSESW6_CONFIG=$SYSROOT/usr/bin/ncursesw6-config`); pkg-config is safe
  because the driver scopes `PKG_CONFIG_LIBDIR` to the sysroot already.
- **Auto-detected optional dependencies.** A recipe that lets `configure`
  decide builds differently depending on which recipes happened to run before
  it. util-linux linked ncurses or not depending on whether ncurses was already
  in the sysroot — so the rootfs differed between a warm cache and a clean
  build. Answer every optional dependency explicitly, the way the systemd
  recipe does, and add it to `RECIPE_DEPENDS` so the ordering is a fact rather
  than an accident.

## Why so few knobs

The immutable A/B image model means the base set stays at ~40–60 components. That
budget is what makes an all-Clang, two-architecture, from-scratch build tractable at
all, so a recipe that needs unusual machinery is a signal to reconsider whether the
component belongs in the base image or in an app bundle.

See [`_template/recipe.sh`](_template/recipe.sh) for a starting point.
