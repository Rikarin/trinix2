# 19 — Menus Belong to Applications

**✅ Carried out.** All four places below have changed: `trinix-menu-v1` splits `get_menu_bar(id)`
from `get_toplevel_menu_bar(id, toplevel)`, the wlroots shim tracks a menu on the `wl_client` with a
second list for overrides, the compositor resolves on focus change and looks accelerators up through
the same resolution, and `TrinixMenu.ForApplication()` is the entry point with `ForWindow` as the
override. [The contract](../vixen-platform-contract.md) § 5 is now written against what the protocol
does, and this document is kept for the reasoning.

⚠️ **Amends [the Vixen ⇄ Trinix contract](../vixen-platform-contract.md) § 5, the
`trinix-menu-v1` protocol, and [02](02-services-architecture.md) § The transport decision.**

The contract's § 5 reaches the right conclusion — the menu bar is the screen's, applications
describe menus rather than drawing them, and the model rides the Wayland connection rather than
D-Bus — from a premise that is false:

> A menu belongs to a *surface*, though, and D-Bus has no notion of one.

A menu does not belong to a surface. It belongs to an **application**, and the protocol took the
sentence literally: `trinix_menu_manager_v1.get_menu_bar` takes an `xdg_toplevel`, with
`already_has_menu` as a per-toplevel error. That scoping is carried through
[`MenuBar.cs`](../../src/Trinix.Compositor/MenuBar.cs), which describes itself as "the menu model **a
window** exported", and [`TrinixMenu.cs`](../../src/Trinix.Platform/TrinixMenu.cs), whose instances
are keyed on a window handle.

## What macOS does, since that is what is being imitated

The menu bar is `NSApplication.mainMenu` — **a property of the application object**. Switching
between two windows of one application with ⌘\` does not change the menu bar. Only switching
*applications* does.

Window state does reach the menu, but only item *state*: enablement and check marks resolve through
the responder chain, starting at the key window's first responder (`validateMenuItem:`). So the
focused window decides whether **Save** is greyed, never whether the **File** menu exists.

The bar itself — its position, the Apple menu at the left, the status items at the right — is system
chrome, supplied by nobody's document.

That splits the original question into two, and the contract answered the first one correctly and
the second one by accident:

| Question | macOS | Trinix as written | Trinix as amended |
|---|---|---|---|
| Who owns the bar? | the system | the shell and compositor | unchanged |
| What scopes its contents? | the application | **the window** | the application |

## The premise is wrong; the conclusion survives intact

The anti-D-Bus argument in § 5 is worth keeping and does not depend on the surface. What it actually
needs is:

> The menu must ride something whose lifetime and focus the compositor already tracks.

A **`wl_client`** — the connection itself — satisfies that completely. The menu still dies with its
client, still arrives in order with the rest of that client's state, still cannot outlive its owner,
and still costs no second IPC mechanism and no second lifetime to reconcile. Every sentence in § 5's
"Why this is not D-Bus" holds word for word with *surface* replaced by *connection*. Unity's and
KDE's mistake was putting the model on the bus, not choosing the wrong Wayland object.

## What per-toplevel costs

Three consequences, in ascending order of seriousness.

1. **Menu churn on ⌘\`.** Switching between windows of one application changes the active toplevel,
   so the compositor swaps menu models where macOS does nothing at all. Even when the two trees are
   identical, that is a rebuild the reference behaviour does not have.

2. **N windows, N identical menus.** A ten-window editor exports the same tree ten times and must
   apply every state change ten times. This is not hypothetical — `TrinixMenu` keys its instances on
   a window handle, so an application with several windows will do exactly this, and the toolkit
   binding that hides it will be a synchronisation bug waiting to happen.

3. **An application with no windows cannot have a menu bar.** This is the decisive one. On macOS,
   activating an application whose windows are all closed still shows its menu bar, and **File ▸ New
   is how the user gets a window back**. `get_menu_bar(id, toplevel)` cannot express that state:
   there is no toplevel to create the menu from. The protocol as written makes a normal, everyday
   macOS interaction structurally impossible, and no amount of shell policy can recover it.

## ✅ Observed, 2026-08-25

Booted arm64 in QEMU, one client with two windows, focus driven from the serial console:

```
menu bar for an application: 6 items [File, View]
window mapped 'Trinix Wayland demo' 640x480 at 48,48
menu bar shown: 6 items [File, View]
window mapped 'Trinix Wayland demo window 2' 640x480 at 84,84
```

Both properties this document exists to produce are **observed rather than reasoned**: two windows
yield **one** `menu bar for an application:` line, and switching focus between them changes nothing —
no second `menu bar shown:` followed the map of window 2, and three further focus presses added zero
lines. The pre-change format was `menu bar for 'Trinix Wayland demo'`, window-titled; it is now
application-scoped.

⚠ **The control is what makes the silence mean anything.** A second, separate client produced its own
`menu bar for an application:` line, and three focus presses across the two clients produced **exactly
two** new `menu bar shown:` lines. That proves the key injection reaches the compositor, so the null
above is a real null rather than undelivered input — and two changes from three presses is one press
that moved focus without moving the bar.

Still reasoned, not observed: *which* press was the intra-client one. The counts are decisive; the
per-press attribution comes from reading `CycleFocus`/`Raise`.

## The amendment

```
trinix_menu_manager_v1:
  get_menu_bar(id)                          the client's menu bar
  get_toplevel_menu_bar(id, toplevel)       an override for one window
