# 10 — Updates, Recovery and Backup

Three features with one mechanism between them: the machine can always get back to a state that
worked. The brief lists them as § 16, § 17 and part of § 5, and they share a document because
separating them produces two snapshot systems and two definitions of "recovery".

## System updates

`IMPLEMENTATION_PLAN.md` Phase 2 already built the layout — GPT with an ESP, root A, root B and a
mutable `/data` — and Phase 6 built image signing. This is the client that uses them.

```
                    ┌──────────────┐
                    │ systemd-boot │  boot-counting; N failed boots ⇒ the other slot
                    └──────┬───────┘
              ┌────────────┴────────────┐
        ┌─────▼─────┐             ┌─────▼─────┐          ┌──────────┐
        │  root A   │             │  root B   │          │  /data   │
        │  running  │             │  staged   │          │ Btrfs    │
        │ EROFS +   │             │ verified  │          │ /home    │
        │ dm-verity │             │           │          │ /var     │
        └───────────┘             └───────────┘          │ /Apps    │
                                                          └──────────┘
```

### The update transaction

1. Fetch the manifest for the new version. Signed, versioned, and it names the root hash.
2. Fetch the image **into the inactive slot** — see chunking, below.
3. Verify the dm-verity root hash against the signature. ⚠ This is the step that makes everything
   after it safe, and it is verified *before* anything is marked bootable.
4. Re-seal the TPM policy for the new boot state (doc 05 § Encryption warned about this: skipping it
   means the keychain and the disk fail to auto-unlock after the reboot).
5. Update the ESP: a boot entry for the new slot, with `tries=3`.
6. Reboot. systemd-boot counts down a failed boot; three failures and the entry is marked bad and the
   previous slot is chosen. A successful boot to `graphical.target` **plus** the compositor and the
   broker reaching `active` marks it good.
7. `/data` is untouched, always. There is no `/etc` merge, because `/etc` is in the image and
   machine-specific configuration lives in `/data` with a symlink.

| Decision | Reason |
|---|---|
| **A/B images, not a package upgrade** | Already decided in `IMPLEMENTATION_PLAN.md` § 1. Restated because everything here depends on it: a half-applied image update is not a state that exists |
| Boot-success is defined by **services**, not by the kernel starting | A machine that boots to a black screen because the compositor cannot start has not booted successfully, and a rollback criterion that says otherwise is a criterion that never fires when you need it |
| ⚠ **dm-verity is not yet on** — the kernel is configured and the layout is in place ([`app-bundles.md`](../app-bundles.md) § 9) | It lands here, with this client, because this is what writes the root hash and flips the entry. Until it does, "signed image" means the download was signed and not that the running system is |
| The user sees **one button**: "Update" | Which updates the image, the applications and the runtimes in one action, in that order, with one progress indicator. The brief's § 17 asked for this and it is right |
| Firmware via `fwupd` | ⚠ A recipe Trinix does not have, and the only sane way to update firmware. It is on the list, and until it is there the Update pane says firmware is not managed rather than silently not managing it |

### Content-defined chunking, not deltas

The naive options are a full image every time (hundreds of megabytes for a security fix) or a
per-version binary delta (a from×to matrix that a small project cannot produce).

Neither. The image is split into **content-defined chunks** — a rolling hash picks boundaries, so an
insertion shifts only the chunks around it — each chunk is stored content-addressed in the repository,
and a client fetches only the chunks it does not already have.

| Property | Consequence |
|---|---|
| A delta from *any* version to *any* version, for free | No matrix. A machine two years stale updates efficiently |
| The repository is static files again | Same hosting story as doc 09 |
| The chunk index is small and signed with the manifest | And the whole image's hash is still verified at the end, so a malicious chunk cannot survive |
| Chunking is arithmetic | It is C#, it is testable, and it is roughly the same code shape as [`MerkleTree.cs`](../../src/Trinix.Bundle/MerkleTree.cs) which already exists |

⚠ The chunker's parameters (window, minimum, target, maximum) must be **fixed and versioned**: changing
them re-chunks everything and every client redownloads the world. This is the kind of constant that
gets tuned casually and should not be.

