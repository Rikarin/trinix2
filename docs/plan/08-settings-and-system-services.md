# 08 — Settings and System Services

One Settings application, and the services it is a face for. The brief is right that fragmentation
here is where Linux desktops feel least like an operating system, and the fix is not a bigger control
panel — it is that **every setting is a key in one store (doc 02) with one schema, and Settings is a
generated-ish view over it**.

## The application

| Decision | Reason |
|---|---|
| **One process, one window, one search field** | Settings that spawns a second Settings — `nm-connection-editor`, `pavucontrol`, `blueman` — is the exact failure mode. Where an underlying daemon has no adequate API, we write the pane against the daemon rather than shipping its GUI |
| Every pane is a plugin **inside the application**, not a separate binary | It gives us the sidebar, the search, the deep-linkable URL (`trinix-settings://network/wifi`) and one back-stack for free |
| Every control is bound to a **schema'd key**, not to bespoke code | A pane is then mostly declaration; the search index over settings (doc 06) is generated from the schemas rather than hand-listed; and "reset this pane" and "what did I change?" both become possible |
| Panes that need privilege call `trinixd`, which re-authenticates | No Settings pane runs as root, and no pane holds an authorisation for longer than the operation |
| Third-party panes: **no** | An application's preferences live in the application, per the mac model. A system whose Settings can be extended by an installed application has a Settings that can be lied to |

**Sections**, in order, matching the brief with three merges: Appearance · Desktop & Dock · Displays ·
Sound · Network · Bluetooth · Keyboard · Mouse & Trackpad · Printers & Scanners · Notifications &
Focus · Privacy & Security (merged: they are the same conversation) · Applications (defaults, login
items, permissions per app) · Users · Storage · Battery & Power · Accessibility · Time & Language ·
Software Update · Developer · About.

`Search`, `Automation` and `Backup` are not panes — they are applications, because each is a place
you *do* something rather than configure something.

## Network

The base image has systemd and iproute2 and no network management at all today.

| Choice | Reason |
|---|---|
| **`systemd-networkd` + `iwd`**, not NetworkManager | networkd is already in the base image — zero new recipes for wired, and it is the one whose configuration is declarative files rather than a daemon-owned database. `iwd` is small, has no `wpa_supplicant` dependency, is D-Bus-native and does its own key handling. NetworkManager brings a large surface, its own connection-editor culture, and a plugin ecosystem we would spend years not shipping. ⚠ The cost is real: `iwd` is one new recipe, and enterprise 802.1X profiles are less turnkey than NetworkManager's |
| Wi-Fi credentials in the keychain | Doc 05. Not in a config file readable by whatever runs as the network group |
| VPN: **WireGuard in-kernel**, and OpenVPN/IPsec through a plugin interface | WireGuard is a kernel config and a key — a first-class pane with no daemon. The others are legacy and get a smaller, uglier path, honestly labelled |
| DNS: `systemd-resolved`, DoT/DoH available and off by default | Off by default because a DNS that silently routes to a third party is a privacy decision the user should make. The pane explains both sides in two sentences |
| Firewall: `nftables`, with a **default-deny inbound** and a per-application prompt on first listen | Which is only meaningful because doc 04's `network.server` exists. Without the sandbox this would be a checkbox |
| Hotspot, proxy, Bluetooth PAN | Panes over the same services. Hotspot is `iwd` AP mode plus networkd |

The C# API doc 26 of the brief asks for is `Network.Interfaces`, `Network.Wifi`, `Network.Vpn`,
`Network.Dns`, in `Trinix.Sdk.Services` — a façade in `trinixd` per doc 02, so no application ever
talks to networkd or iwd.

## Sound

**PipeWire + WirePlumber**, and ⚠ **they are not in the base image today** — Phase 4 planned them and
the recipe list does not have them. That is a prerequisite for this pane, for doc 03's screen
recording, for per-app volume and for doc 11's media applications, and it is on the critical path
for more things than its size suggests.

