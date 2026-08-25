# 04 — Sandbox and Permissions

`Info.json` already carries a `permissions` array. It is already inside the signature — which
[`app-bundles.md`](../app-bundles.md) § 1 argues was the right thing to do first, since a permission
added after signing is not a permission. And it is already, in that document's own words, **not
enforced**: nothing stops an application opening a socket it never declared.

This document is the enforcement.

## What a permission is

> An application's authority is the set of things it can do **that it could not do by being a
> process**. Everything else is not a permission, it is a fact about Linux.

That framing decides the vocabulary. `network.client` is a permission because the default is no
network. "Read its own files" is not, because it is what a process is. The failure mode this avoids
is the forty-checkbox permission sheet that nobody reads and that describes, mostly, capabilities the
application never lacked.

## The mechanism: systemd, not a second sandbox

Trinix has systemd as PID 1 and a Gatekeeper that already launches applications
([`Trinix.Gatekeeper/Launcher.cs`](../../src/Trinix.Gatekeeper/Launcher.cs)). Launching an
application becomes: verify the bundle (already built), **construct a transient systemd unit** from
its signed permissions, and start it there.

| Requirement | systemd property |
|---|---|
| Cannot see the filesystem | `RootDirectory=` over a minimal composed root, `TemporaryFileSystem=`, `BindReadOnlyPaths=` for the runtime and the bundle |
| Its own home | `BindPaths=~/Library/Containers/<id>/Data:/home/<user>` — the app's `$HOME` *is* its container |
| No network | `PrivateNetwork=yes` plus a shared namespace joined only when `network.client` is present |
| No devices | `PrivateDevices=yes`, `DeviceAllow=` empty; everything arrives as an fd from `trinixd` (doc 02) |
| No other processes | `PrivatePIDs=`/`ProtectProc=invisible` |
| Syscall floor | `SystemCallFilter=@system-service`, `SystemCallArchitectures=native`, `NoNewPrivileges=yes`, `MemoryDenyWriteExecute` where the runtime allows it |
| Kernel surfaces | `ProtectKernelTunables`, `ProtectKernelModules`, `ProtectControlGroups`, `RestrictRealtime`, `RestrictSUIDSGID` |
| Resource bounds | `MemoryMax=`, `CPUWeight=`, `TasksMax=` — which is also what makes doc 11's System Monitor able to say something true per application |

| Choice | Reason |
|---|---|
| **systemd's sandboxing, not bubblewrap** | Both are namespaces and seccomp underneath. systemd is already PID 1, already supervises, already gives us cgroup accounting for free, and already has the properties audited by a great many people. Shipping bwrap means a second mechanism to keep current and no supervision |
| A **transient unit per launch**, not a pre-written unit file | The unit is derived from the signature at launch. A unit file on disk is a permission grant that outlives a re-signing, and one an attacker can edit |
| The app's `$HOME` is its container | The single most effective simplification available. Every library that writes to `$HOME/.config` — and every one of them does — writes into the container, and nothing has to be patched |
| ⚠ `MemoryDenyWriteExecute` is **off** for .NET applications | The JIT needs W^X transitions. It is on for NativeAOT bundles, and the difference is recorded per-bundle rather than being a system-wide surrender |
| ⚠ The sandbox is **not** a defence against the kernel | A kernel LPE defeats it. That is true of every desktop sandbox including macOS's, and the honest statement belongs here rather than in a footnote |

## The vocabulary

Deliberately coarse, per doc 00's quality bar and the argument already made in `app-bundles.md`.
Fourteen permissions, in four groups.

| Permission | Grants | User-visible as |
|---|---|---|
| `display` | A Wayland connection and a window | (never shown — an app with no window is not an app) |
| `network.client` | Outbound sockets, DNS | "Connect to the internet" |
| `network.server` | Listening sockets | "Accept incoming connections" |
| `files.home` | Broker-mediated reach into the user's documents | "Access your files" |
| `files.removable` | Same, for `/Volumes` | "Access removable drives" |
| `devices.camera` | A camera fd | "Use the camera" |
| `devices.microphone` | An audio input node | "Use the microphone" |
| `devices.location` | Position | "Know your location" |
| `devices.usb` | A raw USB device, chosen by the user each time | "Talk to USB devices" |
| `system.notifications.critical` | Notifications that pierce Focus | "Send urgent alerts" |
| `system.automation` | Invoke other applications' verbs (doc 14) | "Control other applications" |
| `system.background` | Keep running with no window; be started at login | "Run in the background" |
| `system.capture` | Hold a screen-capture stream after the picker | "Record the screen" |
| `system.input` | ⚠ Reserved and **never granted by the Store**. Accessibility tools and remote-control tools only, by explicit user grant in Settings | "Control your computer" |

Everything else an application might want — a font, the clipboard when focused, its own settings, a
notification, a search index contribution, the time — is unpermissioned, because the mediating
service already scopes it to the caller's identity.

### The two axes that do not exist

The brief asks for `Documents → Read / Write` and `Camera → Use`. Trinix has **one axis**: you have
`files.home` or you do not, and the read/write distinction is decided per-file by the broker at the
moment the user picks it. The reason is that a read-only grant over a whole directory is a promise
the kernel is being asked to keep about a mount, and the honest cases — "this app should only read my
photos" — are much better served by the user picking the photos.

