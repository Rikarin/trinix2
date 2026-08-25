# 02 — Services Architecture

The layer between C# applications and Linux. Doc 00's rule — one implementation of each concept —
lives or dies here, because a service is where the second implementation would otherwise appear.

## The two-faces rule

Every Trinix service has two front doors onto one implementation:

```
                    io.trinix.Notifications          org.freedesktop.Notifications
                    (typed, versioned, ours)         (the interface GTK/Qt/Electron call)
                              \                        /
                               \                      /
                                Trinix.Services.Notifications
                                         one implementation
```

| | Native face | Compat face |
|---|---|---|
| Named | `io.trinix.<Service>` | the freedesktop / de-facto standard name |
| Defined by | a C# interface in `Trinix.Services.Contracts`, XML generated from it | somebody else's XML, transcribed by hand and pinned |
| Shaped for | what Trinix's applications need, including things the standard cannot express | what the existing Linux ecosystem already sends |
| Versioned | by us, with the SDK | never — it is a compatibility surface and it changes when upstream changes |
| May be dropped | no | yes, per-service, if the ecosystem moves |

**Why this and not "just use the freedesktop interfaces".** Because they are the intersection of what
several desktops could agree on a decade ago, and Trinix needs things none of them carry: a
notification that knows which *bundle identity* sent it (so the sandbox can attribute it), a
permission decision attached to a Wayland surface, a search result that is an action rather than a
string. Building only on the standard face means every Trinix-specific capability arrives as a
vendor extension to a protocol we do not own.

**Why this and not "just use our own".** Because Zen, VS Code, and every Flatpak in doc 13's
catalogue call the standard names, and a desktop where third-party applications cannot raise a
notification is not a desktop.

⚠ The compat face is **not a superset and not a subset** — it is a projection, and the projection is
lossy in one direction on purpose. A caller on the compat face gets the process's identity rather
than a signed bundle identity, and therefore gets the permissions of an *unattested* caller. Doc 04
§ Attribution says what that means. This is the one place where the rule has teeth rather than being
a convenience.

## The transport decision

| Option | Verdict |
|---|---|
| **D-Bus** | **Chosen.** Already in the base image, already how systemd, logind and the whole Linux desktop are addressed, already has activation, introspection, per-message credentials via `SO_PEERCRED`, and a well-understood proxy story that doc 04's sandbox needs. Its faults — a text-ish marshalling, a daemon in the path, no zero-copy — are irrelevant at the message rates a desktop service sees |
| Wayland extensions | **For anything that belongs to a surface**, and only that. Already the reasoning in [the platform contract](../vixen-platform-contract.md) § Menus: a menu belongs to a window, D-Bus has no notion of one, and every previous global-menu design needed a second protocol just to say which bus name went with which window. The same argument extends to permission dialogs anchored to a window, and to drag-and-drop |
| A bespoke protocol over `AF_UNIX` | Rejected. It would be faster and it would buy nothing: the whole compatibility story would have to be bridged, and we would own an IPC system in addition to an OS |
| gRPC / HTTP | Rejected. A localhost TCP or HTTP/2 stack for a clipboard is absurd, and it has no peer-credential story |

**Where the boundary is drawn:** if the answer to "who is asking?" is a *window*, it is Wayland. If
it is a *process*, it is D-Bus. Bulk data — a file being previewed, a screen recording — is a file
descriptor passed over D-Bus, never a byte array in a message.

## The daemon set

Small on purpose. Each line is a process, its lifetime, and why it is separate.

| Daemon | Runs as | Lifetime | Why not folded into another |
|---|---|---|---|
| `trinixd` (exists) | root, system bus | boot → shutdown | The privileged half: mounts, power, updates, device policy. It is the only thing here that runs as root, and it stays small for exactly that reason |
| `trinix-broker` | root, system bus | boot → shutdown | Doc 04. Must outlive and out-privilege every app it mediates, and must not share an address space with anything that parses untrusted input |
| `trinix-secrets` | user | session | Doc 05. Holds key material; separate so that a crash dump of anything else does not contain it |
| `trinix-shell` | user | session | Doc 03. The only one with a Vixen UI and therefore the only one with a garbage collector on a user-visible path |
| `trinix-beacon` | user | session, idle-exits | Doc 06. Indexes; the one process expected to use real CPU, so it is the one that can be `nice`d and killed |
| `trinix-settings` | user | activated, idle-exits | Doc 08. The settings *store*, not the application |
| `trinix-automation` | user | activated | Doc 14. Runs user-authored logic, which is why it is not inside anything else |
| `trinix-compositor` (exists) | user | session | Not a D-Bus service at all — it is the Wayland display, and the shell talks to it over the protocol |

