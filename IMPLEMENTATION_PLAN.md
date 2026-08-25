# Trinix — Implementation Plan

A macOS-like Linux distribution, built from scratch (LFS-style), targeting **ARM64 and x86_64 from day one**, with:

- **PowerShell** as the native interactive shell
- **.NET / C# (latest)** as the primary development language
- **systemd** as PID 1
- A **custom C# package manager** built around a macOS-style app-bundle model
- **Wayland** with a **custom compositor/window manager written in C#**
- All UI applications built on the in-development **Vixen engine**
- A **.app-like bundle format**, a **.dmg-like distribution image**, and **app signing**
- The whole build runs inside **Docker**, so nothing needs installing on the host Mac

---

## 1. Guiding architecture decisions

These are made up front so every phase builds on them:

| Decision | Choice | Rationale |
|---|---|---|
| C library | **glibc** | systemd requires it in practice; .NET and Mesa are best-tested against it. musl would fight us the whole way. |
| Kernel | Mainline LTS Linux | Custom config per arch; no reason to fork. |
| Boot | UEFI + systemd-boot | Simple, works identically in QEMU/UTM on both arches. |
| Toolchain | **LLVM/Clang + LLD primary; GCC only for glibc** | Clang is inherently a cross-compiler — one LLVM build targets both arches, collapsing the dual cross-GCC bootstrap. Kernel builds officially with `LLVM=1`; .NET is Clang-built upstream. glibc requires GCC, so a minimal GCC exists solely to build glibc + crt files. |
| C++/compiler runtime | compiler-rt + libunwind + libc++ for Clang-built components | Coherent all-LLVM runtime. `libgcc_s.so.1` + `libstdc++.so.6` are still shipped as compat libraries because Microsoft's official .NET binaries expect them. |
| Build orchestration | Docker BuildKit (`docker buildx`) multi-stage, multi-platform | Reproducible, cacheable, runs on the Mac with zero host tooling. |
| x86_64 builds on the Mac | **Cross-compile from an arm64 build container** | Running x86_64 containers under QEMU emulation is 10–20× slower; with Clang, cross-compiling both arches from one container is the natural mode anyway. |
| Compositor strategy | C# compositor on **wlroots via C bindings first**, pure-managed protocol implementation as a later replaceable layer | Wayland's wire protocol is easy in C#; DRM/KMS modesetting, GPU buffer management, and input handling are not. wlroots buys years. The C# side owns all policy (window management, animations, shell UI). |
| Base OS delivery | **Immutable, signed A/B system images**; apps live outside the base image | This is the macOS model and makes the custom package manager tractable — it manages apps and image updates, not 30,000 interdependent packages. |
| `/bin/sh` | Keep a POSIX shell (dash or bash) installed | Thousands of build scripts, systemd generators, and third-party tools hardcode `#!/bin/sh`. PowerShell is the *user-facing* shell, not a POSIX replacement. |

---

## 2. Repository layout

```
Trinix/
├── docker/               # Build environment definitions
│   ├── host-tools.Dockerfile     # Stage 0: build container (gcc, make, python, etc.)
│   └── ci.Dockerfile
├── toolchain/            # LLVM/Clang toolchain build + per-arch sysroots; mini-GCC for glibc
├── base/                 # Base system: package build recipes (kernel, glibc, systemd, ...)
│   └── recipes/<name>/   # source URL, sha256, patches, build script
├── image/                # Rootfs assembly, ESP layout, A/B image + dm-verity tooling
├── src/                  # All C#/.NET source (one solution)
│   ├── Trinix.Init/              # systemd units, session/seat config (not code — config)
│   ├── Trinix.Compositor/        # Wayland compositor + WM ("Aurora"?)
│   ├── Trinix.Shell/             # Dock, menu bar, desktop (Vixen apps)
│   ├── Trinix.Apps/              # Files, Terminal, Settings, ... (Vixen apps)
│   ├── Trinix.Pkg/               # Package manager + update client
│   ├── Trinix.Bundle/            # .app bundle + disk-image + signing libraries/CLI
│   └── Trinix.Interop/           # P/Invoke bindings: wlroots, libinput, DRM, etc.
├── vixen/                # Git submodule or package reference to the Vixen engine
├── signing/              # Dev PKI: root CA, signing tools (real keys NEVER committed)
├── scripts/              # Entry points: build.ps1, run-vm.ps1 (PowerShell, naturally)
└── docs/
```

