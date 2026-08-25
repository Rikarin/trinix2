# Trinix — Implementation Overview

A single-page reconciliation of **what the plan asks for**, **what exists in the repository**, and
**what is holding the rest up**.

Sources: every file under [`docs/plan/`](plan/), [`docs/app-bundles.md`](app-bundles.md),
[`docs/vixen-platform-contract.md`](vixen-platform-contract.md),
[`IMPLEMENTATION_PLAN.md`](../IMPLEMENTATION_PLAN.md), the recipes under [`base/recipes/`](../base/recipes/),
the pins in [`base/sources.json`](../base/sources.json), the projects in [`src/`](../src/), the
orchestrators in [`scripts/`](../scripts/) and [`.github/workflows/build.yml`](../.github/workflows/build.yml).

> **Where the sources disagree, the code wins.** Every row below is marked against the tree, and a
> divergence from a document is called out in the note and collected in [Part 3](#part-3--where-the-plan-and-the-tree-disagree).
> This file is the *only* place that carries per-subsystem status. The design documents say what a
> subsystem is meant to be and why; [`17-roadmap.md`](plan/17-roadmap.md) keeps the phase boundaries
> and their exit criteria. Three places recording the same thing is how they come to disagree — which
> is exactly what the register in Part 3 is a record of.

**Measured 2026-08-26** on macOS/arm64, at commit `488ca37`. Everything marked ✅ below was run, read
or both; every number in Part 1 came from a command in this working tree, not from a document. Where
a row is a judgement it says so.

## Legend

| | Meaning |
|---|---|
| ✅ | Built, tested, and gated |
| 🟡 | Partially built — the named half works, the rest is listed under *owed* |
| ⬜ | Not started — no project, no recipe, no script |
| ⛔ | Blocked — cannot start until something else exists or a decision is made |
| 📄 | **Designed only.** A plan document exists and nothing in the tree implements it |

📄 is Trinix's own row, and it is most of this file. Twenty-two design documents describe roughly 192
engineer-months of work; the tree contains Phases 0–6, which those documents do not price.

---

# Part 1 — Feature inventory

## 1.1 Build, CI and the gates

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `scripts/build.ps1` — six stages (`host-tools`, `llvm`, `toolchain`, `app`, `base`, `image`), both arches | ✅ | [scripts/build.ps1](../scripts/build.ps1) | Docker BuildKit throughout; nothing installs on the host but pwsh and Docker |
| `scripts/check-solution.ps1` | ✅ | [scripts/check-solution.ps1](../scripts/check-solution.ps1) | Ran here: **"OK: all 19 projects under `src/` are in `Trinix.slnx`"**. A script, not a test, deliberately |
| `dotnet format whitespace src --folder --verify-no-changes` | ✅ | [.github/workflows/build.yml](../.github/workflows/build.yml) | Ran here: **exit 0**. ⚠ [16](plan/16-build-ci-and-testing.md) § The gates still carries a paragraph saying this gate is red; it was fixed in `75cb7d0` and the same document's own table explains the folder-mode fix two paragraphs above. See Part 3 |
| `dotnet build` as the analyser gate | ✅ | [src/Directory.Build.props](../src/Directory.Build.props) | `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `IsAotCompatible`, `Deterministic`, `InvariantGlobalization`, `net10.0`, SDK pinned to 10.0.301 in `global.json` |
| `dotnet test` — five test projects | ✅ | `src/*.Tests/` | Ran here: **419 passing, 0 failing**. Bundle 241, Sandbox 96, Management 33, Services.Contracts 32, Sdk.Generators 17. ⚠ [16](plan/16-build-ci-and-testing.md)'s table lists only the first two and totals 274 |
| `scripts/check-api.ps1` + `src/Trinix.ApiCheck` | ✅ | [scripts/check-api.ps1](../scripts/check-api.ps1) | Ran here: **"OK: 6 assemblies match"**. Covers `Trinix.Bundle`, `.Interop`, `.Management`, `.Platform`, `.Sandbox`, `.Services.Contracts`; opt-out by declaration. Reads the built assembly, so generated surface is covered by construction |
| `PublicAPI.Shipped.txt` | 🟡 | beside each of the six | Every `Shipped` file is empty and every promise lives in `Unshipped` — 1 163 lines across six libraries. Honest: Trinix has released nothing. `-Fold` is the ritual that moves it |
| `scripts/check-determinism.ps1` | ✅ | [scripts/check-determinism.ps1](../scripts/check-determinism.ps1) | Ran here: **"2 bundles, 80 files, identical across two builds"**. Separate `ArtifactsPath` per build, so the second build cannot pass by finding the first up to date. ⚠ The signature is not reproducible and must not be |
| The `dotnet` CI job | ✅ | workflow | check-solution → restore → format → build → test → CheckApi. No toolchain dependency, so it gates a PR |
| The `determinism` CI job | ✅ | workflow | Sibling of `dotnet` rather than a step in it: it builds everything twice from cold |
| `verify-pins` CI job | ✅ | workflow | `update-sources.ps1 -Verify` re-hashes all 61 pins against upstream on every push |
| `host-tools` CI job, both host arches | ✅ | workflow | `ubuntu-24.04` and `ubuntu-24.04-arm` |
| `toolchain` and `base-and-image` CI jobs | 🟡 | workflow | **Nightly and `workflow_dispatch` only** — `if: github.event_name == 'schedule' \|\| ...`. They do not gate a pull request, deliberately: LLVM plus two sysroots is hours |
| VM tier: `-Check`, `-LoginCheck`, `-GraphicsCheck`, `-AppCheck`, `-DataCheck` | 🟡 | [scripts/run-vm.ps1](../scripts/run-vm.ps1), [image/scripts/run-qemu.sh](../image/scripts/run-qemu.sh) | **Five checks exist; CI runs two.** `-AppCheck` (Phase 6's exit criterion) and `-DataCheck` (the Btrfs assertions) are not in the workflow. The README and `image/README.md` both describe only three or four of the five |
| **Container tier** | ⬜ | — | ⚠ **Does not exist.** There is no script, compose file, CI job or test that runs the rootfs as a container. [16](plan/16-build-ci-and-testing.md) says "the rootfs already runs as a container" and `IMPLEMENTATION_PLAN.md` § 3 says two-tier testing is "designed in from Phase 2"; the only tiers that exist are `dotnet test` and QEMU |
| **UI tier** (`Vixen.Ui.Testing`, headless) | ⛔ | — | The package is not in [`vendor/vixen/`](../vendor/vixen/). See 1.6 |
| `CheckAot` | ⬜ | — | ⚠ **No project sets `PublishAot`.** [`src/publish.sh`](../src/publish.sh) publishes all five components framework-dependent, and `Trinix.Compositor.csproj`'s own header explains why: NativeAOT cannot cross-compile between architectures, and one container serving both targets is what the tree is organised around. [03](plan/03-shell-and-window-management.md) says the compositor is NativeAOT and that this is "already true" |
| Screenshot goldens, latency gates, `trinix doctor`, sandbox escape suite | ⬜ | — | [16](plan/16-build-ci-and-testing.md) § The gates. None started |
| **Stage-image staleness gate** | ⬜ | — | Still owed. `Assert-HostToolsCurrent` ([scripts/build.ps1:173](../scripts/build.ps1)) rebuilds `host-tools`, but `base.Dockerfile` starts `FROM ${TOOLCHAIN_IMAGE}` — so a host-tools fix cannot reach the stage that compiles recipes. Doc 16 documents two incidents; nothing in the tree stops a third |
| ⚠ Nothing has run on Linux | — | — | Every measurement above, and every one in doc 16, is macOS/arm64. CI has never completed a run |

## 1.2 Toolchain and base system

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| One LLVM/Clang/LLD, both target triples | ✅ | [toolchain/scripts/build-llvm.sh](../toolchain/scripts/build-llvm.sh) | LLVM 21.1.0. `aarch64-trinix-linux-gnu` and `x86_64-trinix-linux-gnu` |
| Minimal cross-GCC, for glibc only | ✅ | [toolchain/scripts/build-sysroot.sh](../toolchain/scripts/build-sysroot.sh) | glibc 2.41. Rebuilt once to harvest `libgcc_s`/`libstdc++` as compat libraries for Microsoft's .NET binaries |
| `toolchain-sanity.sh` — static and dynamic C/C++ hello world under `qemu-user`, both arches | ✅ | [toolchain/scripts/toolchain-sanity.sh](../toolchain/scripts/toolchain-sanity.sh) | Also asserts Clang-built C++ uses libc++/libunwind rather than falling back to GCC's |
| **57 base recipes** | ✅ | [base/recipes/](../base/recipes/) | 58 directories, minus `_template`, which `all_recipes()` skips. [00](plan/00-vision-and-principles.md) says the count is "kept near sixty"; it is |
| **61 pinned sources**, version + sha256 | ✅ | [base/sources.json](../base/sources.json) | `trinix-fetch` is the only sanctioned download and fails hard on a digest mismatch |
| Pins with no recipe and no consumer | 🟡 | — | **Five: `busybox`, `pcre2`, `erofs-utils`, `squashfs-tools`, `tzdata`.** `mkfs.erofs`/`mksquashfs` come from Debian in `host-tools.Dockerfile` instead. [08](plan/08-settings-and-system-services.md) § Sound already flags `pcre2`; the other four are unflagged |
| Kernel 6.12.30, `make LLVM=1`, per-arch config | ✅ | [base/recipes/linux/](../base/recipes/linux/) | `trinix_check` asserts named symbols so an upstream defconfig change cannot quietly drop one |
| systemd 257 as PID 1 | 🟡 | [base/recipes/systemd/recipe.sh](../base/recipes/systemd/recipe.sh) | Built with a long list of `disabled`. The ones that cost something: `-Dseccomp`, `-Dpam`, `-Dpolkit`, `-Dopenssl`, `-Dtpm2`, `-Dlibcryptsetup`, `-Dacl`, `-Dnetworkd`, `-Dresolve`, `-Dbpf-framework`. [21](plan/21-what-systemd-does-not-do.md) audits them — see Part 3 for where its audit is wrong |
| glibc, coreutils, util-linux, shadow, bash, dash, kmod, e2fsprogs, dosfstools, btrfs-progs, iproute2, openssl, ca-certificates, xz/zlib/zstd, ncurses, readline, expat, libffi, libcap, libxcrypt, hwdata | ✅ | [base/recipes/](../base/recipes/) | The LFS-shaped floor. Cross-built with Clang against the sysroot; `base-sanity.sh` asserts machine type and library closure for every shipped binary |
| SQLite 3.53.4 **with FTS5** | ✅ | [base/recipes/sqlite/recipe.sh](../base/recipes/sqlite/recipe.sh) | Built ahead of any consumer, for [06](plan/06-search.md)'s Beacon index. Nothing uses it yet |
| ICU 77.1 | ✅ | [base/recipes/icu/recipe.sh](../base/recipes/icu/recipe.sh) | Present because .NET's SDK and PowerShell need it, **not** for localisation. Trinix's own assemblies are `InvariantGlobalization=true` |
| D-Bus 1.16.0, system bus at `/run/dbus/system_bus_socket` | ✅ | [base/recipes/dbus/recipe.sh](../base/recipes/dbus/recipe.sh) | ⚠ Nothing Trinix-written connects to it. Its own header still says the menu bar will be a D-Bus interface, which [19](plan/19-menus-belong-to-applications.md) decided against |
| One font: DejaVu, seven faces | 🟡 | [base/recipes/dejavu-fonts/recipe.sh](../base/recipes/dejavu-fonts/recipe.sh) | At the path Vixen's `SystemFonts` probes. **No fallback face is registered**, which is one of [20](plan/20-localisation.md)'s reasons for English-only |
| Graphics: libdrm, libinput, libevdev, mtdev, libxkbcommon, xkeyboard-config, libdisplay-info, pixman, seatd, wayland 1.24, wayland-protocols, wlroots 0.19.3 | ✅ | [base/recipes/](../base/recipes/) | wlroots built `-Drenderers=[] -Dallocators=[] -Dbackends=drm,libinput -Dxwayland=disabled`. Software compositing through pixman into DRM dumb buffers, deliberately |
| Vulkan: loader, headers, glslang, Mesa 26.1.8 (**lavapipe only**) | ✅ | [base/recipes/mesa/recipe.sh](../base/recipes/mesa/recipe.sh) | `-Dgallium-drivers=[] -Dvulkan-drivers=swrast -Dopengl=false`. No GL, no EGL, no gbm. `trinix-vk-probe` walks the chain at boot and the graphics check asserts `llvmpipe` |
| PipeWire, WirePlumber, alsa, `iwd`, `nftables`, WireGuard, CUPS, Bluetooth, `fwupd`, `openssh`, `sudo`, PAM, `libfido2`, `tpm2-tss`, `cryptsetup`, `libseccomp`, X11 | ⬜ | — | None of these is a recipe or a pin. Several plan documents assume some of them are; see Part 3 |
| `trinix-system` — users, `/etc`, fstab, `XDG_RUNTIME_DIR`, the read-only-root symlink set | ✅ | [base/recipes/trinix-system/recipe.sh](../base/recipes/trinix-system/recipe.sh) | Users are baked in at build time: one `trinix` (shell `/usr/bin/pwsh`) and `root` (shell `bash`), both password `trinix`. There is no account management and no first-boot assistant |

## 1.3 The image and the A/B layout

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| GPT, four partitions | ✅ | [image/scripts/build-image.sh](../image/scripts/build-image.sh) | `trinix-esp` FAT32 256 MiB · `trinix-root-a` ext4 · `trinix-root-b` **zeros, no filesystem, no loader entry** · `trinix-data` Btrfs 2 GiB |
| Built without root and without a loop mount | ✅ | same | `mke2fs -d`, `mkfs.btrfs --rootdir`, `mtools`, `sgdisk`. That is why it runs in an unprivileged container |
| systemd-boot, with a rescue entry | ✅ | same | `EFI/BOOT/BOOT*.EFI` and `EFI/systemd/`; `trinix-a.conf` + `trinix-a-rescue.conf` |
| Read-only root; `/var`, `/home`, `/root`, `/Applications` as symlinks onto `/data` | ✅ | [base/recipes/trinix-system/recipe.sh](../base/recipes/trinix-system/recipe.sh) | The decision the whole update model rests on |
| `/data` on Btrfs with four subvolumes | ✅ | [image/scripts/build-image.sh](../image/scripts/build-image.sh) | `@home`, `@apps`, `@var`, `@containers`, plus `.snapshots`. Laid out to [10](plan/10-updates-recovery-and-backup.md) § Rewind's argument, ahead of any Rewind code. `CONFIG_BTRFS_FS`, `CONFIG_BTRFS_FS_POSIX_ACL` |
| `image-sanity.sh` and `base-sanity.sh` | ✅ | [image/scripts/](../image/scripts/) | Machine type and library closure for every binary; the firmware finds a loader that names a root partition that exists |
| Reproducible image assembly | 🟡 | same | Fixed UUIDs, `hash_seed`, `SOURCE_DATE_EPOCH`. ⚠ `mkfs.btrfs` still stamps something the script's own comment says will differ |
| **The B slot is empty** | 🟡 | same | Correct today — an empty partition is not a filesystem — but it means nothing has ever been written to slot B, and no updater exists to do it |
| **dm-verity** | ⬜ | — | `CONFIG_DM_VERITY=y` and `linux/recipe.sh` guards it. **Applied to nothing.** [image/README.md](../image/README.md) says so plainly and explains why: turning it on means writing the loader entry and flipping the slot, which is the updater's job |
| **A signed system image** | ⬜ | — | ⚠ `build-image.sh` writes `$IMAGE.sha256` and nothing else. There is no manifest, no signature and no verifier for the disk image. `scripts/build.ps1`'s own help text advertises the stage as "bootable, **signed** A/B disk image", and [10](plan/10-updates-recovery-and-backup.md) says "Phase 6 built image signing" — Phase 6 signed *application bundles* |
| **A recovery slot** | ⬜ | — | [10](plan/10-updates-recovery-and-backup.md) § Recovery wants a third small slot. The partition table is exactly four entries and the disk is truncated to the end of the fourth |
| Full-disk encryption | ⬜ | — | ⚠ No `cryptsetup` recipe, no pin, no `CONFIG_DM_CRYPT`, and systemd is `-Dlibcryptsetup=disabled`. Four documents describe it; see Part 3 |

## 1.4 .NET, PowerShell and the management surface

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| .NET 10 SDK + runtime in the image | ✅ | [base/recipes/dotnet/recipe.sh](../base/recipes/dotnet/recipe.sh) | Microsoft's official `linux-arm64`/`linux-x64` binaries against Trinix's glibc; the GCC compat libraries exist for exactly this |
| PowerShell 7.6.4 as the login shell for `trinix` | ✅ | [base/recipes/powershell/recipe.sh](../base/recipes/powershell/recipe.sh) | `root` keeps bash, deliberately: the account you fix pwsh from must not need pwsh |
| `trinixd` — the first C# system service | 🟡 | [src/Trinix.Daemon/](../src/Trinix.Daemon/) | 115 lines. `AddSystemd()`, `Type=notify`, journal severities, **one log line per boot**. Runs `DynamicUser=yes`, `ProtectSystem=strict`, `RestrictAddressFamilies=AF_UNIX`. ⚠ [02](plan/02-services-architecture.md) describes it as "root, system bus … mounts, power, updates, device policy". It is none of those and references no D-Bus |
| `Trinix.Management` — five cmdlets | ✅ | [src/Trinix.Management/](../src/Trinix.Management/) | `Get-TrinixSystem`, `Test-TrinixSystem`, `Get-TrinixService`, `Restart-TrinixService`, `Get-TrinixNetworkInterface`. 33 tests over `Cli` parsing, `ip -json` shapes and service health |
| `trinix-selftest.service` — `Test-TrinixSystem` at every boot | ✅ | [src/Trinix.Management/](../src/Trinix.Management/) | One row per check, including the compositor. This is where a future A/B updater gets its answer to "did the slot I just wrote work" |
| A terminal emulator | ⬜ | — | [12](plan/12-terminal-and-the-shell-language.md) is 📄 in full. The only console is the serial line and `/dev/tty2` |
| `Trinix.Management` onto the service contracts | ⬜ | — | The cmdlets shell out to `systemctl`/`ip`. [17](plan/17-roadmap.md) puts this in Phase 9 |

## 1.5 The compositor and the two protocol extensions

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `trinix-compositor`, C#, on `graphical.target`, `/dev/tty1`, seatd | ✅ | [src/Trinix.Compositor/](../src/Trinix.Compositor/) | 1 053 lines of C# over 1 164 lines of C. Runs as the `trinix` user with one group membership as its only privilege |
| `libtrinix-wlr` — the C half | ✅ | [base/recipes/trinix-wlr/](../base/recipes/trinix-wlr/) | Owns the wlroots objects and the listener plumbing C# cannot express; every decision is above it |
| `Trinix.Interop` — the P/Invoke surface | ✅ | [src/Trinix.Interop/](../src/Trinix.Interop/) | `Wlroots.cs` (541) and `WaylandClient.cs` (623). Both ends of the boundary, API-gated |
| Outputs, input devices, xdg-shell map/unmap, focus stack, click-to-focus | ✅ | [WindowManager.cs](../src/Trinix.Compositor/WindowManager.cs) | 618 lines and the whole of Trinix's window policy. "If a rule about window behaviour is not in `WindowManager.cs`, Trinix does not have that rule yet" |
| Cascade placement, three keybindings, compositor-driven move and resize, traffic-light hit-testing | ✅ | same | The bindings are `Alt`+`Escape` (exit), **`Alt`+`F1`** (cycle) and `Alt`+`Q` (close). ⚠ There is **no Super/logo/Command binding anywhere in the compositor**, and `CycleFocus` rotates over `_windows[0]` — the oldest window, not MRU and not application-scoped. [03](plan/03-shell-and-window-management.md) describes "⌘Tab cycling" |
| **Snapping, tiling, workspaces, the overview, minimise, screenshots, animations, per-monitor anything, remembered geometry, gestures, night light** | ⬜ | — | [03](plan/03-shell-and-window-management.md) is 📄 apart from the two rows above. `control_activated(minimise)` is delivered and the shell does nothing with it |
| `trinix-shell-v1` — decorations | ✅ | [base/recipes/trinix-protocols/protocol/trinix-shell-v1.xml](../base/recipes/trinix-protocols/protocol/trinix-shell-v1.xml) | `set_shadow`, `set_corner_radius`, `set_drag_region`, `set_resize_inset`, `set_control`/`unset_control`; events `control_activated`, `control_hover`, `shadow_applied`. Client draws, compositor drags, shadows and hit-tests |
| `trinix-menu-v1` — the global menu bar | ✅ | [base/recipes/trinix-protocols/protocol/trinix-menu-v1.xml](../base/recipes/trinix-protocols/protocol/trinix-menu-v1.xml) | `get_menu_bar` (scoped to the `wl_client`) **and** `get_toplevel_menu_bar` (the per-toplevel override) — so [19](plan/19-menus-belong-to-applications.md)'s ✅ is earned in the protocol. `insert`/`update`/`set_accelerator`/`remove`/`commit`; `activated`/`about_to_show`/`closed` |
| Menu model held, resolved per focus, accelerators matched | ✅ | [MenuBar.cs](../src/Trinix.Compositor/MenuBar.cs), [WindowManager.cs](../src/Trinix.Compositor/WindowManager.cs) | Two lists in the C shim (`client_menus`, `toplevel_menus`), `menu_for_toplevel()` as the three-line resolution, and a detach rather than a destroy when a toplevel with an override goes away. Replaced atomically on `commit`; `FindAccelerator` honours shortcuts whether or not the menu was opened |
| `about_to_show` / `closed` | 🟡 | [Wlroots.cs](../src/Trinix.Interop/Wlroots.cs) | Defined in the protocol, implemented in the shim, bound in `Wlroots.cs` — and **called from nowhere**. The only menu event ever sent is `activated`. So [19](plan/19-menus-belong-to-applications.md)'s lazy population is transported, not exercised |
| **A managed client that exports a menu** | ⬜ | — | ⚠ `TrinixMenu.ForApplication`/`ForWindow` have **no callers outside their own definitions and the API baseline**. The only client that has ever exported a menu is the C demo, [trinix-wl-demo/src/demo.c](../base/recipes/trinix-wl-demo/src/demo.c) `export_menu()`. `HelloUi` never mentions `TrinixMenu` |
| **A menu bar that draws** | ⬜ | — | `MenuEnd` calls `Log.Line(bar.Describe())` — the boot check reads a journal line. ⚠ Both the compositor's own comment and the platform contract say the blocker is "no font in the image"; a font *has* been in the image since Phase 5. **The blocker is that no shell process exists** |
| A shell process, dock, panel, control centre, wallpaper | ⬜ | — | 📄. [03](plan/03-shell-and-window-management.md), [17](plan/17-roadmap.md) Phase 9 |
| Fractional scaling, cursors, colour management | ⬜ | — | Integer scale only; `-Dcolor-management=disabled`; a cursor needs a renderer this compositor does not have |

## 1.6 `Trinix.Platform` and Vixen integration

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| Vixen's `IPlatform`, implemented for Trinix | ✅ | [src/Trinix.Platform/](../src/Trinix.Platform/) | 1 747 lines: `TrinixPlatform`, `TrinixWindow`, `TrinixDisplays`, `TrinixMenu`, `TrinixServices`, `EvdevKeys`. A Vixen application opens a window, takes keyboard and pointer input, and presents a Vulkan swapchain |
| `libtrinix-wl-client` — the C half | ✅ | [base/recipes/trinix-wl-client/](../base/recipes/trinix-wl-client/) | Exists because Vulkan's WSI takes a libwayland `wl_display` and calls libwayland on it; once the object is libwayland's, the marshalling belongs on that side |
| xdg-shell + both Trinix extensions, client side | ✅ | same | `TrinixMenu.ForApplication`/`ForWindow` marshal `trinix-menu-v1` — see 1.5 for the fact that nothing calls them |
| Vixen, pinned as 41 packages | 🟡 | [vendor/vixen/](../vendor/vixen/) | All at `0.1.0-trinix.6e46eee4180a`; `scripts/update-vixen.ps1` regenerates. The one Vixen change was `UiApplicationOptions.Platform`, a generic hook. ⚠ **The pin is 81 commits behind Vixen's `master`**, and two of the things Trinix asked upstream for have landed in those 81 — see Part 3 |
| **The clipboard** | ⬜ | [TrinixServices.cs](../src/Trinix.Platform/TrinixServices.cs) | `TrinixClipboard` returns `false` from every member. `wl_data_device_manager` is bound and unused. ⚠ [01](plan/01-system-sdk.md)'s table says "Windows, input, clipboard, DPI — implemented for Trinix already" |
| File pickers, cursors, window position, screen capture | ⬜ | — | The contract's own § "What Trinix does not provide yet" is accurate on all four |
| **Six Vixen packages the plan assumes are available** | ⛔ | [vendor/vixen/](../vendor/vixen/) | `Vixen.Ui.Markup`, `Vixen.Ui.Controls.Advanced`, `Vixen.Ui.HotReload`, `Vixen.Ui.Testing`, `Vixen.Audio(.Codecs)`, `Vixen.Video(.Codecs)` are **not vendored**. [01](plan/01-system-sdk.md) flags two of them. See Part 3 |
| **Vixen's accessibility tree** | 🟡 | [vendor/vixen/](../vendor/vixen/) | ⚠ **True of the pin, no longer true upstream.** Zero `Accessib*` symbols in the pinned `Vixen.Ui.dll`; but Vixen's `master` now carries `Core/Vixen.Ui/Accessibility.cs`, `UiElement.Role`/`AccessibleName`/`AccessibleState` and `UiDocument.AccessibilityInvalidated` (commit `ecf25aa3`). [18](plan/18-risks-and-open-questions.md) R6's ask was answered; Trinix's pin has not moved. Zero accessibility symbols in Trinix either way |

## 1.7 Bundles, signing, PKI and Gatekeeper

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| `.app` layout, `Info.json`, `Contents/{Bin,Resources,Frameworks,_Signature}` | ✅ | [src/Trinix.Bundle/](../src/Trinix.Bundle/) | `BundleLayout`, `BundleInfo`, `BundleScanner`. JSON not TOML, and [app-bundles.md](app-bundles.md) § "Why JSON and not TOML" says why |
| Merkle manifest, RFC-6962 shape, ordinal file order | ✅ | [MerkleTree.cs](../src/Trinix.Bundle/MerkleTree.cs), [BundleManifest.cs](../src/Trinix.Bundle/BundleManifest.cs) | |
| ECDSA-P256 detached signature over the literal manifest bytes | ✅ | [BundleSealer.cs](../src/Trinix.Bundle/BundleSealer.cs), [BundleVerifier.cs](../src/Trinix.Bundle/BundleVerifier.cs) | Pure .NET crypto. CMS was rejected — `System.Security.Cryptography.Pkcs` is not in the shared framework |
| X.509 chain validation, code-signing EKU, trust store in the image | ✅ | [TrustStore.cs](../src/Trinix.Bundle/TrustStore.cs), [DeveloperPki.cs](../src/Trinix.Bundle/DeveloperPki.cs) | Roots ship at `/usr/share/trinix/pki/roots`, part of the read-only image. There is deliberately no way to add one at runtime |
| `.tdi` — EROFS plus a 96-byte signed footer | ✅ | [DistributionImage.cs](../src/Trinix.Bundle/DistributionImage.cs) | `TRINIXDI` magic, `trinix-tdi/1\n` domain separation, 4 KiB blocks. Footers are assembled byte by byte in tests to reach the bounds checks |
| `trinix-bundle` — `pki init\|identity\|show`, `seal`, `pack`, `verify`, `install`, `uninstall`, `list`, `info` | ✅ | [src/Trinix.Bundle.Tool/](../src/Trinix.Bundle.Tool/) | ⚠ **No `update`, no `rollback`.** [13](plan/13-compatibility.md) § The Linux tier says both "exist today" |
| Install: loop-mount, staged rename, receipt at `/var/lib/trinix/bundles` | ✅ | [BundleInstaller.cs](../src/Trinix.Bundle/BundleInstaller.cs) | Root only. A user-level `~/Applications` install path does not exist, though [09](plan/09-applications-and-the-store.md) describes one |
| **fs-verity**, sealed at install, recorded in the receipt, asserted in the VM | ✅ | [FsVerity.cs](../src/Trinix.Bundle/FsVerity.cs) | `CONFIG_FS_VERITY=y`, guarded by `linux/recipe.sh`, checked by `run-qemu.sh --app-check`. **The one integrity mechanism that is real end to end** |
| `open` / `trinix-open` — verify then launch, in a session of its own | ✅ | [src/Trinix.Gatekeeper/](../src/Trinix.Gatekeeper/) | Verifies on *every* launch, not the first, and [app-bundles.md](app-bundles.md) § 7 argues why. `--wait` for a supervisor |
| Seven tamper cases refused | ✅ | [src/Trinix.Bundle.Tests/](../src/Trinix.Bundle.Tests/) | 241 tests. Flipped byte, file added, removed, renamed, mode-bit change, edited manifest, and a manifest **re-signed by a trusted signer** naming `../../etc/passwd` |
| Revocation | ⬜ | — | Not checked. [app-bundles.md](app-bundles.md) § "Revocation is not checked" states it |
| `tpkg`, repositories, a signed index, delta fetch, the Store | ⬜ | — | 📄. Zero code — `grep -rn tpkg src base image scripts docker` returns nothing |
| ⚠ `minimumSystemVersion` compares two ways | 🟡 | [SystemVersion.cs](../src/Trinix.Bundle/SystemVersion.cs) | Found by `SystemVersionTests`; **pinned, not changed**, pending [18](plan/18-risks-and-open-questions.md) R13. `0.3-rc1` fails a minimum of `0.3` and `0.3.1-rc1` passes it |

## 1.8 `Trinix.Sandbox` — what it can and cannot enforce

Doc 04's mechanism, built as *data*: a systemd unit plus a list of **gaps** naming what the unit does
not enforce and who would have to.

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| The unit, as a value | ✅ | [SandboxUnitBuilder.cs](../src/Trinix.Sandbox/SandboxUnitBuilder.cs), [SandboxUnit.cs](../src/Trinix.Sandbox/SandboxUnit.cs) | 2 614 lines across ten files. `RootDirectory`, `MountAPIVFS`, `TemporaryFileSystem`, `BindReadOnlyPaths`/`BindPaths`, `PrivateNetwork`, `PrivateDevices`, `DevicePolicy=closed`, `ProtectProc=invisible`, `NoNewPrivileges`, the three `ProtectKernel*`, `MemoryMax`/`CPUWeight`/`TasksMax`, and a deliberate absence of `PATH` |
| Eight kinds of gap | ✅ | [SandboxUnit.cs](../src/Trinix.Sandbox/SandboxUnit.cs) | `Broker`, `Compositor`, `SessionManager`, `SystemCapability`, `Inert`, `NoSuchProperty`, `Runtime`, `BundleFormat` |
| A property has **three** states | ✅ | [SandboxCapabilities.cs](../src/Trinix.Sandbox/SandboxCapabilities.cs) | `Rejected` (`SystemCallFilter`, `SystemCallArchitectures` — systemd refuses them without seccomp), `Inert` (`MemoryDenyWriteExecute`, `RestrictRealtime`, `RestrictSUIDSGID` — accepted and enforcing nothing, so they are emitted *and* recorded as gaps), `Enforced` |
| `systemctl --version` parsed, not assumed | ✅ | [SystemdProbe.cs](../src/Trinix.Sandbox/SystemdProbe.cs), [SystemdFeatures.cs](../src/Trinix.Sandbox/SystemdFeatures.cs) | The real banner from the booted VM is committed in `SystemdFeaturesTests` and pinned |
| 96 tests | ✅ | [src/Trinix.Sandbox.Tests/](../src/Trinix.Sandbox.Tests/) | Unit construction, gaps, rendering, preflight verdicts, user resolution, feature parsing, and two snapshots |
| **Three of the fourteen permissions change the unit** | 🟡 | [SandboxUnitBuilder.cs](../src/Trinix.Sandbox/SandboxUnitBuilder.cs) | `display` binds the compositor socket; `network.client`/`network.server` choose `PrivateNetwork=yes` versus a shared namespace. **Eleven have no property at all** — ten are the broker's and one is the session manager's. ⚠ [04](plan/04-sandbox-and-permissions.md) says ten; [app-bundles.md](app-bundles.md) says three-plus-ten-plus-one and is right |
| `open --sandbox` and `--print-unit` | 🟡 | [src/Trinix.Gatekeeper/Sandboxed.cs](../src/Trinix.Gatekeeper/Sandboxed.cs) | The file's own header: **"Nothing in this file has ever run."** Opt-in behind a flag; the default path is `Launcher.Spawn`/`Exec`, uncontained. The only unit that starts an application, `trinix-helloui.service`, uses `open --wait`, which cannot be combined with `--sandbox` |
| **The three prerequisites** | ⛔ | [SandboxPreflight.cs](../src/Trinix.Sandbox/SandboxPreflight.cs) | A composed root at `/usr/share/trinix/sandbox/root`, a shared netns at `/run/netns/trinix-apps`, and a privileged path to the system manager. **None is created anywhere in `base/` or `image/`**, so `--sandbox` cannot succeed on a real image today |
| The syscall floor | ⛔ | — | systemd is `-Dseccomp=disabled` and there is no `libseccomp` recipe. The kernel has `CONFIG_SECCOMP_FILTER=y`; systemd is what cannot use it |
| `PrivatePIDs` | ⬜ | [SandboxUnitBuilder.cs](../src/Trinix.Sandbox/SandboxUnitBuilder.cs) | Gated on `SandboxCapabilities.PrivatePids`, which defaults to `false` and is never set true. ⚠ [04](plan/04-sandbox-and-permissions.md) lists it among the properties that "succeeded" |
| `CONFIG_LANDLOCK` | ⬜ | [base/recipes/linux/config/trinix.config](../base/recipes/linux/config/trinix.config) | Absent in any form |
| The broker, portals, consent, audit mode, the escape suite | ⬜ | — | 📄. `trinix-broker` does not exist |

**The honest summary: an application on Trinix today is contained no more than any other process.**
[app-bundles.md](app-bundles.md) § 1 says exactly that, and it remains true — what changed is that
there is now an opt-in flag that would try.

## 1.9 `Trinix.Services.Contracts` and the generator

| Feature | Status | Where | Blocked by / note |
|---|---|---|---|
| Three service interfaces | ✅ | [src/Trinix.Services.Contracts/](../src/Trinix.Services.Contracts/) | `IClipboard`, `INotifications`, `ISystem` — and no others. `ServiceAttributes`, `ServiceErrors` |
| The three design decisions, pinned by tests | ✅ | [ContractSurfaceTests.cs](../src/Trinix.Services.Contracts.Tests/ContractSurfaceTests.cs) | Bulk data is a `SafeFileHandle`, never a byte array. A notification is `NotificationId` + `NotificationRequest` with no `app`/`id`/`timestamp`. `IClipboard` has **no history member**, asserted by reflection |
| `Trinix.Sdk.Generators` — one declaration, both ends | ✅ | [src/Trinix.Sdk.Generators/](../src/Trinix.Sdk.Generators/) | 1 895 lines. Emits `<S>Proxy.g.cs`, `<S>Handler.g.cs` and `<S>Introspection.g.cs` — the D-Bus introspection XML included, so a protocol change shows up as a baseline diff |
| The generator argues with you | ✅ | [ServiceDiagnostics.cs](../src/Trinix.Sdk.Generators/ServiceDiagnostics.cs) | Diagnostic ids tracked in `AnalyzerReleases.Shipped.md`. 17 tests, including contract-rule refusals |
| Round trips over an in-memory bus | 🟡 | [LoopbackBus.cs](../src/Trinix.Services.Contracts.Tests/LoopbackBus.cs) | 32 tests. ⚠ The loopback has **no file-descriptor passing**, so `IClipboard.ReadAsync`/`OfferAsync` — the members the fd decision exists for — are not exercised end to end anywhere |
| `Tmds.DBus.Protocol`, pinned exactly `[0.95.0]` | ✅ | [Trinix.Services.Contracts.csproj](../src/Trinix.Services.Contracts/Trinix.Services.Contracts.csproj) | [18](plan/18-risks-and-open-questions.md) R4 measured the AOT story before a contract was written, which is what the spike was for |
| **Nothing in production references it** | ⬜ | — | ⚠ The only consumers are its own tests and the generator's. `trinixd` has **no project reference at all**; [`src/publish.sh`](../src/publish.sh) never publishes the assembly, so it is not in the image. No Trinix process has ever put a message on the system bus |
| The standard face — portals, `org.freedesktop.Notifications`, Secret Service, AT-SPI | ⬜ | — | 📄. Zero code. This is [`docs/plan/README.md`](plan/README.md)'s rule 2 and it is entirely unbuilt |
| The settings store, device brokering, `trinix-shell`, `trinix-broker`, `trinix-secrets`, `trinix-beacon`, `trinix-settings`, `trinix-automation` | ⬜ | — | 📄 |

## 1.10 Everything with no code behind it

Whole documents whose implementation status is **📄 — designed only**. Listed so the ratio is visible,
because the ratio is the point.

| # | Document | What exists in the tree |
|---|---|---|
| [01](plan/01-system-sdk.md) | The System SDK | Nothing named `Trinix.Sdk`, `.Services`, `.Theme`, `.Controls`, `.Testing`, `.Tool`. No `trinix new/build/sign/run/doctor`. The generator (1.9) and the bundle tool (1.7) are the two pieces that landed |
| [02](plan/02-services-architecture.md) | Services architecture | Three contracts and a generator (1.9). No daemon, no bus traffic, no compat face, no settings store |
| [03](plan/03-shell-and-window-management.md) | Shell and window management | Focus, three `Alt` bindings, drag, resize (1.5). No shell process at all |
| [04](plan/04-sandbox-and-permissions.md) | Sandbox and permissions | A unit builder and a gap list (1.8). No enforcement in the launch path |
| [05](plan/05-identity-and-keychain.md) | Identity and keychain | `shadow` with yescrypt. **No keychain, no accounts code, no TPM, no PAM, no Secret Service.** The only "keychain" in `src/` is two comments |
| [06](plan/06-search.md) | Search (Beacon) | SQLite with FTS5 in the image. Nothing else |
| [07](plan/07-files-and-quick-look.md) | Files and Quick Look | Nothing |
| [08](plan/08-settings-and-system-services.md) | Settings and system services | Nothing. Network, sound, Bluetooth, printing and power have no recipe and no code |
| [09](plan/09-applications-and-the-store.md) | Applications and the Store | Install/verify/launch (1.7). **No repository, no `tpkg`, no Store, no update, no rollback** |
| [10](plan/10-updates-recovery-and-backup.md) | Updates, recovery, backup | The A/B *layout* and the Btrfs subvolumes (1.3). **No updater, no verity, no chunking, no Rewind, no recovery slot** |
| [11](plan/11-core-applications.md) | Core applications | Two reference bundles, `Hello` and `HelloUi`. Of the twenty applications, zero |
| [12](plan/12-terminal-and-the-shell-language.md) | Terminal and the shell language | pwsh as the login shell and five cmdlets (1.4). No terminal emulator |
| [13](plan/13-compatibility.md) | Compatibility | Nothing. No XWayland (`-Dxwayland=disabled` and no X11 stack at all), no Flatpak, no Wine, no runtimes |
| [14](plan/14-automation.md) | Automation | Nothing |
| [15](plan/15-accessibility.md) | Accessibility | **Nothing in Trinix** — one case-insensitive grep for `at-spi\|atspi\|accessib\|a11y\|orca` over the whole tree outside `docs/` returns a single hit, a planning sentence in `IMPLEMENTATION_PLAN.md`. No `at-spi2`, `atk`, `orca` or `speech-dispatcher` recipe. ⚠ The Vixen half has since been built upstream and Trinix's pin does not have it — see Part 3 § True of the pin |
| [20](plan/20-localisation.md) | Localisation | **Nothing.** No catalogue, no formatter, no logical-edge stylesheet. ICU is in the image for .NET's sake and Trinix's own assemblies are `InvariantGlobalization` |

---

# Part 2 — Effort, reconciled

[17](plan/17-roadmap.md) prices Phases 7–14 at **192 EM** and says out loud that this is sixteen years
for one person. That figure needs three adjustments before it is the number to plan against, and one
caveat about what "done" means.

| | EM | Source |
|---|---|---|
| Doc 17's Phases 7–14 | 192.0 | [17](plan/17-roadmap.md) § The phases |
| **+** doc 21's prerequisites, in no phase | **4.1** | [21](plan/21-what-systemd-does-not-do.md) § Recommendation. ⚠ The doc totals its own rows as 4.0; they sum to 4.10 |
| **+** doc 20's localisation, decided after doc 17 was written | **3.0** | [20](plan/20-localisation.md). Belongs in Phase 7, whose 21.5 predates it |
| **−** doc 10's Btrfs migration, already delivered | **1.0** | [10](plan/10-updates-recovery-and-backup.md) § Effort, still listed as owed |
| **Trinix's own remaining work** | **≈ 198** | |
| Upstream, in Vixen, not counted anywhere | 2–3 | [20](plan/20-localisation.md)'s `TextEditor`. ⚠ [18](plan/18-risks-and-open-questions.md) R6's ~2 EM of accessibility and doc 20's catalogue relocation **have both been built upstream since the pin was taken**, so they are no longer owed — they are un-consumed |

⚠ [18](plan/18-risks-and-open-questions.md) R1 says "190 engineer-months"; doc 17 and
[`plan/README.md`](plan/README.md) say 192. One of the three is wrong and R1 is the odd one out.

## What is actually done against that 198

**Phases 0–6 are built and doc 17 does not price them**, so none of the 198 has been consumed by the
compositor, the toolchain, the image or the bundle format. What has been consumed comes out of Phase 7
alone, and it is doc 16's floor rather than doc 01's SDK:

| Doc 16 line | EM budgeted | Landed |
|---|---|---|
| Test projects, harness, container-tier fixtures | 1.5 | 🟡 Five test projects and 419 tests exist. **The container tier does not**, and the UI tier is blocked on a Vixen package |
| Backfilling unit tests for what exists | 2.0 | 🟡 Bundle, sandbox, contracts, generator and management are covered. The compositor, `Trinix.Interop` and `Trinix.Platform` have **no tests at all** |
| The gates | 1.5 | 🟡 Four of five: solution, format, `CheckApi`, determinism. `CheckAot` is owed and nothing sets `PublishAot` |
| Screenshot goldens | 1.0 | ⬜ |
| Latency harness | 1.0 | ⬜ |
| Build orchestrator for `src/` | 1.0 | ✅ by doc 16's own test — every CI step is a command a developer can type |
| Doc 02's contracts and generator (inside Phase 7's 21.5) | — | 🟡 Three interfaces, a generator, and no daemon behind them |

**Judgement, not measurement: about 3 EM of 198 has landed, and roughly 195 remain.** That is 1.5 %,
and it is the most honest sentence in this file.

⚠ And note what "done" means in Phases 0–6 too. The bundle format, the sandbox unit builder, the
service contracts and the two Wayland extensions are *designed, written and unit-tested*; of the four,
only the bundle format has ever been exercised on a booted machine. The sandbox has never run. The
service contracts have never carried a message over a real bus. Neither has any of doc 16's remaining
gates ever been watched go green on Linux, because CI has never completed a run.

---

# Part 3 — Where the plan and the tree disagree

Every entry was checked against the file named. **The tree wins.** These belong in the documents
themselves; they are collected here so that the correction is one pass rather than twenty-two.

## Flatly false

| # | Document | Claim | The tree |
|---|---|---|---|
| 1 | [21](plan/21-what-systemd-does-not-do.md) § `-ACL`, "Two corrections to the premise" | "⚠ **There is no `CONFIG_BTRFS_FS_POSIX_ACL` and no Btrfs at all.** `grep -i btrfs` over the kernel fragment returns nothing" | `trinix.config:84` `CONFIG_BTRFS_FS=y` and `:90` `CONFIG_BTRFS_FS_POSIX_ACL=y`, **with a comment naming journald's ACL on `/var/log/journal` as the reason** — the exact need doc 21 then argues is unmet. Btrfs is also a recipe, a pin, `/data`'s actual filesystem, five fstab lines and 190 lines of `image/README.md`. The Btrfs commits precede the doc 21 commits |
| 2 | [21](plan/21-what-systemd-does-not-do.md) same bullet | "The fragment sets **only** `CONFIG_TMPFS_POSIX_ACL=y`" and "the ext4 `/data`" | Two errors in one sentence. See above; and `/data` is Btrfs, so the `EXT4_FS_POSIX_ACL` question it raises is moot. Half the 0.25 EM it prices is already shipped |
| 3 | [08](plan/08-settings-and-system-services.md) § Network | "networkd is **already in the base image** — zero new recipes for wired" | `base/recipes/systemd/recipe.sh:80` `-Dnetworkd=false`. `:81` `-Dresolve=false`, so the `systemd-resolved` row is also a rebuild rather than a configuration |
| 4 | [02](plan/02-services-architecture.md) § The daemon set | "`trinixd` (exists) \| **root, system bus** \| the privileged half: mounts, power, updates, device policy" | `trinixd.service` sets `DynamicUser=yes`, `ProtectSystem=strict`, `RestrictAddressFamilies=AF_UNIX`. `Trinix.Daemon.csproj` has no project reference and no D-Bus package. It logs one line at start and one at stop |
| 5 | [02](plan/02-services-architecture.md) § Devices, § Network and audio | "`trinixd` … directly against the kernel, logind and **networkd**"; "façades over `systemd-networkd`/**`iwd`** and **PipeWire**" | networkd is not built (above); `iwd` and `pipewire` are neither recipes nor pins |
| 6 | [13](plan/13-compatibility.md) § The Linux tier | "Every mechanism it needs — signature, verification, mount, install, **update, rollback** — exists today" | `trinix-bundle` has no `update` and no `rollback` verb. The other four are real |
| 7 | [01](plan/01-system-sdk.md) § Open | "**API stability.** Vixen gates its public surface with `PublicAPI.*.txt` and a `CheckApi` target. **Trinix has no equivalent**" | `scripts/check-api.ps1` + `src/Trinix.ApiCheck/` + six baseline pairs, run by the workflow at the `Public API baseline` step. Doc 16 documents it at length |
| 8 | [01](plan/01-system-sdk.md) § How the proxies are generated | the service interface is "declared once, in `Trinix.Services.Contracts`, **which both the daemon and the SDK reference**" | Neither exists. `Trinix.Daemon` has no project reference; there is no `Trinix.Sdk` project in `Trinix.slnx`'s 19 |
| 9 | [01](plan/01-system-sdk.md) § What already exists | "Windows, input, **clipboard**, DPI \| `IPlatform`, implemented for Trinix already" | `TrinixServices.cs` — `TrinixClipboard` returns `false` from every member, and its own comment says `PlatformCapabilities.Clipboard` is absent |
| 10 | [01](plan/01-system-sdk.md) § Paths | "`XDG_*` are set to point inside `~/Library`"; "`/System` is a symlink shim" | Neither exists. The only XDG variable set anywhere is `XDG_RUNTIME_DIR`. `~/Library` and `/Applications` *are* real |
| 11 | [app-bundles.md](app-bundles.md) § 6 | fs-verity works "which is why `build-image.sh` creates `/data` with **`mke2fs -O verity`**" | `/data` is Btrfs and the `-O verity` is gone; `build-image.sh:326` says so in a ⚠ comment. `CONFIG_FS_VERITY` (the half that matters) is correct |
| 12 | [10](plan/10-updates-recovery-and-backup.md) § System updates, diagram | root A / root B labelled "**EROFS + dm-verity**" | Both slots are ext4 (`mke2fs -q -t ext4`), with `rootfstype=ext4` on the kernel command line. EROFS is `.tdi` only |
| 13 | [10](plan/10-updates-recovery-and-backup.md) § System updates | "**Phase 6 built image signing**" | Phase 6 signed `.tdi` application bundles. The disk image gets a `.sha256` and nothing else — no manifest, no signature, no verifier. `scripts/build.ps1`'s help text repeats the error |
| 14 | [05](plan/05-identity-and-keychain.md) § Authentication row 1 | "Password \| yes \| **PAM, `pam_unix` with yescrypt**" | There is no PAM anywhere. `shadow` is built `--without-libpam` and `login` calls `crypt(3)` directly; `trinix_check` asserts it links `libcrypt.so.2`. Only the yescrypt half is true |
| 15 | [05](plan/05-identity-and-keychain.md) § Authentication, ¶ PAM | PAM is kept because "it is what `sshd`, `login`, `sudo` and `systemd-logind` all call" | No `openssh` recipe, no `sudo` recipe, and `shadow` was chosen *specifically* to avoid PAM — its own header says so |
| 16 | [IMPLEMENTATION_PLAN.md](../IMPLEMENTATION_PLAN.md) § 3 | "QEMU (`qemu-system-aarch64` **`-accel hvf`** runs near-native on Apple Silicon)" | `run-qemu.sh` passes no `-accel` at all, and `image/README.md` states the opposite outright: Docker Desktop does not pass virtualisation through, so it is TCG |
| 17 | [03](plan/03-shell-and-window-management.md) § The split, and the diagram | "The compositor stays NativeAOT and GC-free on the frame path — already decided … and **already true**"; the diagram labels `trinix-compositor  NativeAOT` | `Trinix.Compositor.csproj` sets no `PublishAot`, and its own twenty-line header says the opposite: framework-dependent, because NativeAOT cannot cross-compile between architectures. `src/publish.sh` publishes it framework-dependent. **No project in `src/` sets `PublishAot`** |
| 18 | [03](plan/03-shell-and-window-management.md) § Placement, focus, stacking | "Today: a cascade, click-to-focus, **⌘Tab cycling**, and a stacking list" | The three bindings are all `Alt`-based: `Alt`+`Escape`, `Alt`+`F1`, `Alt`+`Q`. There is no Super/logo/Command binding anywhere in the compositor, and `CycleFocus` raises the *oldest* window rather than the most recent |
| 19 | [03](plan/03-shell-and-window-management.md) § Supervision, session, and lock | "The session is `graphical.target` → compositor → shell, **as user units**" | `trinix-compositor.service` is a **system** unit (`User=trinix`, `RuntimeDirectory=user/1000`, `WantedBy=graphical.target`). There is no user unit, no shell unit and no shell. [08](plan/08-settings-and-system-services.md) § 3 states the underlying reason correctly: `-Dpam=disabled` means `user@1000.service` never starts |
| 20 | [19](plan/19-menus-belong-to-applications.md) § Doing it | "the protocol is version 1 … with **`HelloUi` as the only client that has ever exported a menu**" | `HelloUi/Program.cs` never mentions `TrinixMenu`. The only client that has ever exported a menu is the **C** demo, `trinix-wl-demo/src/demo.c` → `export_menu()`, which builds exactly the six items the ✅ block quotes. `TrinixMenu.ForApplication`/`ForWindow` have no callers outside their definitions and the API baseline |
| 21 | [vixen-platform-contract.md](vixen-platform-contract.md) closing ¶, and [WindowManager.cs](../src/Trinix.Compositor/WindowManager.cs) | "it cannot draw the words in it until **there is a font in the image**" | A font has been in the image since Phase 5, and the same document's own gap table says so ("a face is now installed at…"). The blocker is that **no shell process exists**. The stale reason is duplicated in the compositor's own comment |
| 22 | [15](plan/15-accessibility.md) § The bridge | "The bridge is **in the SDK**" | There is no `Trinix.Sdk` project. `src/` has `Trinix.Sdk.Generators` and its tests, and nothing else by that name |
| 23 | [README.md](../README.md) § Layout | "`src/` — All C#: compositor, platform backend, **shell**, apps, **package manager**, bundle/signing libraries" | Neither a shell nor a package manager exists as a project, a file or a binary |

## Stale — true when written, overtaken since

| # | Document | Claim | The tree |
|---|---|---|---|
| 24 | [16](plan/16-build-ci-and-testing.md) § The gates | "⚠ **One of the ✔ is red.** `dotnet format --verify-no-changes` exits 2 on `master`" | Fixed in `75cb7d0`. The folder-mode gate CI runs exits 0 here — and the same document's own gate table, two paragraphs above, already explains the fix. The document contradicts itself |
| 25 | [16](plan/16-build-ci-and-testing.md) § Where it stands | The test table lists `Trinix.Bundle.Tests` (241) and `Trinix.Management.Tests` (33) | There are five test projects and **419** tests: Bundle 241, Sandbox 96, Management 33, Services.Contracts 32, Sdk.Generators 17 |
| 26 | [16](plan/16-build-ci-and-testing.md) § What to build | "The container tier … **was designed in from Phase 2 — the rootfs already runs as a container**" | It does not. No script, compose file, job or test runs the rootfs as a container. `IMPLEMENTATION_PLAN.md` § 3 and § 5 make the same claim |
| 27 | [app-bundles.md](app-bundles.md) §§ 1, 7, 9 | "**Nothing calls it**"; the unit is "started by nothing"; "nothing in the launch path builds one" | `Trinix.Gatekeeper/Sandboxed.cs` builds it and hands it to `systemd-run`, behind `open --sandbox`. The accurate sentence is now the one that file uses: opt-in, and never yet executed |
| 28 | [04](plan/04-sandbox-and-permissions.md) § The mechanism | "⚠ `SandboxCapabilities.TrinixToday` gates on the wrong boundary … **three properties systemd would have accepted are being withheld**" | Already fixed in `d582d30`. `EnforcementOf` rejects two and marks three inert, and `Filtered` emits the inert three. The next section of the same document describes the fix |
| 29 | [10](plan/10-updates-recovery-and-backup.md) § Rewind, § Effort | "`/data` is Btrfs \| ⚠ **a change from the current image, which is ext4-shaped**"; "Btrfs migration of `/data` … **1.0 EM**" | Delivered. `/data` is Btrfs with the exact four subvolumes the document specifies |
| 30 | [21](plan/21-what-systemd-does-not-do.md) § `-LIBCRYPTSETUP` Verdict, § Recommendation | The LUKS work "collides with doc 10's Btrfs migration … so it should be done in the same pass **or not started**" | The Btrfs migration already happened, unencrypted. The image-assembly change it wanted to share has been spent |
| 31 | [image/README.md](../image/README.md) § Booting it | "three checks … **CI runs all three**" | Five checks exist (`-Check`, `-LoginCheck`, `-GraphicsCheck`, `-AppCheck`, `-DataCheck`); CI runs two. The root [`README.md`](../README.md) lists four |
| 32 | [19](plan/19-menus-belong-to-applications.md) § ⚠ Amends | "That scoping is carried through `MenuBar.cs`, which describes itself as 'the menu model **a window** exported', and `TrinixMenu.cs`, whose instances are **keyed on a window handle**" | Both describe the pre-amendment state, in the present tense. `MenuBar.cs:24` now says "an **application** exported"; `TrinixMenu.s_menus` is keyed on the **menu** handle, with the window kept only to identify an override. The ✅ header warns the reader; the body does not |
| 33 | [03](plan/03-shell-and-window-management.md) diagram | "`MenuModel` — the `trinix_menu_v1` tree, **per surface** (exists: `MenuBar.cs`)" | [19](plan/19-menus-belong-to-applications.md) changed this and the code followed: per `wl_client`, with a per-toplevel override |
| 34 | [IMPLEMENTATION_PLAN.md](../IMPLEMENTATION_PLAN.md) § 2, § 4 | `docker/ci.Dockerfile`; `src/Trinix.Init/`, `Trinix.Shell/`, `Trinix.Apps/`, `Trinix.Pkg/`; `vixen/` as a submodule; `Info.toml`; busybox in the base; PipeWire and virtio-gpu/venus in Phase 4; a Gatekeeper *service* validating on **first** launch; CMS | None of the paths exist; Vixen is `vendor/vixen/` as 41 committed `.nupkg`; JSON won; busybox was rejected for the shipped system; Mesa is swrast-only and there is no PipeWire; Gatekeeper is a CLI that verifies on **every** launch; CMS was rejected. § 4's Phases 7–8 are explicitly historical and exempt — these are in the live half |

## Undercounts and arithmetic

| # | Document | Claim | The tree |
|---|---|---|---|
| 35 | [01](plan/01-system-sdk.md) § What already exists | "⚠ **Two of these are not actually vendored**" — `Vixen.Ui.Controls.Advanced` and `Vixen.Ui.Markup` | **Six** are missing from the 41: those two plus `Vixen.Ui.HotReload`, `Vixen.Ui.Testing`, `Vixen.Audio(.Codecs)` and `Vixen.Video(.Codecs)`. `Vixen.Ui.Testing` is the one that stings — doc 16's UI tier and doc 01's own `.Testing` line both assume it |
| 36 | [04](plan/04-sandbox-and-permissions.md) | "**ten** of the fourteen have no systemd property behind them at all" | Eleven: ten broker, one session manager. [app-bundles.md](app-bundles.md) has it right. The same off-by-one is in the code's comments — `SandboxUnit.cs:88` and `SandboxUnitBuilder.cs:60` both say "four" where the builder consults three |
| 37 | [04](plan/04-sandbox-and-permissions.md) § Migration | "Trinix has **three** applications today" | Two: `io.trinix.hello` and `io.trinix.helloui`. [app-bundles.md](app-bundles.md) says two; `src/Trinix.Bundle/BundlePermissions.cs:159` repeats the error |
| 38 | [04](plan/04-sandbox-and-permissions.md) § The mechanism | "Every other property the builder emits succeeded: … **`PrivatePIDs`** …" | Never emitted. It is gated on `SandboxCapabilities.PrivatePids`, which defaults to `false` and is set true nowhere. `ProtectProc=invisible` delivers that row alone |
| 39 | [21](plan/21-what-systemd-does-not-do.md) § Recommendation | "Total not budgeted anywhere — **4.0**"; "the remaining **2.15 EM** — PAM, TPM, FIDO2" | The rows sum to **4.10**, and PAM+TPM+FIDO2 is 1.0 + 0.75 + 0.5 = **2.25**. [`plan/README.md`](plan/README.md) inherits the 4.0 |
| 40 | [21](plan/21-what-systemd-does-not-do.md) § How the finding was made, § Flag by flag | "**every one of them is an explicit `-D…=disabled`**, never a failed probe"; `-LIBARCHIVE` "declined explicitly, lines 108–121" | There is no `-Dlibarchive` option in the recipe at all — `-LIBARCHIVE` is unset, not declined. `-Dlibcurl` is line 107 and `-Dp11kit` line 73, not in the quoted range; `-Dsmack`/`-Dima` are `=false` |
| 41 | [18](plan/18-risks-and-open-questions.md) R1 | "Doc 17 says **190** engineer-months out loud" | Doc 17 and [`plan/README.md`](plan/README.md) both say 192 |
| 42 | [13](plan/13-compatibility.md) § XWayland | "wlroots implements it; **it is one recipe** plus compositor policy" | wlroots is built `-Dxwayland=disabled`, and there is no X11 anything in the tree — no `xorg-server`, `libxcb`, `libX11` or `xorgproto`, and no pins for them. It is a rebuild plus a stack |
| 43 | [12](plan/12-terminal-and-the-shell-language.md) § PowerShell as the administration surface | "which is fine for **four** cmdlets" | Five: `Get-TrinixSystem`, `Test-TrinixSystem`, `Get-TrinixService`, `Restart-TrinixService`, `Get-TrinixNetworkInterface`. The document's own code block names three, and omits the only mutating one |
| 44 | [20](plan/20-localisation.md) § Trinix, today | "**`InvariantGlobalization=true` applies to every Trinix project**, applications included" | `Directory.Build.props` sets it, but `Trinix.Sdk.Generators.csproj` explicitly clears it — the compiler host decides its own globalization. True of every *application*; not of every project |
| 45 | [20](plan/20-localisation.md) § Trinix, today | "The word 'localisation' appears in this repository **twice**" | The stem also appears in `ISystem.cs`, `INotifications.cs`, `BundleLayout.cs`, `BundlePermissions.cs` and docs 01, 08, 11, 16, 18 and `app-bundles.md`. The two it names are real; the count is not |

## True of the pin, no longer true of Vixen

A category of its own, because the document is not wrong about the tree — it is wrong about the
sibling repository, and the fix is a pin bump rather than an edit. **[`vendor/vixen/`](../vendor/vixen/)
is at `6e46eee4`, which is 81 commits behind Vixen's `master`**, and Trinix asked upstream for two
things that have since landed in those 81.

| # | Document | Claim | Vixen's `master` |
|---|---|---|---|
| 46 | [15](plan/15-accessibility.md) § Where it actually has to live; [18](plan/18-risks-and-open-questions.md) R6 | "There is **no accessibility surface in Vixen** — no `Role`, `AccessibleName`, `AutomationId` or accessibility namespace"; "it is upstream work, ~2 EM … asked for in Vixen's doc 46 as A2" | **Answered.** `Core/Vixen.Ui/Accessibility.cs` exists; `UiElement` carries role, name, value and state with `UiDocument.AccessibilityInvalidated` behind it (commit `ecf25aa3`), and Vixen's doc 09 now has an `## Accessibility` section. Verified absent from the pinned `Vixen.Ui.dll`, so the row above stays 🟡 until the pin moves |
| 47 | [20](plan/20-localisation.md) § Vixen already has the catalogue — in the wrong assembly | "`StringId`/`Strings`/`StringCatalog` … in **`Vixen.Editor.Ui`**"; "⚠ the catalogue is a field and **not a `Signal<T>`**"; the gap "promote them out of the editor; make the catalogue a signal" | **Both halves answered.** All three types are in `Core/Vixen.Ui/` — which *is* in Trinix's 41-package closure — and `StringCatalog`'s own remark now reads that the catalogue in use is a `Signal<T>`, "which is what makes a language change re-label a running interface" (commit `dc028e9e`) |
| 48 | [20](plan/20-localisation.md) § Text, layout and direction | "All **53** `CultureInfo` uses in the UI assemblies are `InvariantCulture`" | 135 `InvariantCulture` at `master`, and **one `CurrentCulture`** — `Vixen.Ui.Controls.Advanced/DataGrid.cs`. Worth re-checking whether `Controls.Advanced` was inside the set the count was taken over; it is not a package Trinix vendors |

⚠ This is [18](plan/18-risks-and-open-questions.md) R5's warning arriving from the pleasant
direction. The mitigation it names — "Trinix updates its pin deliberately and on its own schedule" —
is working; what it did not anticipate is that a deliberate pin also defers *good* news, and that
three documents would go on quoting a Vixen that no longer exists.

## Uncorrected instances of the full-disk-encryption claim

The finding is [21](plan/21-what-systemd-does-not-do.md)'s and it is **confirmed**: outside `docs/`,
the only hits for cryptsetup/LUKS/dm-crypt in the whole tree are `docker/host-tools.Dockerfile`
(`cryptsetup-bin`, on the *build* machine) and `base/recipes/systemd/recipe.sh` (`-Dlibcryptsetup=disabled`).
No recipe, no pin, no `CONFIG_DM_CRYPT`. Three of the six places have since been corrected in place.
**Three have not:**

| # | Document | Claim still standing |
|---|---|---|
| 49 | [05](plan/05-identity-and-keychain.md) § What it holds | "the **disk's recovery key escrow** if the user chooses" |
| 50 | [11](plan/11-core-applications.md) § Setup Assistant | "**disk encryption**" as a setup step |
| 51 | [11](plan/11-core-applications.md) § Disk Utility | "**LUKS**" |

## Soft claims worth tightening

| # | Document | Claim | Note |
|---|---|---|---|
| 52 | [00](plan/00-vision-and-principles.md) non-negotiable 6 | "wlroots has a headless backend, the rootfs runs as a container, and `wlr-screencopy` produces a PNG" | `wlr-screencopy` appears nowhere in the tree; nothing anywhere runs the compositor headless; the container tier does not exist (#26). wlroots is built `-Dbackends=drm,libinput`, and `WLR_BACKENDS` is set nowhere in the tree. The *capability* claims may hold — the *practice* does not |
| 53 | [00](plan/00-vision-and-principles.md) § Quality bars | "A session … fits in 400 MB. **This is checked**" | Nothing checks it. There is no latency or memory harness of any kind |
| 54 | [14](plan/14-automation.md) § Triggers | "⚠ **Every trigger is backed by something that already exists** — udev/`trinixd`, logind, systemd timers, inotify, **the service bus**" | `trinixd` publishes no events and there is no service bus. udev, logind and timers are real |
| 55 | [09](plan/09-applications-and-the-store.md) § Install and lifecycle | "`/Applications` (system, needs authorisation) and **`~/Applications`** (user, does not)" | `install --destination` exists but the installer loop-mounts, so it is root-only and says so |
| 56 | [08](plan/08-settings-and-system-services.md) § Network | "WireGuard is **a kernel config** and a key"; "Firewall: `nftables`" | Accurate as a description of the work, but neither `CONFIG_WIREGUARD` nor `CONFIG_NF_TABLES`/`CONFIG_NETFILTER` is in `trinix.config` today, so both are a kernel rebuild |
| 57 | [base/recipes/dbus/recipe.sh](../base/recipes/dbus/recipe.sh) header | D-Bus is here for "the Phase 5 global menu bar, **which is a D-Bus interface in every desktop that has one**" | [19](plan/19-menus-belong-to-applications.md) decided against D-Bus for the menu bar and `trinix-menu-v1` is a Wayland protocol. The recipe's other reasons stand |

---

# Part 4 — What is owed, by weight

Judgement throughout, and ordered by what unblocks the most.

1. **The container tier** (doc 16, inside 1.5 EM). It is the cheapest tier by a wide margin, it was
   claimed as existing in two documents, and the sandbox escape suite, the `tpkg` tests and the
   keychain tests all land there. Nothing else in doc 16's list has this ratio.
2. **The sandbox's three prerequisites** (doc 04, inside 24 EM). A composed root, a shared network
   namespace and a privileged path to the system manager. Until they exist, `Trinix.Sandbox` is 2 614
   lines that cannot be executed, and every month of delay makes doc 18's R3 migration cliff steeper
   against a growing application count.
3. **A `libseccomp` recipe and a systemd rebuild** (doc 21, 0.35 EM of flags). Five of the sandbox's
   properties are rejected or inert for one build option. This is the cheapest real increase in
   containment available.
4. **Moving the Vixen pin, and widening it to the six missing packages** — one
   `scripts/update-vixen.ps1` run, a compile, and a decision about closure size and trim surface that
   nobody has taken. It is 81 commits of catching up, it brings back Trinix's own two upstream asks
   (accessibility and the string catalogue), and it unblocks doc 16's UI tier, doc 07's Files, doc
   11's System Monitor and Text Edit, and every `.vxml` in the plan. It is also the only item in this
   list that resolves register rows — #35 and #46–#48 — with a build rather than an edit.
5. **The stage-image staleness gate** (doc 16). Two documented incidents, hours each, both invisible
   until something failed for an unrelated reason. It is a digest in an image label.
6. **`CheckAot`** (doc 16, inside 1.5 EM). Nothing sets `PublishAot` anywhere, so the AOT decision the
   compositor is designed around has never been proved by a publish — and doc 03 states it as fact.
7. **Correcting the register in Part 3.** Fifty-seven entries, most of them a sentence. Doc 21's Btrfs
   bullets (#1, #2) are the urgent ones, because they are the load-bearing premise of an audit that
   other documents now cite.
