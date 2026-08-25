# 07 — Files and Quick Look

The application a user spends more time in than any other except their browser, and the one where
Linux desktops most consistently ship something a Mac user finds worse. Two things make the
difference and neither is a feature list: **it never blocks**, and **Space shows you the file**.

## Files

A `Trinix.Sdk` application on Vixen's control set — `TreeView`, `DataGrid`, `VirtualizingGrid` and
`Docking` all already exist and are tested, so this is an application, not a framework project.

### The non-negotiable behaviours

| | Decision |
|---|---|
| **The window never blocks on the filesystem** | Every enumeration, stat, thumbnail and network mount is off the UI thread with a placeholder. A file manager that freezes on an unresponsive SMB share is the single most common Linux desktop failure, and it is an architecture choice made on day one or never |
| **Every long operation is a queued, cancellable, resumable job** | Copies, moves, compression, transfers. The queue is visible in one place, survives closing the window, and shows a real rate and a real remaining time. Doc 03's dock icon shows the aggregate progress |
| **Conflicts are answered once, with the answers applied to the rest** | Keep both / replace / skip, with "apply to all remaining", and a preview of both files' size and date. The alternative — a dialog per file — is why people use `cp` |
| **Nothing is deleted immediately** | Trash, with a real restore-to-original-location. Permanent delete requires ⌘⌥⌫ and says how many bytes |
| **Selection survives a refresh** | A directory that changes under you must not lose your selection, and a sort must not scroll you elsewhere |

### Views and navigation

Icon · List · Column (the mac browser, and the one that makes deep hierarchies bearable) · Gallery.
Per-directory view settings, remembered. Tabs, and split panes with independent history — a split
pane whose two halves share a history is a split pane that fights you.

Sidebar: Favourites, iCloud-shaped nothing, Tags, Locations (volumes, network), Recents, Trash.
Reorderable, and its contents are a settings-store list (doc 02), so Search and the open/save panel
show the same favourites.

### Search inside Files

Files' search field **is** Beacon (doc 06), scoped: this folder, or everywhere, toggled. Filters for
kind, date, size and tag build a saved search, and a saved search is a real object in the sidebar —
which is where "smart folder" comes from and it costs almost nothing once the index exists.

### Tags

Colour-and-name labels, stored in the file's extended attributes (`user.trinix.tags`) **and** in the
index. Both, deliberately: the xattr means a tag travels with a file to another Trinix machine and
survives a re-index; the index means a tag search is instant and works over files on filesystems that
have no xattrs. ⚠ They disagree when a file is modified by a tool that strips xattrs, and the index is
the loser — a re-index restores from the xattr, never the other way round.

### Metadata, permissions, and the honest bit about Linux

The Get Info panel shows kind, size, dates, tags, the opening application, a preview, and permissions.
Permissions are shown as **You / Others**, mapping to owner and everyone else, with an "advanced"
disclosure for the real Unix mode, groups and ACLs. The simplification is deliberate — the person
this OS is for does not have a mental model of a group bit — and the disclosure is there because
lying about it would break the first time they hit a permission problem.

### Volumes, network, archives

| | Decision |
|---|---|
| Local volumes | Mounted under `/Volumes/<name>` by `trinixd` (doc 02), on a user action, never automatically for an unknown filesystem |
| Encryption | LUKS volumes prompt, and the passphrase can go in the keychain (doc 05). ⚠️ **Nothing supports LUKS today** — no `cryptsetup`, no `dm-crypt`; see [21](21-what-systemd-does-not-do.md) |
| SMB, SFTP, WebDAV, NFS | Real kernel mounts where possible (`cifs`, `nfs`), **FUSE** for SFTP and WebDAV. ⚠ Each is a base recipe Trinix does not have — `cifs-utils`, `sshfs`/`libfuse`, a WebDAV client — and together they are about 0.5 EM of recipe work before a line of C# |
| FTP | Cut. It is unencrypted, it is 2026, and the sidebar entry would be a liability |
| Archives | Browsed in place as a directory (zip, tar.*, 7z), extracted or created from the context menu, with the operation on the same job queue. Backed by a C# implementation for zip/tar and `libarchive` for the rest |
| Disk images | `.tdi` (doc 09) mounts on double-click and offers install. ⚠ This is the one place the Mac idiom of drag-to-Applications is reproduced exactly, and it works because it is what `.tdi` was built for |

### Version history

