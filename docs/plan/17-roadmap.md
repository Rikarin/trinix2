# 17 — Roadmap

Phases 0–6 are built and are recorded in [`IMPLEMENTATION_PLAN.md`](../../IMPLEMENTATION_PLAN.md) and
the [README](../../README.md). This continues the numbering.

⚠ **Phase numbers 7 and 8 are reused.** The old § 4's Phase 7 (package manager and OS updates) is
this document's **Phase 12**, and its Phase 8 (polish and release engineering) is distributed across
Phases 12–14. Nothing is dropped; the sequencing changed because the sandbox and the SDK turned out
to be prerequisites for almost everything, and they were not in the original plan at all.

## What is still owed from Phase 5

The README is accurate that Phase 5 is not finished: the system shell and the core applications — the
dock, a menu bar that draws, Terminal and Files — were its remaining half. Those are Phases 9 and 10
below, at their real size rather than as a bullet.

## The phases

| # | Phase | Contents | EM |
|---|---|---|---|
| 7 | **The floor** | Doc 16's first half: test projects, the four tiers, the solution-completeness and format gates. Doc 01: `Trinix.Sdk`, the generators, the theme, `Trinix.Sdk.Tool`. Doc 02's contracts, the D-Bus plumbing and the settings store | 21.5 |
| 8 | **Authority** | Doc 04 in full: transient units, the broker, portals, consent, audit mode, the escape suite. Doc 05: accounts, PAM host, keychain, TPM, the Secret Service face. The rest of doc 02: notifications, clipboard, device brokering | 24.0 |
| 9 | **The desktop** | Doc 03 in full: window management, snapping, workspaces, the overview, displays, the shell process, panel, dock, control centre, screenshots. Doc 12: Terminal, and `Trinix.Management` onto the contracts | 27.0 |
| 10 | **Your files** | Doc 07: Files, the job queue, volumes, network, tags, Glance and its fourteen providers. Doc 06: Beacon, the index, extractors, ranking, actions | 24.5 |
| 11 | **A computer you can configure** | Doc 08: Settings and every service under it — network, sound, Bluetooth, printing, power. Doc 11's small applications: System Monitor, Disk Utility, Console, Text Edit, Preview, Calculator, Archive, Clock | 28.0 |
| 12 | **Distribution** | Doc 09: repositories, `tpkg`, the Store, install lifecycle. Doc 10: A/B updates, dm-verity, chunking, recovery, Btrfs and Rewind. The Setup Assistant. Doc 16's remaining gates: `CheckApi`, `CheckAot`, goldens, latency, determinism | 27.5 |
| 13 | **The rest of the world** | Doc 13: the runtime bundles, Flatpak, XWayland, Wine, Zen and VS Code. Doc 15: the AT-SPI bridge, the compositor's accessibility, the gates, Orca | 22.5 |
| 14 | **The last mile** | Doc 14: automation. Doc 11's remaining applications: Notes, Photos, Music/Video | 17.0 |
| | **Total** | | **192** |

### Exit criteria

Each phase is finished when its criterion is a passing test, not when its features exist.

| # | Exit criterion |
|---|---|
| 7 | `dotnet test` runs a real suite in CI; every `.csproj` under `src/` is in the solution; a new application scaffolded by `trinix new`, built, signed, packaged and launched, shows a themed window with the standard menu bar |
| 8 | An application declaring no `files.home` is launched and **cannot** read `~/Documents` — proven by a test that tries and by the audit log showing the attempt. A password stored by one application is unreadable by another without a prompt. A Flatpak-shaped caller reaching the portal gets an unattested identity and a session-scoped grant |
| 9 | Log in to a desktop: dock, menu bar, wallpaper. Open four windows, snap two, switch workspaces, open the overview, take an annotated screenshot. Then `SIGSTOP` the shell **and the focused application**, and every one of those window operations still works |
| 10 | Find a file by a word in its body inside 120 ms, press Space and see it, press ↵ and open it. Copy 10 000 files to an SMB share, close the window mid-copy, and watch the job finish |
| 11 | Join a Wi-Fi network, change the output device, add a printer, set the scale on a second display, and quit a runaway application — all without a terminal, and with a `Get-Trinix*` cmdlet for each |
| 12 | Install from a USB stick onto bare metal, install an application from a repository, take an update, induce a failure, and boot into the previous system automatically. Restore a file deleted last Tuesday |
| 13 | Zen and VS Code launch from the dock, use the system keychain, open a file through the portal chooser, and are indistinguishable from native in the launcher. Orca reads a Files window |
| 14 | An automation authored in the GUI, triggered by inserting a USB stick, imports photos and notifies — and the same automation runs from `Invoke-TrinixAutomation` |

