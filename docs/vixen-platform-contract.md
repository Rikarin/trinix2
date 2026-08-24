# The Vixen ⇄ Trinix contract

Everything a graphical application on Trinix depends on, and nothing else.

Vixen is developed separately from Trinix and neither is finished. That is the
situation this document exists for: it is the firewall named in the
implementation plan's risk table, so that the compositor and the applications
depend on a contract rather than on each other's internals. Both sides can move
as long as this does not.

The normative artifacts are the two protocol files, which ship in the image at
`/usr/share/trinix-protocols/`:

| File | What it covers |
|---|---|
| [`trinix-shell-v1.xml`](../base/recipes/trinix-protocols/protocol/trinix-shell-v1.xml) | Shadows, rounded corners, window dragging, resize edges, traffic-light hit zones |
| [`trinix-menu-v1.xml`](../base/recipes/trinix-protocols/protocol/trinix-menu-v1.xml) | The global menu bar |

Everything else in the contract is upstream Wayland, deliberately. Trinix
invents a protocol only where mac-like behaviour genuinely cannot be expressed
in one that already exists — which turned out to be two places.

## Shape of the split

> **Vixen draws. Trinix arranges.**

Vixen owns everything inside a window: layout, text, widgets, the title bar and
the glyphs in it. Trinix owns everything about a window: where it is, what is in
front of it, what has focus, what the shadow looks like, and what the pointer
was doing before it arrived.

The interesting cases are the ones that look like they belong to one side and
belong to the other. Dragging a window is the clearest: the title bar is
Vixen's pixels, but the drag is Trinix's, because a window that stops moving
while the application is busy is the single most recognisable difference
between a good desktop and a bad one. So Vixen declares the region and never
sees the motion events.

## What Vixen must implement

### 1. Surfaces and buffers

A Vixen window is a `wl_surface` with an `xdg_toplevel` role. Nothing exotic:
if it works on any wlroots compositor it works here.

| Global | Required | Notes |
|---|---|---|
| `wl_compositor` | yes | version 4 or later, for `damage_buffer` |
| `xdg_wm_base` | yes | version 3 or later |
| `wl_shm` | yes | the guaranteed buffer path |
| `wl_seat` | yes | keyboard, pointer |
| `wl_data_device_manager` | yes | clipboard and drag-and-drop |
| `trinix_shell_v1` | no | without it a window is undecorated but functional |
| `trinix_menu_manager_v1` | no | without it the application has no menus |

**Buffers, and an honest note about acceleration.** The contract specifies
`wl_shm` as the path that always works, and EGL or Vulkan as the accelerated
path. Trinix now ships Vulkan and still ships no acceleration, which sounds
like a contradiction and is not.

The image carries a Vulkan loader and exactly one driver: Mesa's **lavapipe**,
which rasterises on the CPU by compiling shaders with LLVM
([`base/recipes/mesa/recipe.sh`](../base/recipes/mesa/recipe.sh)). So
`VK_KHR_wayland_surface` works, a `VkSwapchainKHR` can be created against a
`wl_surface`, and a Vixen window renders. What lavapipe does *not* have is a
GPU or a DRM render node, so its Wayland swapchain takes Mesa's software path
and presents through — `wl_shm`. The buffer that reaches the compositor is the
same buffer the guaranteed path would have produced; Vulkan is how Vixen draws
into it, not how it is handed over.

The compositor is unchanged and still composites through pixman: wlroots' GL
renderer wants a render node that does not exist here, and the reasoning in
[`base/recipes/wlroots/recipe.sh`](../base/recipes/wlroots/recipe.sh) still
holds.

So the limit is performance, not capability, and Vixen should be written
against it rather than around it. An engine whose frame survives a software
rasteriser will run on Trinix now and run better later; one that assumes a
discrete GPU cannot be tested at all until there is one. When real hardware
arrives the path is `linux-dmabuf-v1` and a Vulkan driver that is not lavapipe,
and it is additive — no protocol in this document changes.

**Scaling.** Trinix targets 2× as the ordinary case. A window is described in
logical pixels and a buffer in device pixels, related by
`wl_surface.set_buffer_scale`. Vixen must handle a scale change on an existing
surface, because moving a window between displays is not a rare event.

### 2. Input

Straight `wl_seat`. The two rules that are Trinix's rather than Wayland's:

- **Keyboard focus follows the window, not the pointer.** Clicking raises and
  focuses; moving the pointer does not.
- **The compositor eats some keys.** Menu accelerators registered through
  `trinix_menu_v1.set_accelerator` are handled by the shell and never reach the
  client — which is the point of registering them. Anything not registered is
  the client's.

### 3. Clipboard and drag-and-drop

`wl_data_device_manager`, unmodified, with no Trinix extension — the upstream
protocol already does what a desktop needs, and a private one here would buy
nothing but incompatibility with every existing client.

Vixen should offer at minimum `text/plain;charset=utf-8`, and should prefer
specific MIME types over generic ones when both are on offer. Drag-and-drop
uses the same objects; the compositor provides the drag icon surface's
compositing and Vixen provides its pixels.

### 4. Decorations

Vixen draws the title bar, the traffic lights and the window's own frame. It
then tells the compositor three things through `trinix_shell_surface_v1`:

1. `set_drag_region` — the title bar minus the controls.
2. `set_control` for each of close, minimise, zoom — where the glyphs are.
3. `set_shadow` and `set_corner_radius` — what the window looks like from
   outside its own edges.