## The broker

`trinix-broker`, root, on the system bus, and the process the whole model rests on.

Its job is to be **the only path from an application to something outside its container**, and its
central mechanism is that the user's choice *is* the grant:

```
app: "open a file"
  → broker asks the shell to draw a file chooser   (out of the app's process, out of its address space)
  → user picks ~/Documents/tax.pdf
  → broker opens it and passes the fd back
```

The application never saw a directory listing. It has an fd for one file. This is the mechanism that
makes `files.home` weak-by-default and strong-by-use, and it is why the file chooser must be the
shell's and not a control in `Trinix.Sdk.Controls`.

For paths the user picks repeatedly, the broker issues a **bookmark**: an opaque, app-scoped,
revocable token that reopens a specific file or directory later without a prompt. Bookmarks are
listed in Settings ▸ Privacy per application and revoking one is immediate.

### The compat face: portals

The broker implements `org.freedesktop.portal.*` — FileChooser, OpenURI, ScreenCast, Screenshot,
Camera, Notification, Secret, Settings, Inhibit, Background, Documents. This is the whole reason
doc 13's ecosystem applications work at all, and it is the two-faces rule at its most load-bearing:
one broker, one consent UI, one privacy pane, whether the caller is a Trinix `.app` or a Flatpak.

⚠ **Attribution.** The broker decides who is asking from the caller's cgroup, which maps to the
transient unit, which maps to a verified bundle identity. A caller that is not in such a unit — a
process started from a terminal, a Flatpak with its own containment, a compat-face caller — gets an
**unattested** identity: it is named by its executable, its grants are session-scoped rather than
persistent, and the consent dialog says so in plain words. This is the lossy projection doc 02 § The
two-faces rule warned about, and it is the correct behaviour: an identity we cannot verify must not
be able to accumulate durable authority.

## Consent

The dialog is the security model's user interface, and most of the model's real-world strength is in
how it behaves rather than what it protects.

| Rule | Reason |
|---|---|
| **Prompt on use, never at install** | An install-time list is read by nobody and answered by everybody the same way. A prompt at the moment the camera is wanted is a prompt with context |
| The dialog is drawn by the shell, **anchored to the requesting window**, and the compositor guarantees the anchoring | A permission dialog an application can position, imitate or overlay is not a permission dialog. This is why it is a Wayland-anchored surface (doc 02 § the transport boundary) rather than a free-floating window |
| Three answers: Allow, Allow once, Don't allow | Not "Always allow" — the durable grant is the unmarked default, and "once" is the escape hatch. Four buttons is one too many to read |
| A refusal is **permanent until changed in Settings**, and the app is told | Re-prompting until the user gives in is the dominant dark pattern of the mobile era |
| No prompt may appear without a user action in the requesting app within the last few seconds | Kills the "prompt at 3am until accepted" attack, and it is cheap: the compositor knows when that surface last had input |
| The dialog names the **application**, the **thing**, and — where it can — the **why the app declared** | `Info.json` may carry a `usageDescription` per permission; `trinix doctor` warns when it is missing, the Store requires it |

## What this does not do, stated plainly

- **It does not protect the user from an application they granted `files.home` and then used.** A text
  editor with access to your documents can read your documents. Sandboxes bound *reach*, not intent.
- **It does not make an unsigned application safe.** Developer mode (doc 01) relaxes signing, not
  containment: an unsigned app still runs in a container, but its identity is unattested and its
  grants are session-scoped.
- **It does not contain the compositor, the shell, or `trinixd`.** They are the trusted computing
  base. Keeping that base small is why doc 02 refuses to grow the daemon set.
- **It does not survive a kernel bug, a bad `SystemCallFilter` interaction, or a permission the user
  granted by habit.** Doc 16 gates the first two with tests; nothing gates the third.

## Migration, and the awkward first year

Enforcement arriving after the format is the pleasant version of this problem — the field exists and
is signed — but there is still a cliff: the day the sandbox turns on, every bundle built before it
either works or does not, and the ones that do not are the ones whose declared permissions were
aspirational.

1. **Audit mode first.** The broker runs, the units are constructed, and every violation is *logged
   and allowed*. `trinix doctor` reads the log and tells a developer exactly which permission their
   application actually needed. This runs for one full release.
2. **Enforce for new `minimumSystemVersion`.** A bundle declaring the release in which enforcement
   landed is enforced; older bundles stay in audit mode. `minimumSystemVersion` is already in
   `Info.json` and already signed, which is what makes this possible without a new field.
3. **Enforce everywhere**, one release later, with the audit log's evidence that the long tail is
   empty. Trinix has three applications today, so the tail is short and the window can be shorter than
   it would be for a mature platform — that is an argument for doing this *now* rather than later.

## Effort

| Piece | EM |
|---|---|
| Transient-unit construction, the composed root, the container layout | 2.0 |
| `trinix-broker`: identity, attribution, grant store, bookmarks | 2.5 |
| The portal compat face (11 interfaces) | 3.0 |
| Consent UI + the anchoring protocol + the Privacy pane's backing | 1.5 |
| Audit mode, the log, and `trinix doctor`'s permission cross-check | 1.0 |
| Escape-test suite: an app that tries each of the fourteen and fails | 1.0 |
| **Total** | **11.0** |
