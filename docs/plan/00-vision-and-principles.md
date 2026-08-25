# 00 — Vision and Principles

## What Trinix is

A general-purpose desktop operating system, built on the Linux kernel, whose user-facing surface is
written in C# and whose applications are `.app` bundles that the system verifies, sandboxes and
updates. It is aimed at one person: somebody who currently uses a Mac, likes the way it works, and
wants a machine they own.

That aim is narrower and more demanding than "a nice Linux desktop", and every decision below falls
out of it.

## What "one operating system" commits us to

The failure mode Trinix exists to avoid is the one every Linux desktop has: thirty applications that
each solved the same problem differently, sitting under a theme that makes them look alike from six
feet away. A user finds the seams within a day — two notification styles, three file dialogs, four
places that store a password, a settings application that opens a second settings application.

So the commitment is not visual. It is that **there is one implementation of each concept**, and
every application reaches it through the same interface:

| Concept | One implementation | Applications may not |
|---|---|---|
| Notification | `io.trinix.Notifications` (doc 02) | draw their own popup, ever |
| Password or token | the keychain (doc 05) | write a secret to a file in their own container |
| File chooser | the broker's chooser (doc 04) | enumerate `$HOME` to build their own |
| Search result | a Beacon provider (doc 06) | ship a launcher |
| Preference storage | the settings store (doc 08) | invent a config file format |
| Update | the updater (doc 10) | check for their own updates |
| Window frame, menu bar | the compositor and the shell (doc 03) | draw a menu bar |
| Permission prompt | the broker (doc 04) | ask the user for access in their own dialog |

Each row is enforceable, and where it can be enforced by a build gate or by the sandbox rather than
by a review, it is. Doc 16 says which.

## Non-negotiables

These do not get traded away for a milestone.

1. **C# is the primary language, and the primary language is not a wrapper.** A Trinix service is a
   C# program with a C# public surface. Where a C library does something we would do badly —
   wlroots, Mesa, HarfBuzz, xkbcommon, the kernel — we bind it and own the policy above it, which is
   already how the compositor is built. Where the C library exists only because C was the ambient
   language of 2004, we write the C# and delete the dependency.
2. **Both architectures, always.** arm64 and x86_64 are peers. A feature that works on one is not
   done. This has held since Phase 0 and it is cheap to keep and ruinous to restore.
3. **The base image is immutable and signed.** Applications live outside it. Updates are A/B, and a
   failed boot rolls back without a human. Nothing in this plan may require writing to `/usr`.
4. **An application's authority is declared, signed, and enforced.** Doc 04. The declaration already
   exists and is already signed; the enforcement is the work.
5. **The system does not need the application.** Window management, focus, the menu bar, the
   permission dialog and the shell's own responsiveness never wait on a client's event loop.
6. **Nothing ships without a way to test it headlessly.** wlroots has a headless backend, the rootfs
   runs as a container, and `wlr-screencopy` produces a PNG. A subsystem whose only test is a person
   looking at a screen is a subsystem that will regress silently. Doc 16.
7. **No telemetry.** Not opt-out, not anonymised, not "crash reports only by default". The Store
   learns nothing about who installed what, and doc 09 says how the repository is designed so it
   cannot.

## What Trinix is not

- **Not a distribution in the packaging sense.** There is no dependency solver, no 30,000-package
  archive, no `/etc` merge conflicts on upgrade. The base is one image; applications are
  self-contained bundles against a small number of versioned runtimes. This is the decision that
  makes a two-person package manager tractable, and it is the reason the base recipe count is kept
  near sixty rather than allowed to drift toward six hundred.
- **Not a macOS clone at the pixel level.** The traffic lights, the menu bar and the dock are taken
  because they are good and because the target user has them in their fingers. `Cmd`-shaped
  shortcuts are taken for the same reason. What is not taken is anything that is a compromise with
  Apple's history rather than a design: no bundle-inside-a-bundle `Contents/MacOS`, no resource
  forks, no launch services database that has to be rebuilt when it corrupts.