In return it receives `control_hover` and `control_activated`. Hover is
reported for the *group*: the traffic lights all light up when the pointer is
over any of them, and that behaviour is the compositor's because only the
compositor knows where the pointer is without waking the application.

`control_activated(close)` is a request, not an instruction. The window is not
destroyed; the application decides, exactly as with `xdg_toplevel.close`.

A window that never binds `trinix_shell_v1` still works. It gets no shadow and
its title bar is not draggable — which is the correct failure mode for a
protocol that is an enhancement.

### 5. Menus

An application does not draw menus on Trinix. It describes them once, updates
them as its state changes, and the shell renders whichever window's menus
belong to the active window, at the top of the screen.

The model is a tree of client-numbered items, mutated incrementally and applied
atomically by `commit` — the same discipline as surface state, and for the same
reason: a half-built menu must never be one the user can click.

```
insert(id=1, parent=0, index=-1, kind=submenu,  label="_File")
insert(id=2, parent=1, index=-1, kind=item,     label="_New")
set_accelerator(id=2, keysym=XKB_KEY_n, modifiers=logo)
insert(id=3, parent=1, index=-1, kind=separator, label="")
insert(id=4, parent=1, index=-1, kind=item,     label="_Close")
commit()
```

Lazy population is first-class: describe the bar, and fill each menu in when
`about_to_show` arrives. An application with an expensive menu should do that,
and the protocol is shaped so it costs nothing extra.

**Why this is not D-Bus.** Every previous global menu — Unity's, KDE's — put
the model on the bus. A menu belongs to a *surface*, though, and D-Bus has no
notion of one, so all of those designs ended up also needing a Wayland or X11
protocol whose entire job was to say which bus name went with which window.
That is two IPC mechanisms and two lifetimes for one feature, and it produces
the failure everyone has seen: menus that outlive their window. Carrying the
model on the Wayland connection makes the association structural. The cost is
defining a menu model, which is the bulk of `trinix-menu-v1.xml`, and it is
paid once.

Bridging toolkits that already speak `com.canonical.dbusmenu` is a translator
sitting *over* this protocol, and deliberately not a second path through it.

## What Trinix guarantees

- **The protocols above, versioned.** They are unstable and will change; a
  version bump means clients rebuild. Nothing is removed within a version.
- **A compositor that does not need the application to be responsive.**
  Dragging, resizing, raising, focusing, hover feedback on window controls and
  the entire menu bar work while a client is blocked. This is why so much of
  the protocol is declarative.
- **`XDG_RUNTIME_DIR` and `WAYLAND_DISPLAY`** in every graphical session's
  environment.
- **The XML in the image**, at `/usr/share/trinix-protocols/`, found through
  `pkg-config --variable=pkgdatadir trinix-protocols` — so an application can
  be built against Trinix on Trinix.

## What Trinix does not provide yet

Stated plainly, because a contract that quietly omits its gaps is worse than no
contract:

| Missing | Consequence for Vixen | Arrives with |
|---|---|---|
| Hardware acceleration (a GPU, EGL, dmabuf) | Vulkan works, on a CPU: lavapipe rasterises and presents through `wl_shm`, and the compositor composites in software | Real GPU support — Phase 8, or a VM tier that can supply virgl |
| Text shaping | Vixen brings its own, and a face is now installed at `/usr/share/fonts/truetype/dejavu` — but the system face is still a placeholder rather than a design decision | The rest of Phase 5 |
| A shell that draws the menu bar | Menus are transported and held, not yet displayed | The rest of Phase 5 |
| Minimise, and a dock to minimise into | `control_activated(minimise)` is delivered and the shell does nothing with it | The rest of Phase 5 |
| Fractional scaling | Integer scale factors only | When something needs it |
| Screen capture, portals, PipeWire | No screenshots, no screen sharing | Not scheduled |

One gap on this list has since closed: **how an application is packaged,
signed and installed** is now defined and implemented — see
[docs/app-bundles.md](app-bundles.md). A Vixen application is a `.app` bundle
whose `Contents/Bin` holds its executable, published as a signed `.tdi`. Nothing
about the format is Vixen-specific.

The first two are the ones that matter today: the menu bar protocol is
implemented end to end and the shell holds a complete menu model, but it cannot
draw the words in it until there is a font in the image.

## Versioning

Both protocols are `v1` and unstable, in the sense wayland-protocols means:
breaking changes bump the interface version and the old version is not kept.

That was defensible while the compositor and every client shipped together in
one image. Phase 6 changed it: applications are now signed `.tdi` distribution
images installed independently of the system
([docs/app-bundles.md](app-bundles.md)), so a client built against `v1` can meet
a compositor that has moved on.

Two things keep that honest for now, and neither is a permanent answer:

- `Info.json` carries `minimumSystemVersion`, checked against the image's
  `VERSION_ID`. It is coarse — it says "this needs at least that system", not
  "this needs `trinix_shell_v1`".
- A client that binds a global the compositor no longer advertises fails at
  startup rather than misbehaving, because these globals are bound by name and
  version at connect time.

The real answer is that a protocol an out-of-tree application depends on has to
stop being unstable, and both of these will freeze before there is a third-party
application to break. Until then, a version bump means rebuilding and re-signing
every bundle.