Notifications, clipboard, search, network, audio and power do **not** get their own processes:

- **Notifications and clipboard** live in `trinix-shell`, because their state *is* shell state and a
  separate process would mean two owners of one notification centre.
- **Network and audio** are façades in `trinixd` over `systemd-networkd`/`iwd` and PipeWire (doc 08).
  A daemon whose whole job is to forward to another daemon is a daemon nobody maintains.
- **Power** is a façade over logind and the kernel, in `trinixd`.

⚠ **`trinix-shell` holding the clipboard means the clipboard dies with the shell.** That is
deliberate — a clipboard that survives its session is a data-leak surface — but it also means the
shell's restart must preserve it, and doc 03 § Supervision owns that.

## Notifications

The brief's § 8, and the place where "applications shouldn't implement their own popups" needs to be
an enforced rule rather than a request. It is enforced structurally: a sandboxed application has no
`wlr-layer-shell` access (doc 04), so it *cannot* place a surface over another window. The rule is a
capability, not a guideline.

| | Decision |
|---|---|
| Model | A notification is a record with an identity, not a string: `(app, id, title, body, urgency, actions[], reply?, group, timestamp, expiry)` |
| Update and withdraw | First-class. An application updates its own notification by id rather than posting a second one — the reason every Linux desktop has a download notification per percent |
| Grouping | By app, then by the app's own `group` key. Collapsed after three |
| Actions | Named, and they **activate the application** rather than running a callback in the shell. A notification that survives its app's exit still works |
| Inline reply | Yes, first-class, because Messages and Mail are unusable without it and retrofitting it means a second protocol |
| Do Not Disturb and Focus | A Focus is a named set of (allowed apps, allowed people, schedule, triggers). DND is the built-in Focus with an empty allow-list. Doc 14 can switch Focus, which is most of what people want automation for |
| Critical | A separate urgency that pierces Focus, available only to system components and to apps holding `notify.critical` — which the Store does not grant automatically |
| History | 7 days, in the shell's store, searchable by Beacon, cleared on logout if the user asks |
| Compat face | `org.freedesktop.Notifications`. ⚠ Its `Notify` returns an id the caller can reuse, has no grouping, no reply, and hints rather than urgency. Those map to a degraded native notification and the mapping is documented once, in code, with a test per hint |

**Per-app permission is opt-out, not opt-in.** An application may post from first launch; the first
notification carries a "keep showing these?" affordance. Opt-in prompts on first launch train people
to say yes to everything, which is the failure mode doc 04 is built to avoid.

## Clipboard

The brief's § 9, and it is more interesting than it looks because Wayland's clipboard is
*offer-based*: the source advertises MIME types and the data is only transferred when a target asks.
That has two consequences.

1. **A clipboard that outlives its source needs the shell to take ownership.** When an application
   exits, its offer dies with it — the "I copied something and then quit and now the paste is empty"
   behaviour every Wayland desktop has. Trinix's shell **materialises** the current offer at the
   moment it is made, for types under a size limit, and re-offers it itself. Above the limit
   (a 400 MB image from an editor) it does not, and the paste-after-quit genuinely fails, which is
   the honest behaviour and is what a tooltip in the history explains.
2. **History is materialised data, so it is a privacy object.** 20 entries, in memory only, never on
   disk, dropped on lock, and an offer marked `x-kde-passwordManagerHint` or its Trinix equivalent is
   never recorded. ⌘⇧V opens it. An application may read the *current* clipboard when focused and may
   never read the history — the history is the shell's, and pasting from it is the user's action.

