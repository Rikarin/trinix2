# 16 — Build, CI and Testing

The shortest document here and the most overdue. Everything in documents 01–15 assumes a machine that
can tell you when it broke, and Trinix does not have one yet.

## Where it stands

The image side is in good shape. [`.github/workflows/build.yml`](../../.github/workflows/build.yml)
verifies every pinned source checksum, builds the host-tools container on both host architectures,
builds the toolchain, builds the base system and a disk image for both arches, **boots it in QEMU**,
logs in, checks PowerShell and .NET, and runs a Wayland client under the C# compositor. That is a
better boot gate than most distributions have.

The C# side was one line — `dotnet build src/Trinix.slnx` — and as of **2026-08-25** it is a floor:

| | |
|---|---|
| `Trinix.Bundle.Tests` | 221 tests. Generates a PKI in process, seals a real `.app`, and then breaks it: a flipped byte, a file added, removed and renamed, a mode-bit change, an edited manifest, and — the one worth having — a manifest **re-signed by a trusted signer** naming `../../etc/passwd`. `.tdi` footers are assembled byte by byte to reach the bounds checks |
| `Trinix.Management.Tests` | 33 tests over `Cli` parsing, the `ip -json` shapes and service health |
| `scripts/check-solution.ps1` | Asserts every `.csproj` under `src/` is in `Trinix.slnx`. **A script, not a test**, and deliberately: a test asserting "every project is in the solution" is the one assertion defeated by leaving its own project out of the solution. CI runs it before restore, so it does not depend on the state it checks |
| `.editorconfig` | The repo had none, so `dotnet format` applied Allman defaults and reported 646 "errors" against correct code. Formatting only — no `dotnet_diagnostic` severities, because `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors` together would turn any rule raised to warning into a tree-wide build failure |
| The `dotnet` job | check-solution → restore → format → build → test, cheapest first, still independent of the toolchain and image stages |

The two projects missing from the solution — `Trinix.Platform` and `Trinix.Apps.HelloUi` — are in it, so the
Vixen platform backend is compiled by the job that gates a pull request rather than only by an image build.

**Two bugs surfaced on the day the tests were written**, which is the argument for the whole exercise:

- `System.Text.Json` builds a type with `required` members without running field initialisers, so an
  `Info.json` that simply omits `"permissions"` — every application that asks for nothing — arrived as
  `null` and `Validate()` threw `NullReferenceException` out of `BundleVerifier`. **A crash in the
  component that decides whether code may run, rather than a refusal.** Fixed.
- `SystemVersion.Satisfies` disagrees with its own comment. See doc 18's decision list; pinned, not changed.

⚠ **Nothing has yet run on Linux.** Everything above was measured on macOS/arm64, and two tests already had
to be rewritten because a case-insensitive filesystem made them pass for the wrong reason. CI is the first
Linux run and should be read as a first run.

## A gate that does not exist, found the hard way

⚠ **`Assert-HostToolsCurrent` gives confidence it cannot deliver.** It rebuilds `host-tools` on every
base build — but `docker/base.Dockerfile` starts `FROM ${TOOLCHAIN_IMAGE}`, so a fix to
`host-tools.Dockerfile` **cannot reach the stage where recipes actually compile** without a toolchain
rebuild. There is a staleness check for the image that is not used and none for the image that is.

Measured 2026-08-25 on a development machine: `trinix/toolchain-arm64:dev` dated 2026-07-30 and
`trinix/base-arm64:dev` dated 2026-08-02, against `c18da30` of 2026-08-24 — the commit that added
`mako`, `yaml` and `packaging` precisely because mesa needs them. So mesa failed with
`Python >= 3.10 not found`, which is a **misleading message the repository already documents**: mesa's
probe walks candidate interpreters, `continue`s past any that cannot import those three modules, and
then blames the version. Three weeks of a fix sitting in the tree unable to reach the place that needed
it, and neither image is old enough to look obviously wrong.

The gate: **a stage image must record the digest of the Dockerfile and the base image it was built
from, and a build must refuse — or rebuild — when either has moved.** Cheap, and it is the difference
between a slow build and a build that is quietly testing three-week-old inputs.

⚠ It also means a local build and CI can disagree indefinitely without anyone noticing, which is the
more expensive version of the same fault.

## What is still owed

⚠ This remains the highest-leverage work in the plan. Documents 01–15 add roughly 180 EM of C# to a
repository with no unit tests; the cost of adding them later grows with every one of those months.

