# 16 — Build, CI and Testing

The shortest document here and the most overdue. Everything in documents 01–15 assumes a machine that
can tell you when it broke, and Trinix does not have one yet.

## Where it stands

The image side is in good shape. [`.github/workflows/build.yml`](../../.github/workflows/build.yml)
verifies every pinned source checksum, builds the host-tools container on both host architectures,
builds the toolchain, builds the base system and a disk image for both arches, **boots it in QEMU**,
logs in, checks PowerShell and .NET, and runs a Wayland client under the C# compositor. That is a
better boot gate than most distributions have.

The C# side is one line:

```yaml
- run: dotnet build src/Trinix.slnx --configuration Release
```

Three things are wrong with it, and each is checkable in a minute:

1. **There are no test projects.** `find . -name '*Tests*'` returns nothing. Every C# component in the
   repository — the bundle format and its signature verification, the Merkle tree, the EROFS writer,
   the compositor's window manager, the Wayland client, the platform backend — is covered by a boot
   test and nothing else.
2. **Two projects are not in the solution.** `Trinix.slnx` lists eight projects;
   [`Trinix.Platform`](../../src/Trinix.Platform/) and
   [`Trinix.Apps.HelloUi`](../../src/Trinix.Apps.HelloUi/) are not among them. They are built by
   [`src/pack-apps.sh`](../../src/pack-apps.sh) during the image stage, so they do compile — but the
   fast `dotnet` job that gates every pull request never sees them, which means a compile error in the
   Vixen platform backend is caught by a full image build rather than in ninety seconds.
3. **`dotnet build` is not `dotnet test`, and there is no format, analyser or API gate**, although
   [`Directory.Build.props`](../../src/Directory.Build.props) already sets
   `TreatWarningsAsErrors`, `EnforceCodeStyleInBuild` and the trim/AOT analysers — so the standards
   are declared and only half enforced.

⚠ This is the highest-leverage work in the entire plan. Documents 01–15 add roughly 180 EM of C# to a
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
