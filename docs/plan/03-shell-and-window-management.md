# 03 — Shell and Window Management

The part a user calls "the operating system". It is also the part where Trinix already has the most
code — a wlroots-backed compositor with a window manager, a menu-bar model and two protocol
extensions — and the least policy: [`WindowManager.cs`](../../src/Trinix.Compositor/WindowManager.cs)
says so itself, that if a rule about window behaviour is not in that file, Trinix does not have that
rule yet.

## The split: two processes, one seam

The brief asks for a single C# process owning panel, dock, desktop, window management, launcher,
notifications, control centre, workspaces, shortcuts, clipboard, drag and drop, screenshots and
search. That is the right *conceptual* grouping and the wrong process boundary.

```
trinix-compositor   NativeAOT · no allocation on the frame path · owns the display
   ├─ WindowManager       placement, focus, stacking, workspaces, snapping, the overview's geometry
   ├─ InputRouter         seat, accelerators, gestures, hot corners
   ├─ Animator            every motion of a window or workspace
   ├─ MenuModel           the trinix_menu_v1 tree, per surface       (exists: MenuBar.cs)
   ├─ ShellSurfaces       layer-shell: where the shell's panels are allowed to be
   └─ Capture             wlr-screencopy, and the recording pipeline's source

trinix-shell        framework-dependent · Vixen UI · restartable
   ├─ Panel               the menu bar's pixels, the status items, the clock
   ├─ Dock
   ├─ Launcher + Search   the Beacon front end (doc 06)
   ├─ NotificationCentre  and the popups                              (doc 02)
   ├─ ControlCentre
   ├─ Clipboard           history UI, and the materialisation         (doc 02)
   ├─ ScreenshotUI        the selection overlay and the recording controls
   ├─ Wallpaper / Desktop the desktop's icons
   └─ SessionUI           lock screen, the shutdown dialog, the switch-user sheet
```

| Choice | Reason |
|---|---|
| Two processes | A crash in a notification's markup must not end the session. One process means every bug in the shell's UI is a logout, and the shell's UI is where the churn is |
| The compositor stays NativeAOT and GC-free on the frame path | Already decided in `IMPLEMENTATION_PLAN.md` § 1 and already true. Vixen's UI allocates; putting it in the compositor's address space forfeits the property that makes the compositor trustworthy |
| The shell is a **privileged Wayland client** on `wlr-layer-shell` | It needs surfaces that are not windows — a panel that reserves space, a full-screen overlay above everything. layer-shell is the existing protocol for exactly that, wlroots implements it, and no third-party application gets it (doc 04) |
| Geometry and policy in the compositor, pixels in the shell | The seam is: *the compositor knows where everything is and what the pointer is doing; the shell knows what it looks like.* A menu highlight is the compositor telling the shell which item, not the shell hit-testing |
| The greeter is `trinix-shell` in a different mode, not a separate program | Two login-adjacent UIs is how a desktop ends up with a lock screen that looks nothing like the login screen |

### The invariant, stated so it can be tested

> **`SIGSTOP` any client, including the shell, and window management still works.**

Dragging, resizing, raising, focus, workspace switching, the overview, and the traffic lights are the
compositor's, and none of them sends a message the shell must answer. What *does* stop when the shell
stops is the shell's own pixels: the menu bar freezes, the dock does not respond. The compositor
detects a shell that has missed its frame callbacks for 2 s and paints a "the desktop stopped
responding" state over its layers, then restarts it. This is honest about what a process split costs,
and it is the cost worth paying — the alternative is that the same hang takes the windows with it.

⚠ **The menu bar's model is the compositor's, its pixels are the shell's.** This is the one place the
seam is uncomfortable: an application's menu arrives over `trinix_menu_v1` into the compositor, and
the compositor forwards the tree to the shell. It is worth it because the *accelerators* must be
handled without the shell — a ⌘S that waits on a UI process is a ⌘S that will one day be lost — and
they already are, per [the platform contract](../vixen-platform-contract.md) § Input.

## Window management

The brief's headline, doc 00's number one, and about a third of this document's effort.

### Placement, focus, stacking

Today: a cascade, click-to-focus, ⌘Tab cycling, and a stacking list. What it becomes:

| Rule | Behaviour |
|---|---|
| Placement | Centred on the active display for a first window; cascaded for subsequent windows of the same app; **remembered per (app, window role, display configuration)** and restored on the display it was on. ⚠ Keyed on the *display configuration*, not the display — plugging a monitor in must not scatter windows across a layout that no longer exists |
| Focus | Click to focus and raise. Never focus-follows-mouse; it is not an option, because it makes the menu bar ambiguous |
| ⌘Tab | Between **applications**, most-recently-used, with all of an app's windows raising together. ⌘` between windows of the front app. This is the mac model and it is the one that survives having forty windows |
| Stacking | Ordinary, always-on-top for a user-pinned window, and a `panel`/`overlay` band above for the shell's layers |
| App-specific rules | A rule set matched on app id and window role: default size, display, workspace, floating, ignore-on-overview. User-editable in Settings, and shipped with about a dozen entries for common applications |

### Snapping, and the thing the brief wanted

Drag a window to an edge or corner → a preview of the half or quarter it will take. On release,
**the remaining space offers the other windows**: a chooser of the open windows appears in the empty
region and picking one tiles it there.

That is the brief's idea and it is a good one; the detail that makes it work or not is *when the
chooser appears and how it goes away*. Decision: it appears only if there is more than one other
window on this workspace, it is dismissed by Escape, by clicking the desktop, or by 4 s of no
pointer movement, and it never steals the keyboard from the window just snapped. A helpful overlay
that captures Escape is a hostile overlay.

Snapping targets: left/right half, four quarters, top for fullscreen, and thirds via a modifier.
Split view — two windows in a fullscreen space with a draggable divider — is the same mechanism with
a persistent container, and is the one the brief called "native fullscreen + split view".

### Workspaces and the overview

| | Decision |
|---|---|
| Workspaces | Dynamic count, per-display, plus a full-screen space per fullscreened window (the mac model). A display's workspaces move with it |
| Switching | Three-finger swipe, ⌃← / ⌃→, and from the overview. Animated, and the animation is interruptible — a swipe that cannot be reversed mid-gesture reads as broken |
| Overview (Mission Control) | All windows on the current workspace, scaled and non-overlapping, plus the workspace strip. Live content, not screenshots — wlroots gives the compositor the client buffers already |
| Exposé | Same machinery, filtered to one application, on a long-press of its dock icon or ⌃↓ |
| Layout | A spring-embedder-free deterministic layout: sort by area, place into a row-balanced grid preserving relative screen position, so a window is roughly where it was. ⚠ Determinism matters — an overview that arranges the same windows differently on two invocations destroys the muscle memory that makes it fast |

### Displays

| | Decision |
|---|---|
| Scaling | Per-output, fractional, and **the buffer is rendered at the output's scale, never scaled up from 1×**. `wp_fractional_scale_v1` for clients that speak it; integer scale plus a downscale for those that do not, which is the only correct fallback |
| Multi-monitor | Arrangement in Settings, per-monitor workspaces, per-monitor scale and refresh, and a remembered arrangement keyed on the EDID set |
| Hotplug | Windows on a removed display move to the primary and remember where they were. This is entirely about the "remembered per display configuration" rule above |
| Night light | A colour-temperature ramp in the compositor's output state, scheduled by location or clock |
| HDR | ⚠ Out of scope for 1.0 and the reason is not effort: Trinix composites through pixman on the CPU today because there is no GPU driver (see [the platform contract](../vixen-platform-contract.md) § 1). HDR follows real hardware bring-up, not the shell |
| VRR | Same. Doc 13 § Gaming |

⚠ **Everything about the display pipeline is currently measured on lavapipe compositing through
pixman.** Animations that are comfortable on a real GPU may not be, and the honest engineering
response is to build the animator against a frame budget it measures rather than assumes, and to make
"reduce motion" a genuinely first-class path rather than a courtesy.

## The panel, the dock, and the desktop

**Panel.** The menu bar: the app menu on the left from `trinix_menu_v1`, status items on the right.
Status items are not a tray — there is no `XEmbed`, no `StatusNotifierItem` free-for-all. A status
item is declared by an application in its `Info.json`, rendered by the shell from a description
(icon, tooltip, menu, optional text), and the application never draws into the panel. The compat
face for `StatusNotifierItem` exists for doc 13's ecosystem applications and is a projection with the
same lossy-attribution note as doc 02.

**Dock.** Pinned and running applications, minimised windows, downloads and trash. Magnification
optional and off by default. Position bottom/left/right, autohide. A dock icon is the application's
identity, so it carries the badge a notification set and the progress a file operation reported.