Everything under `src/` builds with the standard `dotnet` SDK inside the build container. Everything under `toolchain/`, `base/`, `image/` runs inside Docker stages.

---

## 3. Build & run model on the Mac

**Build:** `docker buildx` with a multi-stage graph:

1. `host-tools` — Debian/Ubuntu-based builder with compilers + `dotnet` SDK.
2. `toolchain` — one LLVM/Clang/LLD build (targets both arches via `--target`), plus a minimal cross-GCC per arch used only to build glibc; produces a sysroot per target triple (`aarch64-trinix-linux-gnu`, `x86_64-trinix-linux-gnu`).
3. `base-<arch>` — cross-compiled base system installed into a clean rootfs.
4. `system-image-<arch>` — assembled, signed, bootable disk image (ESP + A/B root partitions).

Artifacts are exported with `--output type=local` — you get `trinix-<arch>.img` on the host without installing anything.

**Run/test — two tiers:**

- **Container tier (fast, CI-friendly):** the rootfs runs *as a Docker container* for testing userland: PowerShell, .NET services, the package manager, even the compositor headless (wlroots has a headless backend; screenshots via wl-screencopy for visual tests).
- **VM tier (real boot):** Docker cannot boot a kernel. Full-system testing uses **QEMU** (`qemu-system-aarch64 -accel hvf` runs near-native on Apple Silicon) or **UTM**. QEMU itself can run *from a container* with the image mounted, keeping the "no host installs" promise; graphical testing is nicer with UTM installed on the host (the one optional host tool). `virtio-gpu` gives the compositor real DRM/KMS inside the VM.

---

## 4. Phases

### Phase 0 — Scaffolding (small)
- Repo layout above, `git init`, CI pipeline skeleton (GitHub Actions with buildx).
- `scripts/build.ps1` orchestrator: builds any stage for any arch.
- Pin all source tarball versions + checksums (LFS discipline: reproducibility from day one).

**Exit criteria:** one command produces the (empty) build container for both arches.

### Phase 1 — Toolchain (medium)
- Build LLVM/Clang/LLD once inside the `host-tools` container — a single install cross-compiles to both target triples via `--target` + `--sysroot`.
- Per arch: Linux headers → minimal cross-GCC → **glibc** (GCC-built; glibc does not support Clang) → compiler-rt, libunwind, libc++ (Clang-built) into the sysroot.
- Also build GCC's `libgcc_s` + `libstdc++` as shippable compat libraries (required by Microsoft's official .NET binaries).
- Wrapper scripts/cmake toolchain files so every `base/` recipe builds with `clang --target=<triple>` uniformly; llvm-ar/objcopy/strip replace binutils.
- Sanity suite: compile-and-run test binaries (x86_64 ones verified under `qemu-user`).

**Exit criteria:** both sysroots produce a static and dynamic "hello world" (C and C++) that runs; identical recipe scripts work for both arches.

### Phase 2 — Minimal bootable base (large)
- Cross-build the LFS base set (systemd edition): kernel, glibc, busybox/coreutils, util-linux, dbus, **systemd**, dash/bash (as `/bin/sh`), openssl, ca-certificates, iproute2, e2fsprogs/dosfstools.
- Kernel built with `make LLVM=1` (officially supported); config per arch: virtio (for QEMU), DRM, evdev, squashfs, dm-verity, EFI stub.
- Image assembly: GPT with ESP (systemd-boot) + root A + root B + `/data` (mutable: `/home`, `/var`, installed apps).
- Boot to a systemd multi-user target with a serial console in QEMU, **both arches**.

**Exit criteria:** `./scripts/run-vm.ps1 -Arch arm64` boots to a login prompt.