## What to build, in order

### 1. Test projects, and the four kinds of test

| Kind | Runs where | Covers |
|---|---|---|
| **Unit** | `dotnet test`, any host, seconds | The bundle format, Merkle trees, signatures, the EROFS writer, chunking (doc 10), the settings schema, the ranker (doc 06), path handling, the D-Bus marshalling |
| **UI** | `Vixen.Ui.Testing`, headless, no display | Every window of every first-party application: it builds, it lays out, the keyboard walk closes, the accessibility names exist |
| **Container** | The rootfs as a Docker container | Everything userland: `trinixd`, the broker's unit construction, `tpkg`, the keychain, PowerShell cmdlets, the sandbox escape suite |
| **VM** | QEMU, as today | Boot, the compositor, the session, updates and rollback, and the screenshot goldens |

The container tier is the one that pays for itself fastest and it was designed in from Phase 2 — the
rootfs already runs as a container. A test that installs an application, launches it, checks it cannot
read `~/Documents`, and uninstalls it, runs there in seconds and needs no VM.

### 2. The gates

| Gate | What it stops |
|---|---|
| `dotnet test` on every PR, all four projects in the solution | The obvious |
| **Every project in the solution** | The `Trinix.Platform`/`HelloUi` omission above, permanently — the check is that the set of `.csproj` under `src/` equals the set in `Trinix.slnx` |
| `dotnet format --verify-no-changes` | Style arguments in review |
| **`CheckApi`** — a shipped/unshipped public API baseline, Vixen-style | An accidental breaking change to `Trinix.Sdk`, which doc 01 § Open already names as its biggest missing gate |
| **`CheckAot`** — publish the NativeAOT components for both RIDs | An AOT-hostile dependency reaching the compositor. The analysers warn; only a publish proves it |
| **`trinix doctor`** over every first-party bundle | Doc 01: permission drift, missing menu items, unlocalised strings, keyboard traps, missing icons |
| **The sandbox escape suite** — an application that tries all fourteen permissions and must fail all fourteen | Doc 04's entire value |
| **Screenshot goldens** via `wlr-screencopy` | The shell rendering wrong, which no other test can see. Vixen's golden-image approach applies directly, including generating on one platform and verifying on another |
| **Latency gates**: app launch, search keystroke→results, terminal tab open, boot | Doc 00's quality bars, which are worthless as prose |
| **Determinism**: two builds of the same source produce identical `.tdi` contents | `pack-apps.sh` already reasons carefully about timestamps and about why a *signature* is not reproducible; the contents should be, and nothing checks it |

### 3. Fixing the workflow

The `dotnet` job becomes: restore, format check, build the whole solution, test, `CheckApi`, publish
AOT for both RIDs. It stays fast because it has no toolchain dependency, which is already why it is a
separate job. The container tier becomes a job that consumes the base image artifact the
`base-and-image` job already uploads. The VM tier grows the update/rollback and screenshot tests, and
the expensive parts go nightly — the schedule trigger already exists.

## Two structural things worth fixing at the same time

- **A build orchestrator for `src/`.** `scripts/build.ps1` orchestrates the image; the C# side is a
  bare `dotnet build` plus two shell scripts. As the gates above accumulate, they need somewhere to
  live that is not a YAML file, so that a developer can run the same gate locally. Vixen uses Nuke and
  it works; the requirement is only that CI runs no logic a person cannot run.
- **A status document.** Vixen keeps [`overview.md`](../../../Vixen/docs/overview.md) reconciled
  against the code, deliberately separate from its design documents, and the reason is that three
  places recording the same thing is how they come to disagree. Trinix's README currently carries the
  status, which is right while there are six phases and will not be at fourteen.

## Effort

| Piece | EM |
|---|---|
| Test projects, harness, the container tier's fixtures | 1.5 |
| Backfilling unit tests for what exists (bundle, image, compositor, interop) | 2.0 |
| The gates: solution completeness, format, `CheckApi`, `CheckAot`, determinism | 1.5 |
| Screenshot goldens and the headless capture harness | 1.0 |
| Sandbox escape suite (counted in doc 04) | — |
| Latency harness and its gates | 1.0 |
| Build orchestrator for `src/` | 1.0 |
| **Total** | **8.0** |

⚠ Of this, the first 1.5 EM should happen **before** doc 01's SDK work starts, not alongside it. An
SDK whose first test is written after its API is a public surface is an SDK with the wrong API.