**Desktop.** Wallpaper, and icons for `~/Desktop`. The desktop is a Files view with a different
layout, not a second file manager — doc 07 owns the code.

**Control Centre.** Wi-Fi, Bluetooth, audio output and per-app volume, display brightness, night
light, Focus, keyboard backlight, battery, screen mirroring. Every tile is a view onto a service in
doc 02/08, and a tile whose service is unavailable is absent rather than dead.

## Global shortcuts

| Chord | Action |
|---|---|
| ⌘Space | Search (doc 06) |
| ⌘Tab / ⌘` | Application / window switch |
| ⌃↑ / ⌃↓ | Overview / Exposé |
| ⌃← / ⌃→ | Workspace |
| ⌘⇧3 / ⌘⇧4 / ⌘⇧5 | Screen / selection / capture UI |
| ⌘⇧V | Clipboard history |
| ⌘Q / ⌘W / ⌘, / ⌘M / ⌘H | Quit / close / preferences / minimise / hide — **the compositor enforces these are delivered**, so an application cannot make ⌘Q mean something else |
| ⌃⌘Q | Lock |
| Super | Launcher |

The **Command key**: Trinix maps `Super` to `Command` and puts the standard chords on it, keeping
`Control` for the terminal. This is the single decision that makes the target user comfortable and
the single one that annoys a Linux user most; it is a setting, and the default is Command. Doc 08
§ Keyboard owns the remapping, which happens in the compositor's xkb layer so it applies to every
client including XWayland.

⚠ An application may **not** register a global shortcut. It registers an *accelerator on its own
menu* (which works when it is focused) or an **automation verb** (doc 14) which the user may bind to
a chord in Settings. A global-hotkey API is a keylogger with extra steps and there is no version of
it that is safe to give a sandboxed application.

## Screenshots and recording

The brief's § 23, and it is a shell feature with a compositor mechanism.

- ⌘⇧3 whole screen, ⌘⇧4 selection (with window snapping and a space-to-pick-window mode), ⌘⇧5 the
  control bar: screen or window or region, recording with microphone and/or system audio, cursor
  on/off, a countdown, and a save location.
- Capture is the compositor's `wlr-screencopy` for stills and a PipeWire stream for recording, so the
  same source serves the ScreenCast portal that doc 13's applications need.
- After a capture, a **thumbnail in the corner** with annotation, copy, share and drag-out. This is
  the affordance that makes the feature feel finished, and dragging that thumbnail into an
  application is a real drag-and-drop source — which needs the shell to be a drag origin, and is the
  reason drag-and-drop is listed as shell work rather than assumed.
- OCR of a capture: post-1.0, and it needs an OCR engine that is not in the base. Named so it is not
  forgotten.

⚠ **Nothing may capture the screen without the user's action or an explicit, revocable grant.** The
ScreenCast portal shows the picker; an application never enumerates outputs. The shell shows a
persistent indicator whenever a capture stream is live, and the indicator is drawn by the compositor,
not the shell, so a hung shell cannot hide it.

## Supervision, session, and lock

- The session is `graphical.target` → compositor → shell, as user units. The compositor supervises
  the shell; systemd supervises the compositor; a compositor that fails at boot drops to a text
  console with the log, which doc 10's rollback also watches.
- Lock is the compositor's, not the shell's: `ext-session-lock-v1` guarantees that a crashed lock UI
  leaves the screen locked rather than exposed. The shell draws the lock UI *inside* that guarantee.
- Restarting the shell preserves clipboard and notification history by handing them to the compositor
  across the restart — a small, versioned state blob, and the only state that survives, deliberately.

## Effort

| Piece | EM |
|---|---|
| Window management: placement, rules, remembering, focus, ⌘Tab | 2.5 |
| Snapping, split view, and the fill-the-rest chooser | 1.5 |
| Workspaces, overview, Exposé, and the animator | 3.5 |
| Displays: fractional scale, multi-monitor, hotplug, night light | 2.5 |
| `trinix-shell` host, layer-shell client, supervision, session/lock | 2.0 |
| Panel + menu bar rendering + status items | 1.5 |
| Dock | 1.5 |
| Notification centre and popups (UI only; doc 02 has the service) | 1.5 |
| Control centre | 1.5 |
| Desktop, wallpaper | 0.5 |
| Screenshots and recording, incl. the annotation UI | 2.0 |
| Global shortcuts, Command-key mapping, gestures, hot corners | 1.0 |
| **Total** | **21.5** |