- **Not a browser engine, not a kernel fork, not a libc.** Doc 13 § Browser and the toolchain
  decisions in `IMPLEMENTATION_PLAN.md` § 1 are final.
- **Not a server distribution.** Nothing here is designed for a machine without a display. That is
  not a promise it will not run on one; it is a statement about where the effort goes.
- **Not multi-seat, not remote-desktop-first, not thin-client.** One user, one machine, possibly
  several displays.

## The ten things that have to be excellent

Ranked, because the ranking is what decides what gets cut when a phase runs long. Each is a "wow"
item from the brief with an owner document and a falsifiable form.

| # | Feature | Falsifiable as | Doc |
|---|---|---|---|
| 1 | **Window management** | Snapping, an overview, and per-monitor workspaces, with every gesture completing in one frame while the focused client is `SIGSTOP`ped | 03 |
| 2 | **Universal search** | One shortcut; typing `wifi`, `2+2`, a file name, a person and a system verb each produce the right first result inside 120 ms | 06 |
| 3 | **Quick Look** | Space over any of fourteen file kinds renders a real preview in under 200 ms cold, with a documented plugin interface | 07 |
| 4 | **Transactional updates** | A power cut at any point during an update leaves a machine that boots the previous system | 10 |
| 5 | **A real sandbox** | An application without `files.home` cannot read `~/Documents`, demonstrated by a test that tries | 04 |
| 6 | **One design language** | Every first-party window is one theme, one control set, one font stack, and the utility classes that draw them are generated from one token file | 01 |
| 7 | **Time-machine restore** | Browse a snapshot as a normal folder; restore one file or the whole machine from the recovery image | 10 |
| 8 | **Automation** | A trigger and three actions, authored in a GUI, saved as a file, runnable from PowerShell | 14 |
| 9 | **Nearby share** | A file reaches another Trinix machine on the same network in two clicks, encrypted, without an account | post-1.0 |
| 10 | **Cross-device clipboard** | Copy here, paste there, over the same transport as 9 | post-1.0 |

9 and 10 are the two the brief was most excited about and the two that are cut from 1.0. The reason
is in doc 17: both need a device-pairing story, and a pairing story built before the keychain and the
permission broker exist would be built twice.

## Quality bars

- **Latency.** A pointer-driven interaction — hover feedback, a menu opening, a window beginning to
  move — is the compositor's, and its budget is one frame. Anything the shell process draws has a
  budget of 16 ms at 60 Hz with no allocation on the steady-state path. An application launch, from
  the click to a window with content, is 400 ms for a native application; that is a measurement in
  CI, not an aspiration, and it is the number that decides framework-dependent versus NativeAOT per
  application rather than a preference.
- **Memory.** A session with the compositor, the shell, the four resident services and no
  applications fits in 400 MB. This is checked, because a desktop that idles at 2 GB has quietly
  decided what class of machine it runs on.
- **Boot.** Power to a login prompt in 8 s in QEMU with `-accel hvf`, to a usable desktop in 12 s.
- **Correctness of the security story over its completeness.** A permission model with four
  categories that are genuinely enforced beats one with forty that are advisory. Doc 04 keeps the
  vocabulary coarse for exactly this reason, and it is the same argument
  [`app-bundles.md`](../app-bundles.md) already made when the field was defined.

## How these documents are written

Same discipline as Vixen's plan, and for the same reason — a design record is only worth keeping if
it says *why*.

- A decision appears once, in the document that owns it, and is referenced from everywhere else.
- Every table of choices carries a reason column, and "it is what everyone does" is not a reason.
- A ⚠ marks something that will bite: a decision that contradicts an earlier one, a limit that is
  easy to forget, a place where the obvious implementation is wrong.
- A gap is written down as a gap, with what it costs. A plan that lists only what will work is a
  brochure.