Not in 1.0 as a Files feature. It is a *snapshot* feature and doc 10's Rewind owns it: "restore
previous versions" in the context menu opens the file's timeline from the Btrfs snapshots, and Files
contributes the menu item and nothing else. This is the right factoring — a second versioning
mechanism inside the file manager is how you end up with two.

### Batch rename and duplicates

Batch rename: find-and-replace, add text, sequence numbers, and date/EXIF tokens, with a live preview
of the resulting names and a refusal if any collide. Duplicate detection: by size, then by content
hash, offered as a scan rather than a background process, because a background duplicate scanner is a
disk-thrashing surprise.

## Quick Look — "Glance"

Press Space. The brief calls it a killer feature and it is, and the reason is entirely that it is
**instant and universal**: a preview that works for eleven of your file types is worse than useless
because you stop trying.

### The mechanism

```
Space in Files (or Search, or an open panel, or the desktop)
  → shell asks Glance for a preview of this fd
  → Glance picks a provider by content type
  → provider renders, out of process, sandboxed, with a deadline
  → the panel shows it, or shows a typed placeholder and the metadata
```

| Decision | Reason |
|---|---|
| **Providers are out of process and sandboxed** | Same argument as doc 06's extractors, and the same machinery: a preview provider parses a file that arrived from the internet. A crash is a blank panel, not a dead file manager |
| The panel is the **shell's**, not the application's | So Space works identically in Files, in Search, in an open panel and on the desktop, and so a sandboxed application never needs to be able to render arbitrary files |
| A provider gets **one fd and 200 ms** for a first paint | Progressive is allowed — a PDF's first page then the rest — but the panel must be showing something at 200 ms or it shows the placeholder |
| The panel is a real window that can be resized, full-screened, and **opened into the application** | The mac behaviour, and the one that makes it a browsing tool rather than a peek |
| Previews are cached by content hash | So flicking back and forth through a directory is instant on the second pass |

### The 1.0 provider set

Image (all of Vixen's imaging formats, plus RAW via `libraw` if the recipe is cheap) · video and audio
(playable, scrubbable, via `Vixen.Video.Codecs`) · PDF · plain text · source code with syntax
highlighting (Vixen's `CodeEditor` in a read-only mode — free) · Markdown, rendered · HTML, rendered
as text and not as a web page ⚠ (a preview that runs script is a preview that exfiltrates) · folder
(contents and size) · archive (listing) · `.app` bundle (identity, version, signature status,
declared permissions — which makes Glance a security tool) · font (a specimen) · CSV/TSV (a table) ·
3D model (`.gltf`, `.obj`, via a Vixen viewport — the one place the engine underneath is visibly an
engine) · disk image.

### The plugin interface

The brief proposes:

```csharp
public interface IPreviewProvider {
    bool CanPreview(FileInfo file);
    Control CreatePreview(FileInfo file);
}
```

Two changes, both forced by the sandbox:

```csharp
public interface IGlanceProvider {
    // Content type, not a FileInfo: the provider never sees a path, and matching by
    // path means matching by extension, which is how a .jpg full of HTML gets rendered.
    static abstract IReadOnlyList<ContentType> Handles { get; }

    // A stream over an fd it was handed, a deadline it must respect, and a
    // Vixen element. It cannot open the file itself, because it cannot see the file.
    ValueTask<UiElement> PreviewAsync(GlanceRequest request, CancellationToken deadline);
}
```

A provider ships **inside an application's bundle**, declared in `Info.json`, so installing an
application teaches the system to preview its documents — which is the whole reason the interface is
public. It is registered by content type, and the system's own providers win ties.

⚠ Content type is sniffed, not taken from the extension: `shared-mime-info`-style magic first,
extension only as a tie-break. This is a small decision with a large security consequence.

## Effort

| Piece | EM |
|---|---|
| Files: shell, four views, tabs, splits, sidebar, view memory | 3.5 |
| The job queue: copy/move/delete/compress, conflicts, progress, resume | 2.0 |
| Trash, batch rename, duplicates, Get Info, permissions UI | 1.5 |
| Volumes, mounting, LUKS, and the four network protocols (incl. recipes) | 2.0 |
| Archives, in-place browsing, `.tdi` handling | 1.0 |
| Tags, saved searches, Beacon integration | 1.0 |
| Glance: host, sandbox, cache, panel, keyboard model | 1.5 |
| The fourteen providers | 2.5 |
| Desktop view (shared with doc 03) | 0.5 |
| **Total** | **15.5** |
