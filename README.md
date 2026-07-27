# Trinix

A macOS-like Linux distribution built from scratch for **arm64 and x86_64 from day
one** — PowerShell as the interactive shell, C#/.NET as the primary language, a C#
Wayland compositor, and a macOS-style app-bundle + signed-image model instead of a
conventional package database.

The full design, and the reasoning behind each decision, is in
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).

**Status: Phase 1 complete.** The build container, source pinning, the LLVM
toolchain and both sysroots are done and gated by tests. `clang
--target=<triple>` produces working static and dynamic C and C++ binaries for
arm64 and x86_64. Phase 2 — the bootable base system — is next.

## Requirements

Docker Desktop. That is the whole list — no compilers, SDKs or cross-toolchains are
installed on the host, and nothing is built outside a container. PowerShell 7.4+ is
needed to run the orchestrator scripts, and ships with macOS via `brew install
powershell`; the same scripts also run *inside* the build container.

For booting the finished image you additionally want QEMU (runs from a container) or
UTM (the one optional host tool, nicer for graphical testing) — Phase 2 onwards.

## Build

```bash
./scripts/build.ps1 -Stage host-tools -Verify
```

That builds the Phase 0 build container and asserts it can cross-build for both
target architectures. `-Verify` runs the acceptance gate as part of the build, so a
missing tool fails the build rather than surfacing three stages later.

Then the toolchain — one Clang/LLD install shared by every target, followed by a
sysroot per architecture:

```bash
./scripts/build.ps1 -Stage llvm
./scripts/build.ps1 -Stage toolchain -Verify
```

`-Verify` runs the Phase 1 exit criteria as a test: a static and dynamic hello
world in C and C++, executed under `qemu-user` for **both** architectures, plus
assertions that Clang-built C++ really uses libc++/libunwind and that the GCC
compat libraries .NET needs are present anyway.

The `base` and `image` stages exist in the orchestrator and currently fail with
the phase they belong to — see `./scripts/build.ps1 -?` for the full surface.

### Why GCC is still here

Exactly one component forces it: **glibc cannot be built by Clang.** So a minimal
cross-GCC is built solely to compile glibc, then rebuilt once against real glibc
to harvest `libgcc_s.so.1` and `libstdc++.so.6` — which ship as compat libraries
because Microsoft's official .NET binaries link against them. Nothing Trinix
itself builds uses the GCC runtime; [toolchain-sanity.sh](toolchain/scripts/toolchain-sanity.sh)
fails the build if a Clang-compiled C++ binary quietly falls back to it.

Poke around inside the container:

```bash
docker run --rm -it -v "$PWD:/work" trinix/host-tools:dev bash
```

## Source pinning

Nothing is fetched that is not pinned by version *and* sha256 in
[base/sources.json](base/sources.json). Inside a container, `trinix-fetch <name>` is
the only sanctioned way to obtain a tarball, and it fails hard on a digest mismatch.

```bash
# Bump a version, then re-hash just that entry:
./scripts/update-sources.ps1 -Name llvm-project -Force

# What CI runs: assert every pin still matches upstream.
./scripts/update-sources.ps1 -Verify
```

Downloads are cached in `.cache/sources/` (gitignored).

## Layout

| Path | Contents |
|---|---|
| `docker/` | Build environment definitions and the in-container helper scripts |
| `toolchain/` | LLVM/Clang toolchain build, per-arch sysroots, cmake toolchain files |
| `base/` | `sources.json` (all pins) and one build recipe per base component |
| `image/` | Rootfs assembly, ESP layout, A/B image + dm-verity tooling |
| `src/` | All C#: compositor, shell, apps, package manager, bundle/signing libraries |
| `signing/` | Dev PKI layout and key policy — real keys are never committed |
| `scripts/` | `build.ps1`, `update-sources.ps1` — the host-side entry points |

## Two-tier testing

Docker cannot boot a kernel, so testing is split deliberately:

- **Container tier** — the finished rootfs runs as a Docker container to exercise
  userland: PowerShell, .NET services, the package manager, and the compositor
  headless (wlroots' headless backend, with screenshots for visual assertions).
- **VM tier** — QEMU (`-accel hvf`, near-native on Apple Silicon) or UTM for real
  boot, real DRM/KMS via `virtio-gpu`, both architectures.
