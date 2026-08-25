# 01 — The System SDK

The interface between an application and Trinix. It is the document that decides whether third-party
applications feel native, and it is mostly a document about restraint: the largest thing an
application needs — a UI framework — already exists and is not ours to write.

## The one-line summary

```
Vixen        draws.       Markup, styling, layout, text, controls, input, the frame.
Trinix.Sdk   connects.    Lifecycle, identity, the services, the design language, the bundle.
```

## What already exists, and must not be rebuilt

Vixen is a full application framework, consumed as [pinned packages](../../vendor/vixen/). Before
adding anything to `Trinix.Sdk`, check this table:

| The brief asks for | Vixen has | Where |
|---|---|---|
| Declarative UI | `.vxml` markup compiled by a Roslyn generator; `@if`/`@for`/`@switch`, slots, two-way `bind:`, `on:`/`change:` | `Vixen.Ui.Markup` |
| Reactivity | `Signal<T>`, `Computed<T>`, `Effect`, drained once per frame by the document | `Vixen.Ui.Reactive` |
| Styling and theming | `.vcss` with a real cascade, transitions, `@keyframes`, Oklab, plus a Tailwind-shaped utility generator over a token file | `Vixen.Ui.Styling`, `.Utilities` |
| Layout | Flexbox (Yoga algorithm), block, and the grid work in Vixen doc 43 | `Vixen.Ui.Layout` |
| Text | HarfBuzz shaping, MSDF atlas, bidi, line breaking, wrapping, decorations from the font's own metrics | `Vixen.Ui.Text` |
| Buttons, menus, toolbars, sidebars, lists, tabs, dialogs, text fields, sliders, toggles, context menus | ~40 controls on a real theme, 259 tests | `Vixen.Ui.Controls` |
| Tables, trees, docking, property grids, code editing, colour pickers, timelines | 11 advanced controls, 313 tests | `Vixen.Ui.Controls.Advanced` — ⚠ **not in [`vendor/vixen/`](../../vendor/vixen/)**, see below |
| Virtualisation | `VirtualizingPanel`, `VirtualizingGrid` | `Vixen.Ui.Controls` |
| Hot reload | State-preserving, file-watched | `Vixen.Ui.HotReload` |
| UI testing without a screen | A headless document and an assertion library | `Vixen.Ui.Testing` |
| Windows, input, clipboard, DPI | `IPlatform`, implemented for Trinix already | `Vixen.Platform`, `Trinix.Platform` |
| Images, audio, video decode | Imaging, `Vixen.Audio.Codecs`, `Vixen.Video.Codecs` | `Vixen.Core.Imaging`, `Vixen.Audio`, `Vixen.Video` |

⚠ **Two of these are not actually vendored.** Checked 2026-08-25: the 41-package pin in
[`vendor/vixen/`](../../vendor/vixen/) contains neither **`Vixen.Ui.Controls.Advanced`** nor
**`Vixen.Ui.Markup`**. So the rows above that read as settled are, today, unavailable — and three
documents lean on them: doc 07's Files needs `DataGrid`/`TreeView` for its list and column views, doc
11's System Monitor is a table, and both doc 07 § Glance and doc 11's Text Edit call `CodeEditor`
"free", which it is not until the package is pinned. Markup's absence is the larger one, since doc 01
says the shell and the real applications are `.vxml`.

Adding them to the pin is a `scripts/update-vixen.ps1` run rather than a project — but it is a
*decision* about closure size and trim surface that nobody has taken, and it must happen before
Phase 10 rather than being discovered by the first application that wants a list.

⚠ **This table is the SDK's scope control.** A proposal to add a control, a layout mode, a styling
feature or a text capability to `Trinix.Sdk` is a proposal to fork Vixen, and the answer is a pull
request against Vixen instead. The one exception is the *theme*, below, which is data rather than
code.