## Recovery

A third, small slot on the disk holding a minimal Trinix that boots when both roots fail or when the
user asks at boot.

What it can do: reinstall the current or previous system image · reset the user's password with a
recovery key · repair `/data` — ⚠️ *unlock* is struck, per [21](21-what-systemd-does-not-do.md): nothing encrypts it · browse and restore from Rewind (below) · run a terminal ·
run Disk Utility. What it cannot do: reach the network without the user configuring it, and read the
keychain.

It is `~150 MB` and it is updated as its own A/B pair on a different schedule, so a recovery image is
never the thing that breaks in the same update as the system.

## Rewind — snapshots and backup

The brief's § 16, Time Machine. Snapshot-based, because the filesystem can do this far better than a
copying backup tool.

| Decision | Reason |
|---|---|
| **`/data` is Btrfs** | ⚠ A change from the current image, which is ext4-shaped (`e2fsprogs` is the recipe present). It buys snapshots, subvolumes, transparent compression, checksums and `send`/`receive` — the last being what makes an offsite backup an incremental stream rather than a file-by-file walk. The alternative, ZFS, is out on licence and out-of-tree grounds; the alternative, LVM snapshots, is block-level and cannot do incremental send |
| Subvolumes per concern: `@home`, `@apps`, `@var`, `@containers` | So a restore of the home directory does not roll back installed applications, and `@var`'s churn does not bloat a home snapshot |
| Local snapshots hourly, thinned: 24 hourly, 30 daily, then weekly until the space budget | The mac cadence, and it is right. The budget is a percentage of free space, and thinning is aggressive when the disk fills — a backup system that fills a disk gets turned off |
| ⚠ **A snapshot is not a backup.** | A snapshot on the same disk survives a mistake and not a disk failure. The pane says this in those words, and the first-run flow asks for an external destination |
| External destination: another Btrfs volume via `send`/`receive`, or a network target over SSH | Incremental, encrypted in transit, and the destination can be a plain Linux box |
| ⚠ Encryption at rest on the destination is **required**, keyed from the keychain with a printable recovery key | An unencrypted backup of an encrypted laptop is a laptop that is not encrypted |
| Excluded by default: caches, containers' `Caches`, `/var/tmp`, anything marked `nodump` | And the exclusion list is user-editable, because an app storing real data in `Caches` is a thing that happens |

### Browsing and restoring

The brief asks to "browse a backup as if it were a normal filesystem", and with snapshots that is not
a metaphor: a snapshot *is* a directory. So Rewind's interface is **Files, in a time-travelling
mode** — the same views, the same Glance previews, plus a timeline. Restoring a file is a copy;
restoring the machine is done from the recovery slot.

Per-file version history, which doc 07 deferred here, falls out of the same thing: for a given path,
the set of snapshots in which it differs is its version list.

## What the user actually sees

Three panes and one dialog, and nothing else:

- **Software Update** — a version, a "what's new", a button, and the rollback.
- **Backup** — on/off, the destination, the last snapshot, the space used, and Browse.
- **Recovery** — the recovery key, printable and re-generatable, plus "restart to Recovery".
- The dialog: "This update needs a restart" with a Later that means later and not in ten minutes.

## Effort

| Piece | EM |
|---|---|
| Update client: manifest, slot management, ESP entries, boot-counting, success criteria | 2.0 |
| dm-verity enablement end to end (kernel config exists; the tooling does not) | 1.0 |
| Content-defined chunking: chunker, store format, fetch, resume, verification | 1.5 |
| TPM re-sealing across updates, and its failure paths | 0.5 |
| Recovery slot: image, its own A/B, and the six things it can do | 2.0 |
| Btrfs migration of `/data`, subvolume layout, image assembly changes | 1.0 |
| Rewind: snapshot scheduler, thinning, budget, exclusion | 1.0 |
| Rewind: send/receive to external and network destinations, encryption | 1.5 |
| Rewind's browsing UI (over Files) and per-file version history | 1.5 |
| `fwupd` recipe and firmware in the Update pane | 0.5 |
| **Total** | **12.5** |
