# 13 — Compatibility

The hardest part of replacing macOS, and the part where the brief's instinct — three tiers, and the
user should not care which — is right and expensive.

The brief also names two default applications, **Zen** and **VS Code**, and neither is a C#
application, neither is small, and both decide the shape of this document. Zen is Firefox; VS Code is
Electron. Both want a GTK stack that Trinix's base image does not have and should not acquire.

## The tiers

```
Native      .app bundle, Trinix.Sdk, sandboxed by doc 04            the good experience
Linux       an ordinary Linux desktop app, in one of two shapes     the catalogue
Windows     Wine/Proton over one of the above                       the long tail
```

## The Linux tier, and the decision that makes it affordable

A Linux desktop application needs GTK or Qt, and that is roughly forty libraries: glib, gobject,
pango, cairo, gdk-pixbuf, harfbuzz, freetype, fontconfig, atk, at-spi2, libepoxy, graphene, and so on
down. Putting them in the base image would nearly double the recipe count, put a large attack surface
inside an immutable signed image, and tie a GTK security fix to a whole-image update.

> **The Linux desktop stack is a runtime, delivered as a signed `.tdi` bundle, versioned, mounted
> read-only, and depended on by name.**

```
io.trinix.Runtime.Desktop/26.1     glib, gtk4, gtk3, pango, cairo, fontconfig, mesa, …
io.trinix.Runtime.Qt/6.9           Qt 6 and its dependencies
io.trinix.Runtime.Electron/38      the Chromium/Electron base VS Code and friends share
```

| Property | Consequence |
|---|---|
| It is a `.tdi` | Every mechanism it needs — signature, verification, mount, install, update, rollback — exists today (doc 09, [`app-bundles.md`](../app-bundles.md)) |
| An application declares `runtime` in `Info.json` | A one-line format addition, and the whole "dependency solver" is `look it up` |
| Several versions coexist | An application pinned to an old runtime keeps working; the runtime updates independently of the base image |
| It is outside the base image | The base stays near sixty recipes, and doc 00's third non-negotiable holds |
| ⚠ Building it is real work | Forty-odd cross-compiled recipes with their own patch tails. About **6 EM** for the first runtime and much less for the others, and it is the single largest line in this document |

**Why not Flatpak's runtimes directly?** Considered seriously. `org.freedesktop.Platform` and
`org.gnome.Platform` already exist, are built, are maintained by people who are good at it, and would
save most of that 6 EM. What they cost is a second delivery mechanism (OSTree), a second signature
scheme, a second update path and a second place a user's disk fills up. The compromise below takes
the catalogue without taking the mechanism into the parts of the system we own.

### Flatpak as a guest format

Trinix supports Flatpak, and **the word "Flatpak" does not appear in the interface**.

| Decision | Reason |
|---|---|
| Flatpak apps install through the Store and appear in the launcher as applications | The brief's rule: the user should not care where an application came from. Their origin is visible on the application's page in the Store, as "from Flathub", the same way a third-party repository is labelled |
| They run in **their own** sandbox (bwrap), not doc 04's | Their containment is already built, audited and depended on by their manifests. Two containment mechanisms is acceptable; two *permission models* is not |
| They reach **Trinix's** portals, keychain, notifications and settings | Doc 02's two-faces rule, and doc 04 § Attribution gives them an unattested-but-named identity. One consent UI, one privacy pane |
| Their permissions are shown in Trinix's vocabulary, mapped from the manifest | ⚠ The mapping is approximate — Flatpak's `--filesystem=home` is broader than `files.home` — and the Store says "this application's permissions are broader than Trinix can express" rather than pretending the mapping is exact |
| Updates go through `tpkg update` alongside everything else | One update button (doc 10) |

⚠ This is a genuine compromise and it should be recorded as one: it means two sandboxes, an OSTree
store on disk, and a second set of runtimes that duplicates ours. The alternative is a catalogue of
about twelve applications at launch, and that is not a machine anyone can use.