⚠ **It is also more than twice the size this document first said**, established 2026-08-25 by reading
upstream's `meson.build` and inventorying the build container rather than by estimating. Current
stable is PipeWire **1.6.8** and WirePlumber **0.5.15** — note 1.5.x is a *development* series and
WirePlumber 0.6 does not exist. Seven recipes are missing, not two:

| Missing | Cost |
|---|---|
| `alsa-lib`, `pcre2`, `lua` | Cheap. ⚠ `pcre2` is **already pinned in `sources.json` with no recipe**; Lua ships no shared library, no install layout and **no `.pc` file**, and WirePlumber finds it through pkg-config, so all three get hand-written |
| **`glib`** (target) | **The expensive one, and it arrives in a from-scratch base image solely because the session manager is written against it** |
| **`glib-host`** (`RECIPE_HOST_ONLY`) | Not optional: meson's gnome module resolves `glib-mkenums` and friends as *build-machine* tools, and the container has none of them. `wayland-scanner` and `expat-host` are the precedent for not solving this with an apt package |
| `pipewire`, `wireplumber` | Moderate; ~25 meson options each to answer explicitly |

Four traps, each of which produces a build that works and is wrong:

1. **Both projects fetch from the network by default.** PipeWire's `-Dsession-managers` defaults to cloning WirePlumber **master, unpinned**, as a subproject; WirePlumber's `system-lua` defaults to false and fetches Lua at configure time. `-Dsession-managers=[]` and `-Dsystem-lua=true`. Both fail loudly in a network-less container, which is the good kind.
2. **GLib reads seven cross properties through `meson.get_external_property()` *with fallbacks*.** `have_c99_vsnprintf`, `have_c99_snprintf` and `have_unix98_printf` all default **false**, so an undeclared cross build silently compiles gnulib's printf instead of glibc's. Declare them — following `mesa`'s `llvm-cross.ini` precedent, in a glib-specific cross file rather than the shared one.
3. **There is no `systemd --user`.** `-Dpam=disabled` means `pam_systemd` never registers a session and `user@1000.service` never starts, so upstream's user units are inert. The answer is Trinix-owned system units with a hardcoded `XDG_RUNTIME_DIR`, exactly as `trinix-compositor.service` already does — integration work, not recipe work.
4. ⚠ **The kernel cannot do sound and the VM has no audio device.** `base/recipes/linux/config/trinix.config` contains **no `CONFIG_SND` at all** — grep for `SND`, `SOUND` or `AUDIO` returns nothing — and `run-qemu.sh` passes no audio device. So a perfect recipe would land on a machine with nothing to talk to and nothing to test against.

⚠ **Screen recording with audio is not budgeted anywhere.** Doc 03 costs the capture UI; the pipe from
wlroots' screencopy into PipeWire, plus `xdg-desktop-portal`, is **≥1.0 EM** on top and belongs to
whoever schedules doc 03's recording line.

⚠ **This takes the base from 57 recipes to 63**, against `base/recipes/README.md`'s stated ceiling of
~40–60 and its rule that unusual machinery is a signal to ask whether something belongs in the base
image or in an app bundle. It probably is the right call — the alternative to GLib is writing a
session manager — but it is a budget decision and should be taken as one rather than absorbed.

Devices in and out, per-application volume (free: each sandboxed app already has its own PipeWire
node, doc 04), input level and monitoring, sample rate, and Bluetooth audio codec selection. Spatial
audio is post-1.0 and needs a real reason beyond the brief listing it.

## Displays

Doc 03 owns the compositor half. The pane is arrangement by dragging, resolution, refresh, scale
(with a preview of what each scale means in effective resolution — the mac affordance that makes
fractional scaling comprehensible), rotation, night light schedule, and per-display colour profile.
⚠ Colour management is a stub in 1.0: an ICC profile can be assigned and is applied as a LUT in the
compositor's output state, and nothing is calibrated. Saying "colour managed" for anything more would
be false.

## Power

