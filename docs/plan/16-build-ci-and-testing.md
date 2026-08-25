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
| `Trinix.Bundle.Tests` | 241 tests. Generates a PKI in process, seals a real `.app`, and then breaks it: a flipped byte, a file added, removed and renamed, a mode-bit change, an edited manifest, and — the one worth having — a manifest **re-signed by a trusted signer** naming `../../etc/passwd`. `.tdi` footers are assembled byte by byte to reach the bounds checks |
| `Trinix.Management.Tests` | 33 tests over `Cli` parsing, the `ip -json` shapes and service health |
| `scripts/check-solution.ps1` | Asserts every `.csproj` under `src/` is in `Trinix.slnx`. **A script, not a test**, and deliberately: a test asserting "every project is in the solution" is the one assertion defeated by leaving its own project out of the solution. CI runs it before restore, so it does not depend on the state it checks |
| `.editorconfig` | The repo had none, so `dotnet format` applied Allman defaults and reported 646 "errors" against correct code. Formatting only — no `dotnet_diagnostic` severities, because `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors` together would turn any rule raised to warning into a tree-wide build failure |
| `scripts/check-api.ps1` and `src/Trinix.ApiCheck` | The public surface of six libraries against a `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` pair committed beside each. **Both directions**: an addition nobody approved, and a removal — the one that compiles here and breaks somebody else. See below for which six and why |
| `scripts/check-determinism.ps1` | Builds the applications twice with separate intermediates, seals both, and compares the manifests. Measured 2026-08-25: both bundles, 80 files, identical. ⚠ The *signature* is not reproducible and must not be |
| The `dotnet` job | check-solution → restore → format → build → test → `check-api.ps1`, cheapest first, still independent of the toolchain and image stages. `check-determinism.ps1` is a **sibling job**, not a seventh step: it builds everything twice from cold, and the value of the `dotnet` job is that it answers in about ninety seconds |

The two projects missing from the solution — `Trinix.Platform` and `Trinix.Apps.HelloUi` — are in it, so the
Vixen platform backend is compiled by the job that gates a pull request rather than only by an image build.

**Two bugs surfaced on the day the tests were written**, which is the argument for the whole exercise:

- `System.Text.Json` builds a type with `required` members without running field initialisers, so an
  `Info.json` that simply omits `"permissions"` — every application that asks for nothing — arrived as
  `null` and `Validate()` threw `NullReferenceException` out of `BundleVerifier`. **A crash in the
  component that decides whether code may run, rather than a refusal.** Fixed.
- `SystemVersion.Satisfies` disagrees with its own comment. See doc 18's decision list; pinned, not changed.

### The two decisions inside `CheckApi`

**A tool that reads the assembly, not the analyser that shares its file format.** Vixen's
`Tools/Vixen.ApiCheck` was adapted rather than adopting `Microsoft.CodeAnalysis.PublicApiAnalyzers`,
and the deciding reason is Trinix-specific: **the surface most worth gating is generated.**
`Trinix.Sdk.Generators` writes the proxies, the dispatchers and the D-Bus introspection XML that are
`Trinix.Services.Contracts`' public face, and a change to those *is* a protocol change. The analyser
reports at a symbol's declaration site, which for generated output is a file the build treats as
generated code; reading the built assembly covers it by construction, because the assembly is what
the other end binds to. Proved rather than argued: adding one method to `IClipboard` moves five
baseline lines, three of them generated, and one of the three is the introspection XML with a new
`<method name="Clear">` in it — the wire change, visible as a diff.

Two smaller reasons point the same way. `src/Directory.Build.props` sets `TreatWarningsAsErrors`, so
an analyser would make an unapproved `public` a *build* error — including in the two-hour image build,
which packages these same assemblies. And `Trinix.Bundle`'s own rule is that the component deciding
whether code may run references nothing a package feed can reach; an analyser is a package whose code
runs inside the compiler that builds it.

**Six libraries, and why not the rest.** Covered by default and opted out by declaration, which is
the same direction `check-solution.ps1` runs in: a new library is gated before anyone remembers to
ask. Covered are `Trinix.Bundle`, `Trinix.Interop`, `Trinix.Management`, `Trinix.Platform`,
`Trinix.Sandbox` and `Trinix.Services.Contracts` — everything under `src/` that produces a library
something else compiles against. Not covered:

- **The programs.** `trinixd`, the compositor, `trinix-bundle`, `trinix-open` and the two
  applications are `OutputType Exe`; nothing links against them, so their `public` members promise
  nobody anything.
- **The test projects.** Their surface is xunit's business.
- **`Trinix.Sdk.Generators`**, the one deliberate exception, declared in its own `.csproj` with the
  reason beside it. Nothing references that assembly — the compiler loads it. What a consumer
  depends on is the shape of the code it emits and the diagnostic ids it raises; the ids are already
  tracked in `AnalyzerReleases.Shipped.md` (which is what RS2008 asks for), and the emitted shape is
  gated where it lands, in `Trinix.Services.Contracts`' baseline.