| Type | Behaviour |
|---|---|
| Text, rich text, image, files, colour | Materialised and in history |
| Anything over 8 MB, or a type not on the list | Passed through live, not in history |
| Marked sensitive | Passed through live, never in history, cleared from the current clipboard after 45 s |

Cross-device sync is post-1.0 and is doc 17's; the record shape above is designed to be
serialisable so that it does not need to change then.

## Settings storage

Doc 08 owns the Settings application; the *store* is a service because three things need it and none
of them should own it.

- Typed, schema-declared, with a default. A key with no schema is a bug the generator catches.
- Per-user, per-app namespaced, layered: **system default → administrator → user → managed profile**.
- Changes are events, not polls. An application that wants to follow the accent colour subscribes.
- Stored as one SQLite database per user, not a directory of files, because atomic multi-key writes
  and a watch-for-change that does not mean an inotify storm are both required and neither is
  possible over a directory of files.
- ⚠ **This is not a registry.** Applications store *preferences* here — small, user-meaningful,
  reset-to-default-able. Application *data* goes in the container. The schema requirement is what
  keeps them apart: if you cannot describe it in a settings schema, it is data.

## Devices, and how a service gets to hardware

`trinixd` owns everything under `/dev` that an application might want, and hands out file descriptors
rather than paths. This is the mechanism that makes a camera permission real: the application never
has `/dev/video0`, it has an fd it was given after the broker asked.

| Class | Backed by | Handed over as |
|---|---|---|
| Cameras | V4L2, via `libcamera` where the device needs it | an fd, plus the format negotiation, after `devices.camera` |
| Microphone / audio | PipeWire | a PipeWire node, per-app, with its own volume — which is what makes the brief's per-app volume free |
| Removable volumes | udisks-shaped logic *inside* `trinixd`, not `udisks2` | a mount under `/Volumes/<name>`, after a user action in Files |
| Input devices | libinput, owned by the compositor | never handed to an application. A keylogger is not a permission we offer |
| GPU | DRM render node | via the Wayland/Vulkan path only |
| Printers | CUPS | ⚠ not in the base image. Doc 08 § Printing, and it is a real recipe cost |

⚠ **We are not shipping `udisks2`, `NetworkManager` or `upower`.** Each is a competent daemon and
each brings a second policy owner, a second configuration language and a second set of concepts into
Settings. `trinixd` implements the small subset a personal machine needs directly against the kernel,
logind and networkd. The cost is real — roughly 2 EM across mounting, network and power — and it is
paid to keep doc 00's first rule. Where an ecosystem application expects one of them, doc 13's
runtime bundle may carry a shim; that is a compatibility problem, not a system one.

## Failure, restart and versioning

- Every user service is a systemd user unit with `Restart=on-failure` and a start-limit. A service
  that cannot start three times in a row is *reported to the user by the shell* rather than retried
  silently — a desktop that is quietly missing its keychain is worse than one that says so.
- `trinixd` and `trinix-broker` are system units, `Restart=always`, and their failure is a boot
  failure, which doc 10's rollback counts.
- Every interface carries a version; the SDK's proxy refuses a service older than it was built
  against with a message naming the system version required. This is the only clean answer once
  applications outlive system versions, and it is why `minimumSystemVersion` is already in
  `Info.json`.
- ⚠ **A service may not call into an application synchronously while holding a lock the application
  might need.** Every service→app call (notification action, automation verb, live search provider)
  has a timeout and a documented behaviour on expiry. This is written down here because it is the
  bug that will be written repeatedly otherwise.

## Effort

| Piece | EM |
|---|---|
| `Trinix.Services.Contracts` + the D-Bus plumbing and generator (shared with doc 01) | 1.0 |
| Notifications, both faces, incl. Focus | 2.0 |
| Clipboard, incl. materialisation and history | 1.0 |
| Settings store + schema generator | 1.5 |
| Device brokering in `trinixd`: mounts, camera, audio nodes | 2.5 |
| Network and power façades | 1.5 |
| Service supervision, versioning, the failure UI | 0.5 |
| **Total** | **10.0** |