The corollary is that the Trinix ⇄ Vixen relationship must stay a package reference and never become
a source dependency. It is already a pin with a `scripts/update-vixen.ps1` that rewrites the version
and the vendored packages together; that stays.

## What the SDK is

Six assemblies, and the fifth and sixth are the interesting ones.

```
Trinix.Sdk                  TrinixApplication, the lifecycle, the bundle identity, paths
 ├── .Services              typed clients for every service in doc 02
 ├── .Theme                 the design language: tokens, .vcss, icons, fonts, motion  (data)
 ├── .Controls              the ~8 controls that are Trinix's rather than an application's
 ├── .Generators            Roslyn: service proxies, Info.json ⇄ code, permission checks
 └── .Testing               a fake service host, so an application's tests need no session
Trinix.Sdk.Tool             `trinix` — new, build, sign, package, install, run, doctor
```

`Trinix.Sdk.Controls` needs a hard boundary or it becomes a second widget library. It contains
exactly the controls whose *behaviour* is a Trinix policy and cannot be an application's choice:

`WindowFrame` (title bar, traffic lights, the `trinix_shell_v1` calls) · `MenuBarBuilder` ·
`Sidebar` (the mac-shaped source list with its specific selection behaviour) · `Toolbar` (unified
with the title bar, with the overflow rule) · `FileDropTarget` · `PermissionGate` (renders a control
disabled with the reason when the app lacks the authority, doc 04) · `Inspector` · `SearchField`
(the one that speaks to Beacon). Everything else is a Vixen control or the application's own.

## The application, before and after

Today, [`Trinix.Apps.HelloUi/Program.cs`](../../src/Trinix.Apps.HelloUi/Program.cs) is a Vixen
application with one Trinix line in it, and the comment in it is right that this is the point. What
it is missing is everything a *real* application needs: an identity that agrees with its bundle, a
menu bar, a single-instance rule, document handling, a settings store, and the services.

```csharp
// Trinix.Apps.Notes/Program.cs — the shape every application has.
using Trinix.Sdk;

return TrinixApplication.Run<NotesApp>(args);

[TrinixApp]                                   // generator reads Info.json; identity is not repeated here
sealed partial class NotesApp : Application {
    protected override Window CreateWindow(Launch launch) => new NotesWindow(launch.Documents);

    protected override Menu CreateMenu() => Menu.Standard(this)      // App/File/Edit/Window/Help
        .File(f => f.New(NewNote).Open<NoteDocument>().Separator().Item("Export…", Export))
        .Insert("Note", n => n.Item("Pin", Pin, Key.P | Key.Command));

    protected override async ValueTask StartAsync(CancellationToken ct) {
        Notifications.Requested += OnReminder;
        await Search.PublishAsync(new NoteIndexer(this), ct);        // doc 06, index contributor
    }
}
```

What the eleven lines buy, and what each replaces:

| Line | What the SDK did | Replaces |
|---|---|---|
| `TrinixApplication.Run<T>` | Built the platform (the `Platform = …` hook disappears), joined the session bus, registered for activation, installed the crash handler that files a local report and nothing more | ~40 lines of Vixen options per application |
| `[TrinixApp]` | Generated `Identifier`, `Version`, `Permissions` and the paths from the **bundle's own `Info.json`**, so identity is stated once and is the signed copy | Constants that drift from the manifest |
| `CreateWindow(Launch)` | Single instance by default; a second launch, a file opened from Files, or a `trinix://` URL arrives as a `Launch` on the running process | Every application inventing single-instance |
| `Menu.Standard` | The five menus every mac-shaped application has, with the right items in the right order and the right accelerators, exported over `trinix_menu_v1` | An application drawing its own menu bar, which doc 00 forbids |
| `Notifications`, `Search`, and the rest | Typed clients, resolved from the app object, already scoped to this app's identity and permissions | Direct D-Bus, direct file access, direct anything |