⚠ Every `PublicAPI.Shipped.txt` is empty, and that is the honest state: Trinix has released nothing,
so nothing is a compatibility promise yet and everything lives in `Unshipped`. `-Fold` is the release
ritual that moves it across, and from that day a removal is a breaking change rather than a decision.

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

⚠️ **It happened a second time, which makes it a pattern rather than an incident.** The Btrfs work put
`btrfs-progs` into `host-tools.Dockerfile` so that image assembly could call `mkfs.btrfs`. It could
not: the chain is `image ← base ← toolchain ← host-tools`, `build-image.sh` runs *inside the base
image*, and the cached toolchain was confirmed to carry `mkfs.ext4` and `mkfs.vfat` and no btrfs at
all. `-Stage base` rebuilds host-tools, which again **looks** sufficient and is not — reaching image
assembly meant a full toolchain rebuild, and therefore an LLVM rebuild, for one apt package.

Two independent instances in one week, both costing hours, both invisible until something failed for
an unrelated-looking reason. The gate below is not a nicety.

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
| ✔ `dotnet test` on every PR, every test project in the solution | The obvious |
| ✔ **Every project in the solution** — `scripts/check-solution.ps1` | The `Trinix.Platform`/`HelloUi` omission above, permanently — the check is that the set of `.csproj` under `src/` equals the set in `Trinix.slnx` |
| ✔ `dotnet format --verify-no-changes` | Style arguments in review |
| ✔ **`CheckApi`** — `scripts/check-api.ps1`, a shipped/unshipped public API baseline per library, Vixen-style | An accidental breaking change to what doc 01 § Open names as the biggest missing gate — and, now that the service surface is generated, a change to the D-Bus protocol that nobody wrote down |
| **`CheckAot`** — publish the NativeAOT components for both RIDs | An AOT-hostile dependency reaching the compositor. The analysers warn; only a publish proves it |
| **`trinix doctor`** over every first-party bundle | Doc 01: permission drift, missing menu items, unlocalised strings, keyboard traps, missing icons |
| **The sandbox escape suite** — an application that tries all fourteen permissions and must fail all fourteen | Doc 04's entire value |
| **Screenshot goldens** via `wlr-screencopy` | The shell rendering wrong, which no other test can see. Vixen's golden-image approach applies directly, including generating on one platform and verifying on another |
| **Latency gates**: app launch, search keystroke→results, terminal tab open, boot | Doc 00's quality bars, which are worthless as prose |
| ✔ **Determinism** — `scripts/check-determinism.ps1`: two builds of the same source produce identical bundle contents | `pack-apps.sh` reasons carefully about timestamps and about why a *signature* is not reproducible. The contents should be, and now something checks it: two builds with separate intermediates, both sealed, compared by the Merkle root the seal already computes |

✔ is a gate that exists and that CI runs. The rest are still owed.

⚠ **One of the ✔ is red.** `dotnet format --verify-no-changes` exits 2 on `master`, measured
2026-08-25: it loads the solution through MSBuildWorkspace, which does not run source generators, so
`Trinix.Services.Contracts.Tests` reports twelve `CS0246` for `NotificationsProxy` and its siblings —
types the generator writes. Building first does not help. It is not a formatting failure; no file
needs reformatting. Nothing reported it because CI has never run, and it is a small illustration of
this document's own argument: a gate that has not been watched go green is worth about as much as one
that has not been watched go red.

### 3. Fixing the workflow

The `dotnet` job is now: check-solution, restore, format check, build, test, `CheckApi`. What it
still owes is the AOT publish for both RIDs. It stays fast because it has no toolchain dependency,
which is already why it is a separate job — and why determinism, which builds everything twice, is a
sibling job rather than a step inside it. The container tier becomes a job that consumes the base
image artifact the `base-and-image` job already uploads. The VM tier grows the update/rollback and
screenshot tests, and the expensive parts go nightly — the schedule trigger already exists.

## Two structural things worth fixing at the same time

- **A build orchestrator for `src/`.** `scripts/build.ps1` orchestrates the image; the C# side is a
  bare `dotnet build` plus two shell scripts. As the gates above accumulate, they need somewhere to
  live that is not a YAML file, so that a developer can run the same gate locally. Vixen uses Nuke and
  it works; the requirement is only that CI runs no logic a person cannot run.

  Three gates now live in `scripts/*.ps1`, each a single `pwsh` command with its own `.DESCRIPTION`,
  and the requirement is met — every CI step is a command a developer can type. Whether that stays
  true at ten gates is the open question; `check-api.ps1` already had to discover project properties
  through `dotnet msbuild -getProperty`, which is the sort of thing an orchestrator would own.
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