### Phase 3 — .NET + PowerShell as first-class citizens (medium)
- Install the latest .NET runtime + SDK into the base (linux-arm64 / linux-x64 builds; if Microsoft binaries don't run against our glibc/versioning, fall back to source-build — .NET is buildable from source and this is the "from scratch" answer anyway).
- PowerShell (`pwsh`) installed, registered in `/etc/shells`, **default login shell** for users. Root keeps `/bin/sh` as a rescue fallback; a `trinix-rescue` boot target bypasses pwsh entirely.
- PowerShell profile providing mac-like conveniences; system administration cmdlets (`Trinix.Management` module: services, network, updates) — this becomes the distro's admin surface.
- First C# system service running under systemd (e.g. a trivial `trinixd`) to prove the hosting model. Decide framework-dependent vs **NativeAOT** per component (AOT for boot-path and compositor; framework-dependent for apps).

**Exit criteria:** boot → log in → land in PowerShell; `dotnet --version` works in-VM on both arches.

### Phase 4 — Graphics stack + C# compositor MVP (large, highest risk)
- Base additions: Mesa (virtio-gpu/venus for VMs; amdgpu/intel/nvidia-nouveau later for metal), libdrm, libinput, xkbcommon, seatd or systemd-logind seats, wlroots, PipeWire (audio + later screen-capture portal).
- `Trinix.Interop`: P/Invoke bindings for wlroots/libinput (generated with ClangSharp, hand-curated).
- **Compositor MVP** (NativeAOT, allocation-free render loop to avoid GC hitches):
  1. Clears the screen, renders a cursor.
  2. xdg-shell: map/unmap toplevels, focus, keyboard/pointer routing.
  3. Stacking WM with mac-like policy: window server owns decorations? **No — client-side decorations drawn by Vixen**, compositor owns shadows/animations/traffic-light hit zones via a small private Wayland protocol extension (`trinix-shell-v1`).
  4. Output management, HiDPI scaling (mac-like 2× default), damage tracking.
- systemd session: `graphical.target` → greeter (minimal at first: auto-login) → compositor.

**Exit criteria:** a stock Wayland client (e.g. foot or a demo) runs windowed under the C# compositor in the VM.

### Phase 5 — Vixen integration + system shell + core apps (large, parallel with 4 after MVP)
- Define the **Vixen ⇄ platform contract**: Wayland client backend (wl_surface + EGL/Vulkan), input, clipboard, drag-and-drop, `trinix-shell-v1` for decorations/menus.
- Global **menu bar** protocol: apps export menus (dbus or a Wayland extension); shell renders them mac-style.
- Core Vixen apps, in order: **Terminal** (hosts pwsh — dogfoods everything), **Files** (Finder analog), **Dock + desktop shell**, **Settings**, greeter/login UI.

**Exit criteria:** boot → graphical login → desktop with dock + menu bar → open Terminal and Files.

### Phase 6 — App format, disk images, signing (medium)
- **Bundle format** — `MyApp.app/` directory:
  - `Contents/Info.toml` (or json): identity, version, entry point, declared permissions.
  - `Contents/MacOS/` → `Contents/Bin/` executable (NativeAOT or framework-dependent .NET), `Contents/Resources/`, `Contents/Frameworks/`.
- **Distribution image (.dmg analog)** — call it `.tdi`: an **erofs or squashfs** filesystem image containing the bundle, mountable read-only via loop device; Files app mounts on double-click, "drag to /Applications" installs (= copy).
- **Signing (codesign analog):**
  - Dev PKI: Trinix root CA → developer certificates.
  - Signature = Merkle tree over bundle contents + signed manifest embedded in the bundle (`Contents/_Signature/`), image-level detached signature on the `.tdi`.
  - Verification: `Trinix.Bundle` library + a Gatekeeper-analog service that validates on first launch; **fs-verity** for installed-app integrity, **dm-verity** for the base system image.
  - All of this is pure C# (X.509 + COSE/CMS via .NET crypto) — no OpenSSL scripting.

**Exit criteria:** build → sign → package a Vixen app as `.tdi` on the Mac; install and launch it in the VM; tampered bundle refuses to launch.

> **Phases 7 and 8 below are superseded by [`docs/plan/`](docs/plan/)**, which plans the rest of the
> system — the SDK, the shell, the sandbox, search, Files, Settings, the Store, updates, backup,
> compatibility and accessibility — at its real size. The package manager and updates become Phase 12
> there; the polish items are distributed across its Phases 12–14. The two sections are kept here as
> the record of what was originally intended.

### Phase 7 — Package manager + OS updates (medium)
- `Trinix.Pkg` (C#), two personalities:
  - **Apps:** repository index (signed JSON feed) of `.tdi` images; install/uninstall/update = fetch, verify, copy/remove. CLI (`tpkg`) + PowerShell cmdlets + a Settings/App Store-ish UI later.
  - **System:** A/B image updates — download signed delta or full image to the inactive slot, verify dm-verity root hash, flip boot entry, automatic rollback on failed boot (systemd-boot `boot-attempts`).
- Repository hosting is just static files + signatures (any object store).

**Exit criteria:** `tpkg install <app>` in-VM from a local repo; OS updates flip A→B and roll back on induced failure.

### Phase 8 — Polish & release engineering (ongoing)
- Real-hardware bring-up (one x86_64 laptop target first; Apple-Silicon-native via Asahi kernel is a separate research track).
- Installer (Vixen app), first-boot setup assistant, branding.
- Networking UI (NetworkManager or systemd-networkd + C# frontend), Bluetooth, power management.
- Nightly CI images for both arches; versioning + release channels (edge/stable).
- Secure Boot chain (shim/sbctl) once the signing infra is stable.

---

## 5. Key risks & mitigations

| Risk | Mitigation |
|---|---|
| **LFS × 2 architectures is a lot of surface** | One Clang toolchain cross-compiles both arches with identical recipes; CI builds both on every change so drift is caught immediately. Keep the base package set ruthlessly small (~40–60 recipes, not hundreds) — the immutable-image model permits this. |
| **A few packages assume GNU toolchain quirks** | Most of the modern stack (systemd, Mesa, wlroots, kernel) is Clang-clean; the stragglers get patches in their `base/recipes/` entry — standard LFS practice. glibc is the one permanent GCC dependency, contained in Phase 1. |
| **C# compositor latency (GC pauses)** | NativeAOT, preallocated buffers, zero allocations on the frame path, `GC.TryStartNoGCRegion` where unavoidable. wlroots does the heavy lifting in C. |
| **PowerShell startup time / as login shell** | pwsh cold start is ~300–600 ms on ARM64 — acceptable for interactive login, unacceptable for scripts; hence `/bin/sh` stays for system scripting. Ship pwsh NativeAOT-trimmed if size/startup becomes a problem. |
| **.NET binaries vs. our glibc** | Test Microsoft's official linux-arm64/linux-x64 builds early (Phase 3); source-build fallback is planned, not improvised. |
| **Vixen is itself in development** | The platform contract (Phase 5, first bullet) is the firewall: compositor and apps depend on the contract, not on Vixen internals. A trivial non-Vixen Wayland test client keeps the compositor testable independently. |
| **Docker can't boot the OS** | Two-tier testing (container for userland, QEMU/UTM for boot) is designed in from Phase 2, not bolted on. |
| **Custom signing PKI is easy to get subtly wrong** | Use standard formats (X.509, CMS/COSE, fs-verity/dm-verity kernel primitives) rather than inventing crypto; only the *policy* layer is custom. |

---

## 6. Suggested immediate next steps

1. Phase 0: scaffold the repo, `git init`, write `docker/host-tools.Dockerfile` and `scripts/build.ps1`.
2. Phase 1: get the LLVM toolchain + both sysroots building in Docker — this is the long pole with zero design ambiguity, so it can start today.
3. In parallel: draft the `trinix-shell-v1` protocol and the Vixen platform contract as design docs, since they gate compositor and app work later.