⚠ **`Menu.Standard` is not a convenience, it is a conformance mechanism.** The single most visible
difference between a coherent desktop and a collection of applications is whether ⌘W, ⌘Q, ⌘, and
Window ▸ Bring All to Front mean the same thing everywhere. Making the standard menu the *default*
and the empty menu the effortful choice is what makes conformance the path of least resistance.

## The service surface

Doc 02 owns the services themselves; this is the shape they take in an application. Every one is a
property on `Application`, every one is `async`, and every one throws a `PermissionDeniedException`
carrying the permission it wanted rather than returning null.

```csharp
Files          // open/save panels, bookmarks, the sandboxed reach into the user's data
Keychain       // await Keychain.SetAsync("github.com", "user", token);
Notifications  // post, update, withdraw, and the reply/action callbacks
Clipboard      // read, write, and the history the user is allowed to see
Settings       // this app's own preferences, typed, and the system ones it may read
Search         // publish an indexer, publish a live provider, or query
Network        // interfaces, reachability, and the request that waits for connectivity
Devices        // cameras, microphones, removable volumes — each behind a permission
Power          // battery, thermal, and the "please do not sleep" assertion with a reason
Automation     // publish this app's verbs so doc 14 can call them
System         // version, appearance, locale, accessibility settings, the appearance-changed event
```

Two rules about this list, both learned from watching other platforms get it wrong:

- **`await`, always, even where the implementation is local.** A synchronous `Keychain.Get` that
  works today because the store is a local file becomes a synchronous IPC call the day the store is
  behind a TPM, and by then a thousand call sites are on the UI thread. The asynchrony is part of
  the contract, not of the implementation.
- **No `IsPermissionGranted` boolean.** Asking is a permission-model smell: it teaches applications
  to branch on authority, which produces two code paths of which one is never tested. Applications
  *do*, and handle the refusal — or they wrap the control in a `PermissionGate`, which renders the
  disabled state and the explanation the same way in every application.

### How the proxies are generated

`Trinix.Sdk.Generators` takes the service interface — declared once, in `Trinix.Services.Contracts`,
which both the daemon and the SDK reference — and emits the client proxy and the server dispatcher.

| Choice | Reason |
|---|---|
| **A Roslyn source generator**, not reflection | Everything in Trinix builds with `IsAotCompatible` and the trim analyser on, per [`Directory.Build.props`](../../src/Directory.Build.props). A reflection-based bus binding fails that on the first `PublishAot`, and the failure appears in the compositor's build, not in the SDK's |
| **The C# interface is the source of truth**, the D-Bus XML is generated from it | The alternative — hand-written XML introspected into C# — makes the interface an artefact of the wire format. Doc 02's compat faces go the other way round, and are hand-written *because* their XML is somebody else's normative document |
| Both sides from one declaration | A service whose client and server can disagree will |
| `Tmds.DBus.Protocol` **0.95.0**, pinned exactly | The only maintained managed D-Bus implementation with no reflection on the message path, and **verified under NativeAOT on `linux-arm64`** — method, signal, and an `h` file descriptor in both directions including a live socket, on both the session and system buses, zero trim warnings, no `Requires*` anywhere, 2.96 MiB. Doc 18 R4 carries the evidence and is retired. ⚠ Pin *exactly*: seven releases in 2026 and a rename break at 0.93.0 |
| ⚠ **`Tmds.DBus` — the high-level package — is not the one, and will not tell you so** | It emits 14+ IL3050/IL2055/IL2060 warnings and then fails at `ConnectAsync` under AOT, and it carries **no `Requires*` annotation**, so `TreatWarningsAsErrors` in [`Directory.Build.props`](../../src/Directory.Build.props) is the only thing between a developer and that. Sub-trap: it contains its own internal `Tmds.DBus.Protocol` namespace, so a warning naming `Tmds.DBus.Protocol.MessageReader` means the **wrong package** is referenced — those types do not exist in the real one |
| The generator emits **non-`async` builder functions** for every method and signal | `MessageWriter` is a `ref struct` and therefore cannot cross an `await`. Hand-written, that is 10–20 lines per member forever; generated, it is a constraint on one code path in the generator and nobody else ever sees it. ⚠ This is the concrete reason doc 02's contracts must be generated rather than hand-rolled, and it was found by measurement rather than by design |

