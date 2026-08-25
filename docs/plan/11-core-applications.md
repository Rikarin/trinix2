# 11 — Core Applications

The brief's instinct is right and worth quoting as a principle: **ship a small but excellent set
rather than fifty mediocre applications**. This document decides which, in what order, at what cost —
and it cuts five of them.

## The rule that decides the list

> An application belongs in the system if **not having it makes the machine unusable**, or if it is
> the place a system capability becomes visible.

Files, Settings, Terminal and the Store are the first kind. System Monitor and Disk Utility are the
second — a machine has processes and disks whether or not you ship a window for them. Mail is neither:
a machine without Mail is a machine you read mail on the web with, which is what most people do.

## Ships in 1.0

| Application | Why it is here | EM |
|---|---|---|
| **Files** | Doc 07 | (15.5) |
| **Settings** | Doc 08 | (14.5) |
| **Terminal** | Doc 12 | (4.0) |
| **Store** | Doc 09 | (2.5) |
| **Search** | Doc 06 — the shell's, not a window | (9.0) |
| **Text Edit** | A machine that cannot open a `.txt` is broken. Vixen's `CodeEditor` gives syntax highlighting free; this is a window, a document model and find-and-replace, not an editor project | 1.5 |
| **Preview** | PDF and images: view, page, annotate, sign, rotate, export, and combine PDFs. Shares every renderer with Glance (doc 07), which is why it is cheap | 2.0 |
| **Calculator** | Basic, scientific, programmer, and unit conversion. Its expression engine is also doc 06's live provider — write it once | 1.0 |
| **Archive** | Zip/tar/7z. Mostly the same code as Files' in-place browsing; the window exists so double-clicking an archive does something | 0.5 |
| **System Monitor** | Below | 2.5 |
| **Disk Utility** | Below | 2.5 |
| **Screenshot** | Doc 03 — the capture UI and the annotation editor | (2.0) |
| **Rewind** | Doc 10 — the backup browser | (1.5) |
| **Console** | The log viewer. Developer Mode's window (doc 01), and the thing that turns "it broke" into a bug report | 1.0 |
| **Installer / Setup Assistant** | First boot: language, keyboard, network, account, disk encryption, backup. The first thing anyone sees | 2.0 |
| **Clock** | World clock, alarms, timers, stopwatch. Small, and its absence is conspicuous | 0.5 |
| **Notes** | The one productivity application that ships, because it is where you put a thing while doing something else, and because it is the SDK's proof that a third party can write a real application. Rich text, checklists, attachments, tags, search integration. ⚠ **No sync in 1.0** | 3.0 |
| **Music / Video** | One application, two modes: a local library with metadata and playlists, and a player with subtitles, tracks and speed. Over `Vixen.Audio.Codecs` and `Vixen.Video.Codecs`, which exist. ⚠ Hardware video decode does not, and neither does a GPU (doc 03 § Displays); 4K playback will not work until it does, and that must be said out loud rather than discovered | 3.0 |
| **Photos** | ⚠ **Viewer and organiser only**: albums, dates, search by metadata, basic adjustments, no library ingest from cameras, no faces, no RAW pipeline. This is the cut-down version of the brief's § 20, and the cut is what makes it 2 EM instead of 12 | 2.0 |

New application work in 1.0, excluding what other documents already count: **21.5 EM**.

## The System Monitor, specifically

The brief wants Activity Monitor rather than `htop`, and the difference is almost entirely about
**attribution**: `htop` shows processes, Activity Monitor shows *applications*. Trinix can do the
second properly and most Linux tools cannot, because doc 04 puts every application in its own cgroup:
CPU, memory, I/O and energy are read per-application from the cgroup, not summed by guessing at
process trees.

Tabs: CPU · Memory · Energy · Disk · Network · GPU. Per row: the application (with its icon and
identity), CPU %, memory, energy impact, disk and network rates. Actions: quit, force quit, suspend,
resume, reveal in Files, open files and ports, environment.

