# Trinix

A macOS-like Linux distribution built from scratch for **arm64 and x86_64 from day
one** — PowerShell as the interactive shell, C#/.NET as the primary language, a C#
Wayland compositor, and a macOS-style app-bundle + signed-image model instead of a
conventional package database.

The full design, and the reasoning behind each decision, is in
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).

**Status: Phase 2.** The base system cross-builds — kernel, glibc, systemd,
shadow, coreutils and the rest — and assembles into a signed-later, A/B-capable
GPT disk image that boots to a login prompt in QEMU. Phase 3, .NET and
PowerShell as first-class citizens, is next.

## Requirements

Docker Desktop. That is the whole list — no compilers, SDKs or cross-toolchains are
installed on the host, and nothing is built outside a container. PowerShell 7.4+ is
needed to run the orchestrator scripts, and ships with macOS via `brew install
powershell`; the same scripts also run *inside* the build container.

Booting the finished image needs QEMU, which also runs from a container — see
[Boot it](#boot-it). UTM stays the one optional host tool, and is nicer for
graphical testing once Phase 4 has a compositor.

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

Then the base system and a bootable image:

```bash
./scripts/build.ps1 -Stage base -Arch arm64 -Verify
./scripts/build.ps1 -Stage image -Arch arm64 -Verify
```

`base` cross-builds every recipe under [base/recipes/](base/recipes/) into a
clean rootfs; `image` turns that rootfs into `out/trinix-arm64.img` — GPT, an
ESP with systemd-boot, two root slots and a writable `/data`. Their `-Verify`
gates check what can be checked without booting: that every shipped binary is
the right machine type with all its libraries present, and that the firmware
will find a bootloader that names a root partition which actually exists.

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

## Boot it

```bash
./scripts/run-vm.ps1 -Arch arm64
```

QEMU runs *from a container* too, so the VM tier needs nothing on the Mac
either. You get a serial console, `trinix login:`, and a root password of
`trinix` — a development image sets a known credential rather than an
unguessable one nobody can log in with; Phase 8's installer is where a real one
gets set. `Ctrl-a x` quits.

`./scripts/run-vm.ps1 -Arch arm64 -Check` boots unattended and asserts that the
login prompt appeared, which is the Phase 2 exit criterion as a test. There is
no accelerator — Docker Desktop does not pass virtualisation through — so
expect a couple of minutes for arm64 and considerably longer for x86_64.

See [image/README.md](image/README.md) for the partition layout and why the
root filesystem is read-only.

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
| `scripts/` | `build.ps1`, `run-vm.ps1`, `update-sources.ps1` — the host-side entry points |

## Two-tier testing

Docker cannot boot a kernel, so testing is split deliberately:

- **Container tier** — the finished rootfs runs as a Docker container to exercise
  userland: PowerShell, .NET services, the package manager, and the compositor
  headless (wlroots' headless backend, with screenshots for visual assertions).
- **VM tier** — QEMU for real boot, real DRM/KMS via `virtio-gpu`, both
  architectures. QEMU runs from a container ([docker/vm.Dockerfile](docker/vm.Dockerfile)),
  so the host still needs nothing; UTM stays the optional host tool for
  graphical work from Phase 4 onwards. There is no accelerator inside Docker
  Desktop's VM, so this is TCG emulation — correct, and slow.