## The design language

The part of the SDK that is data, and the part that decides whether Trinix looks like one thing.

```
Trinix.Sdk.Theme/
  tokens.yaml          colour, spacing, radius, elevation, motion, type scale — the only source
  trinix.vcss          the control theme: Vixen's UserAgent origin, restyled
  trinix.utilities     the generated utility classes, from tokens.yaml
  Icons/               one icon set, one grid, one weight axis
  Fonts/               the UI face, the mono face, and the fallback chain
  Motion.cs            named curves and durations, so "a sheet appears" is one call
```

| Choice | Reason |
|---|---|
| One `tokens.yaml`, everything generated from it | Accent colour, light/dark and contrast settings are runtime knobs the user turns in Settings. If a colour is written literally in a stylesheet it will not follow the accent, and the bug appears months later in one control |
| Vixen's `ControlTheme` is **replaced**, not overridden | Vixen's theme is the engine's identity; overriding it leaves both in the binary and produces the specificity fights that make a theme unmaintainable. `Trinix.Sdk.Theme` registers as the `UserAgent` origin instead |
| Light and dark are the same stylesheet with different tokens | Two stylesheets diverge. Always |
| The icon set is drawn, not licensed | An icon set with an attribution requirement in an OS interface is a legal footnote in every screenshot. ⚠ This is real work — about 400 glyphs — and doc 17 budgets it separately from the code |
| **Reduced motion is a token, not a branch** | `Motion.cs` durations collapse to zero when the accessibility setting is on. If instead every animation site checks the setting, some will not, and those are the ones that make a user sick |
| Fractional scaling handled by the layout, not by scaling a bitmap | Vixen lays out in logical pixels and rasterises text at device resolution; there is no blur to work around, and doc 03 § Displays owns the per-output scale |

## Paths, and the mac-shaped surface

An application never builds a path. It asks:

```csharp
app.Paths.Data          // ~/Library/Containers/<id>/Data          — its own, backed up
app.Paths.Caches        // ~/Library/Containers/<id>/Caches        — its own, not backed up, reapable
app.Paths.Preferences   // via Settings; not a directory it writes to
app.Paths.Documents     // ~/Documents — only with files.home, and only through the broker
app.Bundle.Resources    // read-only, inside the signed bundle
```

| Choice | Reason |
|---|---|
| The user's home is **mac-shaped**: `~/Documents`, `~/Library`, `~/Applications`, and `/Applications`, `/Volumes`, `/System` at the root | The target user's muscle memory, and — more importantly — it gives Files a small, closed set of directories to present. A home directory with `.config`, `.local`, `.cache`, `snap`, and eleven dotfiles is a Linux artefact leaking into the interface |
| Underneath, the system is **FHS and unmodified** | glibc, systemd, PowerShell, .NET and every recipe in `base/` expect `/usr`, `/etc`, `/var`. Fighting that would mean patching everything forever. `/Applications` is a real directory; `~/Library` is a real directory; `/System` is a symlink shim, and `XDG_*` are set to point inside `~/Library` so third-party software lands where we want it |
| ⚠ The mac shape is **presentation plus a few real directories**, not a filesystem layer | There is no union mount and no path translation. An application that resolves `~/Documents` gets `/home/<user>/Documents`, which is what it is. Anything cleverer would break `realpath`, and every file dialog that ever showed a user a path they could not type into a terminal was doing something cleverer |

## Distribution, from a developer's side

`Trinix.Sdk.Tool` is the whole developer story, and it wraps the machinery Phase 6 already built —
[`Trinix.Bundle`](../../src/Trinix.Bundle/), `pack-apps.sh`, the developer PKI.