### XWayland

Required, not optional: Wine needs it, older applications need it, and a surprising amount of the
ecosystem still does. wlroots implements it; it is one recipe plus compositor policy for scaling
(⚠ X11 has no per-output scale, so an X client gets the primary display's scale and a downscale
elsewhere — the same fallback doc 03 § Displays defines, and it will look soft on a second monitor).

X11 clients get no `trinix_shell_v1` and no menu export, so they are undecorated by our extensions
and draw their own everything. That is the correct degradation.

## The Windows tier

Wine and Proton, in a runtime bundle of their own, launched through a small C# manager that owns the
prefix per application. A `.exe` opened in Files offers "Open with Wine", which creates a bundle-like
wrapper so the thing appears in the launcher afterwards.

⚠ 1.0 supports this and does not promise it works for any given application. There is no Wine
compatibility database, no per-application patching, no staff to run it. What is promised is that the
mechanism exists and is not a science project for the user.

## Gaming

The brief's § 28, and it is the tier where Trinix currently has the least to offer, because there is
no GPU driver: the image ships Mesa with **lavapipe only**, which rasterises on the CPU
([the platform contract](../vixen-platform-contract.md) § 1). Every gaming feature below is gated on
real hardware bring-up, which is doc 17's Phase 13.

Once it is: Mesa with the real drivers (amdgpu, iris, nouveau/NVK), Steam in a runtime bundle,
Proton, `gamescope` as a nested compositor (which is exactly the right shape here — it is a Wayland
client of our compositor and needs no compositor changes), MangoHud-style overlay, per-game profiles,
controller configuration, VRR and HDR in the compositor's output state, and a Game Mode that is
doc 08's power service with a different profile plus doc 06's indexer pausing.

Ordered honestly: **none of this is 1.0**, and the reason is one recipe away from being a different
answer — the day a real GPU driver works, most of the list is packaging.

## The browser

`Not writing a browser engine` is a non-negotiable (doc 00). What ships:

- **Zen**, packaged as an `.app` against the Desktop runtime. It is Firefox-based, which means Gecko,
  which means it does not need the Electron runtime, and it has native Wayland.
- ⚠ **Packaging somebody else's browser is not free and not finished when it launches.** It needs a
  security-update pipeline that tracks upstream within days, because a browser is the most exposed
  thing on the machine. That is an ongoing operational commitment, not a build step, and it is the
  strongest argument for the Flatpak path above — Flathub already does it.
- Trinix integration, in order of value: the keychain as its password store (via doc 05's Secret
  Service face — free), the portal file chooser (free), notifications (free), the system's default
  browser registration, and `Trinix.Sdk`'s theme colours passed through so it is not the one window
  that ignores the accent.
- Web applications: a `.tdi` generator that wraps a URL in a bundle with its own icon, container and
  profile. Cheap, and it is most of what "web apps" means.

**VS Code**, likewise, against the Electron runtime, with a Trinix keychain integration it already
supports (it calls the Secret Service), and a `Trinix.Sdk` extension providing the project templates
and `trinix doctor` as a task.

## Effort

| Piece | EM |
|---|---|
| `io.trinix.Runtime.Desktop`: ~40 recipes, cross-compiled, both arches | 6.0 |
| Qt and Electron runtimes (mostly reuse) | 2.0 |
| The `runtime` field, resolution, mounting, multi-version coexistence | 1.0 |
| Flatpak: recipe, Store integration, permission mapping, launcher unification | 2.5 |
| XWayland: recipe, compositor policy, scaling fallback | 1.0 |
| Wine/Proton runtime + the prefix manager + the `.exe` wrapper | 2.0 |
| Packaging and maintaining Zen and VS Code, plus the web-app wrapper | 1.5 |
| Gaming (post-hardware; listed, not counted in 1.0) | (4.0) |
| **Total (1.0)** | **16.0** |
