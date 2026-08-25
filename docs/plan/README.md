# Trinix — Implementation Plan

Trinix is a Linux distribution whose goal is to be **somebody's only computer** in place of a Mac.
Not a desktop environment, not a theme, not a curated package list: an operating system with one
design language, one permission model, one search, one updater, and one SDK, in which the Linux
kernel is an implementation detail that a user never has to learn.

This directory is the authoritative design record for the half of that which is not built yet: what
each subsystem is meant to be, and why each decision was taken.

**These documents do not say what is built.** [`../../README.md`](../../README.md) does, and
[`../../IMPLEMENTATION_PLAN.md`](../../IMPLEMENTATION_PLAN.md) is the record of Phases 0–6 — the
toolchain, the base system, the image, .NET and PowerShell, the compositor, the Vixen platform
backend, and the bundle format. Where a document here and the code disagree, the code wins and the
document is wrong; say so in a pull request rather than in a comment.

This plan **supersedes `IMPLEMENTATION_PLAN.md` § 4's Phases 7 and 8**, which were two paragraphs
standing in for about a hundred engineer-months, and continues the phase numbering to 14. Phases 0–6
are not restated here.

Read 00, 01 and 02 first. After that each file is the spec for its subsystem, and they are ordered
by what depends on what, not by what a user sees first.

| # | Document | Scope |
|---|---|---|
| 00 | [Vision and Principles](00-vision-and-principles.md) | What "one operating system" commits us to, the non-negotiables, what Trinix is not |
| 01 | [The System SDK](01-system-sdk.md) | `Trinix.Sdk` — app lifecycle, the service clients, the design language, templates, developer mode |
| 02 | [Services Architecture](02-services-architecture.md) | The daemon set, the IPC decision, the two-faces rule, notifications, clipboard |
| 03 | [Shell and Window Management](03-shell-and-window-management.md) | Compositor policy, the shell process, dock, menu bar, overview, workspaces, screenshots |
| 04 | [Sandbox and Permissions](04-sandbox-and-permissions.md) | Containment, the broker, portals, consent, what a permission actually stops |
| 05 | [Identity and Keychain](05-identity-and-keychain.md) | Users, login, the secret store, TPM binding, the Secret Service face |
| 06 | [Search](06-search.md) | Beacon — the index, the two provider kinds, actions, the latency budget |
| 07 | [Files and Quick Look](07-files-and-quick-look.md) | The file manager, Glance previews, volumes, network locations, the operations queue |
| 08 | [Settings and System Services](08-settings-and-system-services.md) | One Settings application, and the network / audio / display / power services under it |
| 09 | [Applications and the Store](09-applications-and-the-store.md) | Repositories, `tpkg`, the Store, install and launch lifecycle, developer distribution |
| 10 | [Updates, Recovery and Backup](10-updates-recovery-and-backup.md) | A/B images, content-defined chunking, rollback, recovery, Rewind snapshots |
| 11 | [Core Applications](11-core-applications.md) | The twenty applications, what each is for, what each costs, and the four that are cut |
| 12 | [Terminal and the Shell Language](12-terminal-and-the-shell-language.md) | The terminal emulator, PowerShell as the system's admin surface, object output |
| 13 | [Compatibility](13-compatibility.md) | Runtime bundles, Flatpak as a guest format, XWayland, Wine, gaming, Zen and VS Code |
| 14 | [Automation](14-automation.md) | Triggers, actions, the graph, and why it is the same objects PowerShell already has |
| 15 | [Accessibility](15-accessibility.md) | The AT-SPI bridge, the control-level rules, and what actually ships |
| 16 | [Build, CI and Testing](16-build-ci-and-testing.md) | The gates Trinix does not have. Read this one early; it is the smallest and the most overdue |
| 17 | [Roadmap](17-roadmap.md) | Phases 7–14, exit criteria, sequencing, effort |
| 18 | [Risks and Open Questions](18-risks-and-open-questions.md) | Ranked risks, and the five decisions that need a human |

## The three rules these documents are written against

1. **A user must never learn the word "Linux" to use Trinix.** Not in an error message, not in a
   settings pane, not in a file dialog. Where a Linux concept leaks, that is a defect with a bug
   number, not a documentation task.
2. **Every service has a native face and a standard face.** The native face is a typed C# interface
   that Trinix's own applications call. The standard face is the freedesktop interface that the rest
   of the world already calls — `org.freedesktop.portal.*`, `org.freedesktop.Secret.*`,
   `org.freedesktop.Notifications`, AT-SPI. One implementation, two front doors. This appears in
   documents 02, 04, 05, 08 and 15, and it is the single decision that makes "one operating system"
   affordable rather than a rewrite of the Linux desktop.
3. **The system never requires an application to be responsive.** Already guaranteed by the
   compositor for dragging, focus and menus ([the platform contract](../vixen-platform-contract.md));
   documents 03, 04 and 07 extend it to the shell, the permission dialog and file operations.

## Five corrections to the brief

The brief these documents answer is a thirty-section list of what a macOS replacement contains. Five
of its assumptions changed what got planned, and each is argued where it lands.

1. **The SDK is not a new UI framework.** The brief asks for controls, menus, sidebars, lists,
   tables, trees, tabs, dialogs, declarative markup and one design language. Vixen already has all
   of it — VXML markup with a Roslyn generator, VCSS with a Tailwind-shaped utility layer, signals,
   flexbox, HarfBuzz text, forty standard controls and eleven advanced ones including DataGrid,
   TreeView, Docking and CodeEditor, all tested. Writing a second widget library is the largest
   available mistake and buys nothing. `Trinix.Sdk` is **services, lifecycle and a theme**, and doc
   01 is mostly about what it deliberately does not contain.
2. **"Ship Zen and VS Code" is a runtime decision, not an application decision.** Zen is Firefox and
   VS Code is Electron; both want GTK, which is forty recipes Trinix does not have and should not
   put in an immutable base image. Doc 13 makes the Linux desktop stack a **signed `.tdi` runtime
   bundle** on machinery that already exists, and accepts Flatpak as a guest format so the catalogue
   arrives without a second permission model. The word "Flatpak" never appears in the interface.
3. **A permission that nothing enforces is not a permission.** `Info.json` already carries a signed
   `permissions` array and [`app-bundles.md` § 1](../app-bundles.md) says plainly that nothing
   enforces it. Doc 04 closes that, and the mechanism is systemd — which is PID 1 already, and whose
   `RootDirectory`, `BindPaths`, `SystemCallFilter` and `PrivateDevices` are the sandbox we would
   otherwise ship a second copy of.
4. **The productivity block is the largest single cost and the smallest differentiator.** Mail,
   Calendar, Contacts, Photos and Music together are about a quarter of the remaining work and are
   the five applications a user is most likely to already be running in a browser. Doc 11 cuts them
   out of 1.0 with the reasoning, ships the protocol plumbing (CalDAV, CardDAV, IMAP) as SDK
   libraries so they can arrive later without an architecture change, and spends the time on Files,
   Search, Settings and the Store instead.
5. **Scale.** Roughly **190 engineer-months** from Phase 6 to a 1.0 a person could use as their only
   computer — about two fifths of it the shell and the applications, and about a twelfth
   compatibility. That is four years for four people and sixteen for one, which doc 17 states out
   loud and doc 18 ranks first among the risks. Cross-device clipboard, phone integration and Nearby
   Share are past that line, in doc 17's post-1.0 list with their budgets. Plan against the four
   milestones in doc 17, not against a date.