Battery level and health, time remaining, per-application energy impact (from the cgroup accounting
doc 04 already produces), sleep and display timeouts on battery and on mains, Low Power Mode, and
wake-on-lid/lid-close behaviour. Backed by logind and the kernel directly, in `trinixd` — not
`upower`, per doc 02's reasoning.

**Low Power Mode** is a state doc 14 can trigger, and it does four things: caps the CPU governor,
lowers the display, tells the compositor to halve the refresh where the panel supports it, and pauses
background indexing (doc 06) and backup (doc 10). Every one of those is a service that already
exposes the knob, which is why it is a paragraph and not a project.

## Storage

Capacity by category, the "other" bucket named honestly rather than left mysterious, per-application
usage from the containers, cache reclamation, and snapshot space (doc 10). Disk Utility is a separate
application (doc 11) because partitioning is an operation and not a setting.

## Printing

⚠ **CUPS is not in the base image and printing is not free.** It is CUPS plus `cups-filters` plus a
driver story that in 2026 is mostly IPP Everywhere and driverless discovery — which is the good news:
a modern network printer needs no driver, only mDNS discovery (`avahi` or systemd-resolved's mDNS)
and IPP. 1.0 ships **driverless IPP only**, with a pane that finds printers and prints, and says
plainly that a printer needing a vendor driver is not supported. Adding the full driver ecosystem
later is additive.

Scanning is cut from 1.0 entirely; SANE is a larger and less rewarding surface than printing.

## Time, language, region

NTP via `systemd-timesyncd`, timezone by geolocation (with the network-access consequence stated) or
by hand, and the locale/keyboard-layout pair.

⚠️ **This section drew the globalization boundary in the wrong place, and then nothing implemented
even the wrong one.** Corrected 2026-08-25 by [20](20-localisation.md); both halves are worth stating.

*Unimplemented:* `InvariantGlobalization=true` is set in
[`Directory.Build.props`](../../src/Directory.Build.props), which **every** project inherits, and
[`pack-apps.sh`](../../src/pack-apps.sh) passes `-p:InvariantGlobalization=true` again on the
application publish line. So "applications ICU" was true nowhere, and a system that is invariant
everywhere cannot sort a list of files correctly in Czech.

*Wrong place:* the split is not services versus applications — it is **machine-facing versus
human-facing text, and both live in the same process**. `BundleScannerTests.OrdersOrdinallyRatherThanByCulture`
already carries the correct instinct: a signed manifest's file order must be **ordinal**, because a
culture-sensitive sort would make a signature depend on a locale; Files' list view must be **ICU**,
because it is read by a person. Doc 20 § owns the rule and this section defers to it.

⚠ And a trap in the ICU recipe itself: .NET `dlopen`s ICU **by soname**, so a version bump makes the
runtime fall back to invariant **silently** — correct-looking software that sorts wrongly. A startup
assertion is required, not optional.

## Applications pane

Default application per content type and per URL scheme · login items (which are
`system.background`-permissioned, doc 04) · per-application permissions with their access log ·
per-application storage · uninstall. This is one of the two panes the brief did not ask for by name
and that a user will open constantly.

## Effort

| Piece | EM |
|---|---|
| Settings shell: sidebar, search, deep links, pane host, schema binding | 2.0 |
| Network: `iwd` recipe, networkd/iwd façade, Wi-Fi/Ethernet/VPN/DNS/firewall panes | 3.0 |
| Sound: PipeWire + WirePlumber — recipes, façade, pane. **Measured 2026-08-25 as ~3.5**, see § Sound | 3.5 |
| Displays, power, storage panes (over doc 03 / `trinixd`) | 1.5 |
| Bluetooth: BlueZ recipe + façade + pane | 1.5 |
| Keyboard (incl. the Command-key mapping UI), mouse, trackpad | 1.0 |
| Printing: CUPS recipe, driverless discovery, pane, the print dialog in the SDK | 2.0 |
| Users, Privacy & Security, Applications, Time & Language, About, Developer | 2.0 |
| **Total** | **16.5** |
