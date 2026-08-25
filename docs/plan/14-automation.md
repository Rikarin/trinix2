# 14 — Automation

The brief calls this a massive differentiator and it can be, on one condition: that automation is
built on the **same** interfaces applications already expose, rather than on a parallel scripting
surface that only automation uses. Shortcuts on macOS works because every application already had
AppleScript and then Intents; Automator's donation of a second, weaker model is why nobody used it.

## The one idea

> **A verb is declared once and reachable three ways: from Search, from an automation, and from
> PowerShell.**

An application declares its verbs in `Info.json` — the same declaration doc 06 § Actions uses for
search and doc 12 exposes as `Invoke-TrinixVerb`:

```json
"verbs": [
  { "name": "new",    "title": "New Note",
    "parameters": [ { "name": "body", "type": "text", "required": false } ] },
  { "name": "export", "title": "Export Note as PDF",
    "parameters": [ { "name": "note", "type": "reference" },
                    { "name": "to",   "type": "file", "mode": "write" } ],
    "returns": "file" }
]
```

| Decision | Reason |
|---|---|
| Verbs are **typed and declared**, not discovered by reflection | The declaration is signed with the bundle, the generator produces the handler's signature, and `trinix doctor` checks they agree. A reflection-discovered API is one that changes silently between versions |
| Parameters use a **small type vocabulary**: text, number, boolean, date, file, reference, enum, list | So a GUI can build an editor for any verb without the application supplying one, which is what makes third-party applications first-class in the editor on the day they ship |
| A `file` parameter is a **broker-mediated handle**, never a path | Doc 04. An automation passing a path would be a hole straight through the sandbox |
| Invoking a verb needs `system.automation`, and the *target* application's consent | Otherwise "control other applications" is a permission granted once and used for anything. The target app's consent is per-source-app, remembered, revocable in Privacy |
| The engine's own actions are verbs too | System verbs (notify, wait, run a shell command, HTTP request, transform text, branch) are declared by the engine in the same vocabulary, so there is no distinction between built-in and application actions |

## Shape of an automation

```
When:  a USB volume is mounted
       and its label matches "CAMERA*"
Do:    copy /Volumes/$volume/DCIM/**/*.{jpg,raw} → ~/Pictures/Import/$date
       import them into Photos
       eject the volume
       notify "Imported $count photos"
```

An automation is `(triggers[], conditions[], steps[])`, stored as one signed-by-nobody but
user-owned file in `~/Library/Automations/`. It is data — versionable, shareable, diffable — and it
is **not** a program: no loops with unbounded iteration, no arbitrary code, except through the
explicit `run a script` step which is itself a permission.

### Triggers

| Class | Examples |
|---|---|
| Device | Volume mounted/unmounted, display connected, Bluetooth device in range, power connected |
| System | Battery below N, thermal pressure, network changed, Wi-Fi joined a named network, screen locked/unlocked, login, logout, before sleep |
| Time | At a time, on a schedule, after an interval, at sunrise/sunset |
| File | A file appeared in a folder, a folder's contents changed (the "folder action") |
| Application | An app launched or quit; an app published an event |
| Manual | A menu item, a shortcut, a dock action, a Search result |

⚠ **Every trigger is backed by something that already exists** — udev/`trinixd`, logind, systemd
timers, inotify (shared with doc 06's watchers), the service bus. A trigger that would need a new
polling loop is a trigger we do not ship.

### The editor

A `Trinix.Sdk` application, and the visual editor is a **list**, not a node graph. The reason is
empirical: Shortcuts is a list and people use it; every node-graph automation tool is used by the
people who would have written a script anyway. Branches and loops are indented blocks in the list.

Every step's parameter editor is generated from the verb declaration. A step whose application is not
installed shows as a placeholder naming it, rather than being dropped — an automation shared between
machines has to survive that.

### PowerShell, which is the point

```powershell
Get-TrinixAutomation | Where-Object Enabled
Invoke-TrinixAutomation "Import photos"
Invoke-TrinixVerb notes.new -Body "from a script"
New-TrinixAutomation -From ./import.trinixauto
```

And the reverse: an automation's `run a script` step runs PowerShell with the objects, not strings,
of the preceding step. This is the thing a Mac cannot do well, and it is where Trinix's shell choice
pays off — the step's output is a typed object, the script receives it as one, and its output flows
into the next step still typed.

## Security

Automation is the most obviously dangerous feature in this plan, and it wants stating plainly:

- An automation runs as the user, in a sandbox whose permissions are the **union of what its steps
  need**, computed statically and shown before it is enabled.
- An automation that gains a step needing a new permission is **disabled until re-approved**. Same
  rule as doc 09's update permission-diff, same reason.
- `run a script` and `HTTP request` are each a permission on the automation, prompted at authoring
  time, and an imported automation containing either is flagged prominently.
- ⚠ **Importing an automation is installing software.** The UI treats it that way — a review sheet
  listing every step, every application it will control, and every permission, with the same weight as
  the Store's permission list. An "automation" file format that installs quietly is malware
  distribution with a friendly extension.

## Effort

| Piece | EM |
|---|---|
| Verb vocabulary, declaration, generator, dispatch, consent | 1.5 |
| Trigger sources over existing plumbing | 1.5 |
| Engine: conditions, steps, typed data flow, error handling, logging | 2.0 |
| System verbs (~30) | 1.0 |
| Editor application, generated parameter editors, import review | 2.5 |
| PowerShell integration both directions | 0.5 |
| **Total** | **9.0** |