⚠ Two honesty notes. **Energy impact is a model, not a measurement** — a weighted sum of CPU time,
wakeups and I/O, calibrated once — and it should be labelled as relative rather than in watts.
**GPU and VRAM are unavailable** until there is a GPU; the tab is absent rather than showing zeros.

## Disk Utility, specifically

Drives, partitions, filesystems, mount points, SMART, format, erase, disk images, ISO mounting, LUKS.
It runs privileged operations through `trinixd` with re-authentication per operation.

⚠ **RAID and LVM are cut.** They are server features, they double the model's complexity, and a
personal machine that needs them has an administrator who will use the command line. Btrfs is
first-class because doc 10 depends on it; multi-device Btrfs is shown read-only.

## Cut from 1.0, with the reasoning

| Application | Why cut | What ships instead | Later cost |
|---|---|---|---|
| **Mail** | IMAP + SMTP + OAuth + threading + search + HTML rendering is a year on its own, and HTML rendering means an engine (doc 13 § Browser). It is also the application people are least likely to switch | The browser. `Trinix.Sdk.Mail` ships the IMAP/SMTP/OAuth libraries so the application is an application when it comes | 8 EM |
| **Calendar** | CalDAV plus recurrence plus timezones plus invitations. Recurrence alone is a notorious swamp | The browser. `Trinix.Sdk.Calendar` ships CalDAV and an RFC 5545 implementation, which doc 06 and doc 14 can already use for "what's next" | 5 EM |
| **Contacts** | Only interesting when Mail and Calendar exist | CardDAV in the SDK | 2 EM |
| **Web browser** | Doc 13. Not writing an engine is one of doc 00's non-negotiables | Zen, packaged | — |
| **Code editor** | Same argument, one level up: an IDE is not an OS feature | VS Code, packaged. `Trinix.Sdk` targets it with a project template | — |
| **Network Utility** | Six commands in a window, and PowerShell already has the cmdlets | `Test-TrinixNetwork` and friends in `Trinix.Management`, where [one already exists](../../src/Trinix.Management/GetTrinixNetworkInterfaceCommand.cs) | 0.5 EM |

⚠ **The cut list is not a promise-later list.** Each of these should be reconsidered against what a
real user of Trinix is missing after six months, and it is entirely possible that the answer is a
better Photos rather than a Mail.

## What every one of these must do

The reason the list is short is so that all of them can meet the same bar, and the bar is the reason
the system feels like one thing:

- Built on `Trinix.Sdk`, with the standard menu, the standard shortcuts, and no custom chrome.
- **Launch to a usable window in 400 ms** (doc 00), which for several of these decides NativeAOT.
- Full keyboard operability, and an accessibility tree that doc 15's checker passes.
- Every string localisable, no string concatenated from fragments.
- Sandboxed with the minimum permission set, and `trinix doctor` clean — Trinix's own applications are
  the ones that prove the rules are livable.
- Headlessly testable through `Vixen.Ui.Testing`, and screenshot-gated in CI (doc 16). ⚠ This is the
  requirement most likely to be skipped under schedule pressure, and skipping it on the first three
  applications is what makes it never happen for the rest.

## Order

The order is chosen so that each application *proves* something the next one needs.

1. **Terminal** — proves the SDK end to end with the least UI surface, and dogfoods PowerShell.
2. **Files** — proves the broker, Glance, the job queue and the drag-and-drop story.
3. **Settings** — proves the settings store and the service façades; unblocks everything.
4. **System Monitor** — proves the sandbox's cgroup accounting is real.
5. **Store**, **Installer** — proves doc 09 and makes the system installable by someone else.
6. **Text Edit**, **Preview**, **Calculator**, **Archive**, **Clock** — the small ones, which is when
   the SDK's rough edges show up while they are still cheap to fix.
7. **Notes**, **Photos**, **Music/Video** — the ones that need the framework to be good.
