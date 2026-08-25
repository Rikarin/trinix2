# 12 — Terminal and the Shell Language

Trinix's interactive shell is PowerShell — decided in `IMPLEMENTATION_PLAN.md` Phase 3, built, and
already the login shell. This document is the terminal it runs in, and what "PowerShell as the
distro's admin surface" has to mean to be more than a slogan.

## The terminal emulator

A `Trinix.Sdk` application, and the first one built (doc 11 § Order), because it exercises the SDK
with almost no widget surface.

| Feature | Note |
|---|---|
| Tabs and splits | Both, with the same keyboard model as every other Trinix application |
| Profiles | Command, environment, theme, font, working directory. A profile is a settings-store object |
| Rendering | Vixen's text stack: HarfBuzz shaping, the MSDF atlas, ligatures, true colour, full Unicode including wide and combining characters. ⚠ "GPU rendering" from the brief is not available and not needed — there is no GPU (doc 03 § Displays), and a terminal that can draw 100 000 glyphs per frame on a CPU rasteriser is a solved problem given a glyph atlas, which Vixen has |
| Search, and scrollback | With a bounded buffer and a "save to file" |
| Clickable URLs and paths | And OSC 8 hyperlinks, and a path that opens in Files |
| Shell integration | OSC 133 marks for prompt/command/output, which buys jump-to-previous-command, per-command exit status in the gutter, and "select this command's output". The PowerShell profile emits them |
| SSH and SFTP | ⚠ Not built into the terminal. `ssh` is a command; a terminal with an SSH connection manager is two applications. The **SFTP** half is Files' (doc 07 § Volumes), where it belongs |
| The rest | Themes from the design tokens, configurable shortcuts, a bell that is a notification and not a beep |

The one decision worth arguing: the terminal is **not** a `Vixen` viewport rendering a cell grid by
hand — it is Vixen's text layout over a cell model, so that font fallback, combining marks and bidi
behave the same in the terminal as everywhere else. Terminals that reimplement text are terminals with
their own emoji bugs.

## PowerShell as the administration surface

[`Trinix.Management`](../../src/Trinix.Management/) exists and has cmdlets for system status, services
and network interfaces. The commitment is that **every administrative operation Trinix supports has a
cmdlet, and every Settings pane is a face over the same C# library the cmdlet uses.**

That is a testable claim, and doc 16 tests it: a Settings pane that calls something no cmdlet can
reach is a gap in the administration surface, and it is caught by comparing the service contract's
surface with the module's exported verbs.

```powershell
Get-TrinixSystem                          # exists
Get-TrinixService | Where-Object Failed    # exists
Get-TrinixNetworkInterface                 # exists

Install-TrinixApp io.example.notes         # doc 09
Get-TrinixUpdate | Install-TrinixUpdate    # doc 10
Get-TrinixSnapshot | Restore-TrinixItem    # doc 10
Get-TrinixPermission -App io.example.notes # doc 04
Get-TrinixSecret -Service github.com       # doc 05 — metadata only; the secret needs a prompt
Get-TrinixDisplay | Set-TrinixDisplay -Scale 2
Invoke-TrinixVerb notes.new -Body "…"      # doc 14
```

The brief's sketch — `process list`, `service restart ssh`, `package install git` — is a different
shell language, and it is the one thing in the brief this plan declines outright. PowerShell already
has a verb-noun grammar, an object pipeline, tab completion driven by that grammar, and a help system;
inventing a second grammar on top means either a worse PowerShell or two things to learn. What is
worth taking from the sketch is **terseness**, and that is what aliases are for.

## Objects, and where the value actually is

The reason PowerShell is the right choice for this OS is not syntax, it is that
`Get-TrinixApp | Where-Object { $_.Permissions -contains 'devices.camera' }` is a real query over real
objects with no text parsing. Two consequences that have to be designed for rather than hoped for:

- **Cmdlets emit typed objects with formatting views**, not strings. A cmdlet that emits a formatted
  table has thrown the value away.
- **The service contracts (doc 02) are the object model.** The cmdlets return the same records the SDK
  returns, so a script and an application see one shape. This is why `Trinix.Management` must depend
  on `Trinix.Services.Contracts` and not define its own types — ⚠ it currently defines its own, which
  is fine for four cmdlets and will not be at forty.

## `/bin/sh` stays, and pwsh's start-up cost

Both already decided and both still true: `dash` is `/bin/sh` because thousands of scripts and
systemd generators require a POSIX shell, and pwsh's ~300–600 ms cold start is fine for a login and
not for a script. Two additions:

- ⚠ **The startup cost is worse than it looks in a terminal application**, because opening a tab
  starts a shell. The profile must stay small, the module must be loaded lazily through
  `Get-Module -ListAvailable` metadata rather than eagerly, and doc 16 measures tab-open latency as a
  gate. A terminal where a new tab takes 700 ms feels broken regardless of what it is doing.
- A **NativeAOT-trimmed pwsh** is the escape hatch named in the original plan's risk table. It is a
  real option and it costs compatibility with binary modules; it is not needed unless the measurement
  says so.

## Effort

| Piece | EM |
|---|---|
| Terminal: cell model over Vixen text, escape parsing, scrollback, selection | 2.0 |
| Tabs, splits, profiles, search, shell integration, links | 1.5 |
| PowerShell profile, prompt, OSC 133, lazy module loading | 0.5 |
| `Trinix.Management` growth onto the service contracts, and the cmdlet-coverage gate | 1.5 |
| **Total** | **5.5** |