```

**Resolution, in the compositor:** the focused toplevel's override if it has one, otherwise that
toplevel's client's bar, otherwise the shell's own. Three lines, evaluated on focus change.

| Decision | Reason |
|---|---|
| The default unit is the **`wl_client`** | It is the closest thing Wayland has to an application, it is what the compositor already has for every surface, and it is what preserves § 5's lifetime argument unchanged |
| A per-toplevel override exists, and is expected to be rare | macOS handles the genuinely-different-window case by swapping `mainMenu` on activation, which is app-side policy over an app-scoped menu. The override is the same capability without requiring the application to observe its own focus changes |
| The override is a **separate request**, not an optional argument | So that `already_has_menu` stays meaningful on both, and so that an application that never wants one never mentions toplevels |
| The bar shown when nothing is focused is **the shell's** | macOS shows Finder's. Per-client scoping is what makes this expressible at all: the shell is a client like any other, and its menu is its own — no special case in the compositor |
| ⚠ Item **state** stays the application's job | Trinix has no responder chain, and inventing one to make greying automatic would be inventing a UI framework in a compositor. An application greys **Save** by calling `update`, which it must already do on every state change. This is a real difference from macOS and it is the right side to be on |

### What does not change

The atomic `commit` discipline, the client-assigned id model, `insert`/`update`/`remove`, lazy
population through `about_to_show`, accelerators being registered with and consumed by the compositor
([the contract](../vixen-platform-contract.md) § 2), the rejection of D-Bus, and the position that a
`com.canonical.dbusmenu` bridge sits *over* this protocol rather than beside it. The menu model — the
bulk of the XML — is untouched. This is a change to what a menu is attached to, and to nothing else.

## Doing it

Four places, and the protocol is version 1 and self-declared unstable with `HelloUi` as the only
client that has ever exported a menu, so this is close to free **now** and is not later.

| Where | Change |
|---|---|
| [`trinix-menu-v1.xml`](../../base/recipes/trinix-protocols/protocol/trinix-menu-v1.xml) | Split the two requests; rewrite the § describing why the connection rather than the surface; keep the error enum |
| `libtrinix-wlr` | Track the menu on the `wl_client` resource; a second map for overrides |
| [`MenuBar.cs`](../../src/Trinix.Compositor/MenuBar.cs), `WindowManager` | The model is per client, resolution on focus change; accelerator lookup follows resolution |
| [`TrinixMenu.cs`](../../src/Trinix.Platform/TrinixMenu.cs) | `TrinixMenu.ForApplication()` as the ordinary entry point; the window-keyed one becomes the override and stops being the default |

⚠ **Keep interface version at 1 rather than bumping to 2.** A version bump means clients rebuild, and
the audience for that courtesy is one application in this repository. Nothing has shipped; a v2 that
exists to be polite to nobody is a v2 that will be maintained forever.

**Effort: 0.5 EM**, and it comes out of [01](01-system-sdk.md)'s `Menu.Standard` line rather than
adding to the plan's total — the SDK's menu API had to be written against something, and it is better
that it is written against this.

## Two things this opens

- **Who supplies the application's name?** macOS's first menu is bold and carries the application's
  name, About, Preferences and Quit. With a client-scoped menu the compositor knows the client but not
  what it is called. The candidates are `xdg_toplevel.set_app_id` (present, a string, unverified) and
  the bundle identity the launcher already established at exec (verified, and the one
  [04](04-sandbox-and-permissions.md) uses for everything else). It should be the second, which means
  the compositor needs to learn the identity of a connecting client — a small addition to
  `trinix-shell-v1`, and the same fact [03](03-shell-and-window-management.md) § Panel wants for status
  items and [02](02-services-architecture.md) wants for attributing a notification.
- **The application menu's standard items are the system's, not the application's.** About, Preferences,
  Services, Hide, Quit are the same five in every macOS application and they are supplied by the
  framework. `Menu.Standard` in [01](01-system-sdk.md) already generates them; what this document adds
  is that the compositor may safely assume they are there, because the menu is now per-application and
  an application is exactly the thing that has them.