```bash
trinix new app --name Notes --id io.example.notes    # project, Info.json, .vxml, an icon, a test
trinix build                                          # dotnet publish, both RIDs
trinix sign --identity "Development: you"             # Merkle manifest, the existing sealer
trinix package                                        # Notes.tdi, EROFS + signed footer
trinix run                                            # install to ~/Applications and launch, with logs
trinix doctor                                         # the twelve things that are wrong with your bundle
```

`trinix doctor` matters more than it looks. It is where conformance stops being a document: it checks
that the permissions declared are the ones the code's service calls need (the generator knows both),
that the menu has the standard items, that every string is localisable, that no control is
unreachable by keyboard, that the icon exists at every size, that `minimumSystemVersion` is not a
guess, and that nothing in the bundle is writable. Doc 09 § Review makes a subset of it a gate on
Store submission, and doc 16 runs it over Trinix's own applications in CI, which is what keeps it
honest.

## Developer mode

A single switch in Settings ▸ Developer, off by default, and the plan's answer to the brief's § 29.
What it changes, and nothing else:

| On | Effect | Why it is gated |
|---|---|---|
| Unsigned bundles | Gatekeeper accepts a bundle signed by a locally generated development identity | Otherwise every developer's first experience is a refusal, and the workaround they find is worse than the switch |
| SSH server | `sshd` starts, key auth only, password auth not offered | A listening service on a personal machine is a decision, not a default |
| Debugging | `ptrace` of one's own processes, the .NET diagnostic pipe, and the sandbox's `debug` relaxation for a named application | `ptrace` across the sandbox is a total bypass of doc 04; it is scoped to one app id and logged |
| The console | Logs, tracing, live service inspection, the D-Bus monitor — a graphical `journalctl` plus what `busctl` shows | Reading the system's log is a privacy surface |
| Experimental APIs | SDK surfaces marked `[Experimental]` stop being errors | So that a preview API cannot accidentally become load-bearing in a shipped application |

⚠ Developer mode is **per-user and survives updates**, and it is recorded in the update's log. A
machine that boots with it on and cannot say when it was turned on is a machine somebody else turned
it on for.

## Effort

| Piece | EM |
|---|---|
| `Trinix.Sdk` core: lifecycle, identity, launch, paths, single instance | 1.5 |
| `.Generators`: service proxies, `[TrinixApp]`, permission cross-check | 1.5 |
| `.Services` clients (tracks doc 02's services; the cost is mostly there) | 1.0 |
| `.Theme`: tokens, `.vcss`, motion, fonts — **excluding icons** | 2.0 |
| Icon set, ~400 glyphs | 2.0 |
| `.Controls`: the eight | 1.5 |
| `.Testing` | 0.5 |
| `Trinix.Sdk.Tool` including `doctor` | 2.0 |
| Templates, and the SDK's own documentation | 1.0 |
| **Total** | **13.0** |

## Open

- **Does an application declare its menu in markup?** `Menu.Standard` above is fluent C#. A `.vxml`
  menu would be more consistent with the rest of the framework and would let the generator check
  accelerator collisions at compile time. It is also a new markup dialect. Deferred until three
  applications exist and their menus can be looked at.
- ~~**Localisation.**~~ ✅ Answered by [20](20-localisation.md): Trinix 1.0 ships in English, and
  every user-visible string goes into a catalogue from the day it is written anyway. The strings were
  never the expensive part — a concatenated sentence, a count formatted with no plural category, a
  `value.ToString()` no grep can find, and a stylesheet written in `pl-4` rather than `ps-4` are, and
  a compiler cannot point at half of them later. 3.0 EM in Phase 7, against ~8 EM to retrofit.
- **API stability.** Vixen gates its public surface with `PublicAPI.*.txt` and a `CheckApi` target.
  Trinix has no equivalent and the SDK is the assembly that most needs one. Doc 16.