## Milestones

The four points at which the thing is worth showing someone.

| | After | What is true |
|---|---|---|
| **M1 — A desktop** | Phase 9 | You can log in and use windows. It is not yet a computer: no file manager, no settings, no way to install anything |
| **M2 — A computer** | Phase 11 | Files, search, settings, a terminal, and the system's own applications. Usable by its author, all day, with a terminal nearby |
| **M3 — Installable** | Phase 12 | Somebody else can install it on their own machine and it updates and backs itself up. This is the first release that can have users |
| **M4 — A replacement** | Phase 13 | A browser, an editor, the Linux catalogue, and accessibility. **1.0** |

## Parallelism, and the honest arithmetic

192 engineer-months is **sixteen years for one person and about four years for four**, and that is
the most important number in this document. Every plan of this shape fails by not saying it.

What can genuinely run in parallel, given the dependencies:

```
Phase 7  ────────────────┐
  SDK ──────────────┐    │
  Tests ────────────┤    │
  Services core ────┘    │
                          ├── Phase 8 authority ──┐
                          │                        ├── Phase 10 files+search ──┐
                          └── Phase 9 desktop ─────┤                            ├── 12 ── 13 ── 14
                                                    └── Phase 11 settings ──────┘
```

- **Phases 8 and 9 are independent** once the SDK exists, and they are the two largest. Two people or
  two tracks split cleanly here.
- **Phase 11's Settings panes are independently parallel** — a pane is a self-contained unit of work
  once the settings store and the service façades exist, which is the argument for building them in
  Phase 7.
- **Phase 13's runtime bundles do not depend on anything in 8–12** and can start whenever there is
  someone to do recipe work; it is also the least interesting work in the plan, which is a real
  scheduling consideration.
- **Documents 06 and 14 share the verb vocabulary** and doc 06 defines it, so automation cannot lead.

## The two cut lines, drawn in advance

Drawn now, so that they are decisions rather than a panic later. In order of what goes first:

**Cut line 1 — reach M4 sooner, ~24 EM.** Drop Photos, Music/Video and Notes (8 EM) — the browser
covers all three for most people. Drop automation to 1.1 (9 EM) — it is a differentiator and it is not
a thing anyone needs on day one. Drop printing (2 EM) and Wine (2 EM). Reduce Glance to eight
providers (1 EM) and the icon set to 250 glyphs (1 EM).

**Cut line 2 — if the schedule is still wrong, ~30 EM more.** Drop the Store application, keeping
`tpkg` and sideloading (2.5 EM). Drop Rewind's network destinations (1.5 EM). Drop the Qt and Electron
runtimes and ship Flatpak only for those (2 EM, at the cost of VS Code feeling foreign). Drop
fractional scaling for integer-only (1.5 EM). ⚠ **And then stop cutting**: what is left below this
line — the sandbox, the update transaction, accessibility, the test gates — is the part that cannot be
added afterwards, and a 1.0 that ships without them is a 1.0 that has to be rebuilt.

## Post-1.0

| Item | EM | Note |
|---|---|---|
| Real hardware bring-up: GPU drivers, laptops, firmware quirks | 6 | Gates everything below |
| Gaming: Steam, Proton, gamescope, VRR, HDR, Game Mode | 4 | Mostly packaging once the GPU works |
| Device pairing, then **cross-device clipboard** and **Nearby Share** | 6 | Both need pairing, which needs the keychain; building pairing first is why they are here rather than in 1.0 |
| Phone integration: notifications, files, SMS, media control | 8 | A protocol and an Android application; the largest post-1.0 item |
| Mail, Calendar, Contacts | 15 | Doc 11's cut list, reconsidered against what real users miss |
| Voice control, switch control | 5 | Over doc 14's verbs, which is why it waits |
| Sync for Notes, Photos, settings | 8 | Needs a service, which needs an operator |
| Fast user switching, guest | 2 | |
| Apple Silicon native via Asahi | ? | A research track, not a line item |
