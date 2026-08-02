# Trinix

A macOS-like Linux distribution built from scratch for **arm64 and x86_64 from day
one** — PowerShell as the interactive shell, C#/.NET as the primary language, a C#
Wayland compositor, and a macOS-style app-bundle + signed-image model instead of a
conventional package database.

The full design, and the reasoning behind each decision, is in
[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).

**Status: Phase 6.** The base system cross-builds — kernel, glibc, systemd,
shadow, coreutils and the rest — and assembles into an A/B-capable GPT disk
image. Booting it and logging in lands you in **PowerShell**, with .NET,
Trinix's own `Trinix.Management` cmdlets, and a C# system service running under
systemd. On top of that sits the graphics stack — libdrm, libinput,
libxkbcommon, wlroots, seatd — and **a Wayland compositor written in C#**,
which runs stock Wayland clients in windows, with two Trinix protocol
extensions for decorations and a mac-style global menu bar
([the contract](docs/vixen-platform-contract.md)).

Phase 6 adds the **application format**: `.app` bundles with a signed Merkle
manifest, `.tdi` distribution images (EROFS + a signed footer), a development
PKI, and a Gatekeeper-analog launcher. Build, sign, package, install, launch —
and a tampered bundle refuses to start. See
[docs/app-bundles.md](docs/app-bundles.md).

Still to come: Vixen and the system shell (Phase 5's remaining items), then the
package manager and A/B updates (Phase 7).

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

Applications are their own stage, and `base` builds it first — the image carries
both the trust store and a signed reference application:

```bash
./scripts/build.ps1 -Stage app -Arch arm64 -Verify
```

That publishes `Hello.app`, seals it with a Merkle manifest signed by this
machine's development certificate, packages it as `out/apps/arm64/Hello.tdi`,
and — under `-Verify` — asserts that each of the seven ways of tampering with it
is detected. The development PKI is created on first use under `signing/local/`
and is never committed; see [docs/app-bundles.md](docs/app-bundles.md).

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
either. You get a serial console and `trinix login:`. Two accounts, both with
the password `trinix` — a development image sets a known credential rather than
an unguessable one nobody can log in with, and Phase 8's installer is where a
real one gets set:

| Account | Shell | For |
|---|---|---|
| `trinix` | **PowerShell** | The system as it is meant to be used |
| `root` | bash | Rescue, deliberately: the account you fix PowerShell from must not need PowerShell to start |

The boot menu carries a rescue entry for the same reason, and `Ctrl-a x` quits.

Once logged in:

```powershell
Get-TrinixSystem                  # identity, kernel, which A/B slot booted
Test-TrinixSystem                 # is this image healthy? one row per check
Get-TrinixService -Failed         # anything not running as intended
Get-TrinixNetworkInterface        # addresses, via iproute2's JSON output
dotnet --version
```

`Test-TrinixSystem` also runs at every boot as `trinix-selftest.service`, which
is where Phase 7's A/B updater will get its answer to "did the slot I just
wrote actually work" — so the compositor is one of the things it checks.
`trinixd` is the first C# system service — `Type=notify`, logging to the
journal, hosted by systemd like any other.

## The compositor

`trinix-compositor` is Trinix's window server, and it is C#. It starts with
`graphical.target`, draws on `/dev/tty1`, and gets the display and the keyboard
from **seatd** — so it runs as the `trinix` user, with membership of one group
as its only privilege.

```bash
systemctl status trinix-compositor
journalctl -u trinix-compositor -o cat     # outputs, input devices, windows
systemctl start trinix-wl-demo             # a Wayland client, in a window
```

The split is worth knowing about before reading the code. wlroots does
modesetting, buffer management and the protocol implementations; a small C
library ([base/recipes/trinix-wlr/](base/recipes/trinix-wlr/)) owns those
objects and the listener plumbing that C# cannot express; and
[src/Trinix.Compositor/](src/Trinix.Compositor/) makes every decision — where a
window opens, what has focus, what `Alt` does, what dragging means. If a rule
about window behaviour is not in `WindowManager.cs`, Trinix does not have that
rule yet.

There is no OpenGL in the image, and that is a decision rather than an
omission: wlroots' GL renderer needs a DRM render node, QEMU's virtio-gpu
offers one only when the *host* can lend it a GL context, and the host here is
a container. So the compositor composites in software through pixman into dumb
buffers — correct, slow, and honestly the same thing every VM does. Mesa
arrives when there is a GPU worth talking to; see
[base/recipes/wlroots/recipe.sh](base/recipes/wlroots/recipe.sh) for the whole
argument.

The virtual-console login moved to `tty2` when the compositor took `tty1`. The
serial console is unaffected, and `systemd.unit=multi-user.target` boots without
a display at all.

Four unattended checks, each a phase's exit criteria expressed as a test:

```bash
./scripts/run-vm.ps1 -Arch arm64 -Check          # boots to a login prompt
./scripts/run-vm.ps1 -Arch arm64 -LoginCheck     # logs in, lands in pwsh, runs .NET
./scripts/run-vm.ps1 -Arch arm64 -GraphicsCheck  # runs a Wayland client under the compositor
./scripts/run-vm.ps1 -Arch arm64 -AppCheck       # installs, launches and then refuses a tampered app
```

There is no accelerator — Docker Desktop does not pass virtualisation through —
so expect a couple of minutes for arm64 and considerably longer for x86_64.

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
