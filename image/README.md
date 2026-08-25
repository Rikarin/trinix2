# Image assembly

Turns the cross-built rootfs into `out/trinix-<arch>.img`: a GPT disk that UEFI
firmware boots without any further installation step.

```bash
./scripts/build.ps1 -Stage image -Arch arm64 -Verify
./scripts/run-vm.ps1 -Arch arm64
```

## Layout

| # | Partition label | Type | Contents |
|---|---|---|---|
| 1 | `trinix-esp` | FAT32, 256 MiB | systemd-boot, loader entries, the kernel |
| 2 | `trinix-root-a` | ext4, sized to content + 25 % | The base system, mounted **read-only** |
| 3 | `trinix-root-b` | ext4-sized, empty | Target of the first A/B update |
| 4 | `trinix-data` | **Btrfs**, 2 GiB | `/var`, `/home`, `/root`, `/Applications`, containers |

`/data` used to be ext4 with `-O verity`. It is Btrfs now, which is
[doc 10 § Rewind](../docs/plan/10-updates-recovery-and-backup.md) and
[doc 18 R8](../docs/plan/18-risks-and-open-questions.md): Time Machine on Linux
is either a filesystem that can snapshot or a tool that copies files, and a tool
that copies files cannot browse a backup as a normal directory or send an
incremental stream offsite. The `-O verity` went away with the change and
nothing replaced it — ext4 keeps fs-verity as a superblock feature that must be
set at `mke2fs` time and can never be added later, whereas Btrfs stores the
Merkle tree as ordinary items and sets its own flag the first time a file is
sealed. Sealing an installed application works exactly as before; it simply no
longer depends on a decision made at image-assembly time.

The root slots stay ext4, deliberately. They are read-only images replaced
wholesale by an A/B update and verified as a unit by dm-verity — none of what
Btrfs is good at applies to them, and copy-on-write on a filesystem nothing
writes to is cost with no return.

The root slots are identical in size on purpose: the partition table is written
once, at install time, and every future update has to fit inside the slot this
one laid out.

Slot B has no loader entry. It is an empty partition until an update populates
it, and a menu entry pointing at an empty partition is a boot failure waiting
for someone to press a key — Phase 7's updater writes `trinix-b.conf`, with
systemd-boot's boot-attempt counters in the filename, at the same moment it
writes the slot.

Slot A has two entries: the normal one, and a rescue entry that boots
`systemd.unit=rescue.target`. That exists because PowerShell is the login
shell, so the interactive path now depends on a .NET runtime starting
correctly. The rescue target runs `sulogin`, which execs root's shell — and
root's shell is deliberately bash.

## `/data` — subvolumes

Doc 10 asks for four subvolumes, "so a restore of the home directory does not
roll back installed applications, and `@var`'s churn does not bloat a home
snapshot". A snapshot in Btrfs is per-subvolume, so the subvolume boundaries
*are* the restore boundaries, and the split is a decision about what should be
restorable on its own.

```
top level (subvolid 5)          mounted at /data
├── @home         → /data/home           /home is a symlink to this
├── @apps         → /data/Applications   /Applications is a symlink to this
├── @var          → /data/var            /var is a symlink to this
├── @containers   → /data/containers     ~/Library/Containers points here
├── .snapshots                           Rewind's snapshot store
└── root, srv, opt                       plain directories, not subvolumes
```

### How today's paths map onto it

The read-only root has no writable directories of its own: `build-base.sh`
relocates everything a recipe installed into `/var`, `/home`, `/root`, `/srv`,
`/opt` and `/Applications` into `/usr/share/factory/data`, leaves a symlink
behind (`/var → data/var`, and so on), and image assembly seeds the factory
content back into `/data`. So the mapping is decided by which symlink target
each subvolume is mounted at, and it is a table in `build-image.sh` — an
unmapped factory directory is a `die`, not a silent drop.

| Today | Subvolume | Why |
|---|---|---|
| `/var → /data/var` | `@var` | The churn: journal, caches, dbus and systemd state. Doc 10 wants it out of the home snapshot, and it wants a different retention |
| `/home → /data/home` | `@home` | The thing Rewind is actually for |
| `/Applications → /data/Applications` | `@apps` | Doc 10's exact argument: restoring yesterday's home must not uninstall today's application. Also the fs-verity-sealed tree, which has no business being rolled back by a document restore |
| `~/Library/Containers/<id>/{Data,Caches}` (docs 01, 04) | `@containers` | ⚠ See below |
| `/root`, `/srv`, `/opt` | top level | `/root` is a home directory in name only — root's shell is reachable from the rescue entry and nothing else — and the other two are empty. The top level is the volume that is never rolled back, which is what you want of the one holding the snapshots |

⚠ **`~/Library/Containers` is the awkward one.** Docs 01 and 04 put it *inside*
the home directory (`app.Paths.Data` is `~/Library/Containers/<id>/Data`, and
the sandbox binds it as the app's `$HOME`), while doc 10 wants `@containers` as
a peer of `@home`. Both can be true, and it is resolved with a symlink:
`~/Library/Containers → /data/containers/<user>`. The documented path stays
literally what those documents say, a `btrfs send` of `@home` carries a symlink
instead of gigabytes of app state, and per-app container data gets its own
retention — which is what doc 10 needs in order to exclude containers' `Caches`
by default.

The alternative was a *nested* subvolume under `@home`. It is rejected because
Btrfs snapshots are not recursive: a nested subvolume appears as an **empty
directory** inside every snapshot of its parent, which is a genuinely confusing
thing to meet halfway through a restore.

`.snapshots` is a subvolume rather than a directory for the usual reason: it
keeps a snapshot of the top level from recursing into the snapshot store, and
it gives the store its own identity to exclude and account for.

## Mount options

`/etc/fstab` lives in the base image
([`base/recipes/trinix-system`](../base/recipes/trinix-system/recipe.sh)), not
here, so this is the block image assembly expects to find — and checks for
before it does any work, because a filesystem type in fstab is not advisory and
a `/data` that does not mount is a system that does not boot:

```
PARTLABEL=trinix-data /data              btrfs rw,noatime,compress=zstd:1,discard=async,subvolid=5           0 0
PARTLABEL=trinix-data /data/home         btrfs rw,noatime,compress=zstd:1,discard=async,subvol=/@home        0 0
PARTLABEL=trinix-data /data/Applications btrfs rw,noatime,compress=zstd:1,discard=async,subvol=/@apps        0 0
PARTLABEL=trinix-data /data/var          btrfs rw,noatime,compress=zstd:1,discard=async,subvol=/@var         0 0
PARTLABEL=trinix-data /data/containers   btrfs rw,noatime,compress=zstd:1,discard=async,subvol=/@containers  0 0
```

| Option | Argument |
|---|---|
| `compress=zstd:1` | Doc 10 asked for transparent compression. Level 1 rather than the default 3: the levels above it buy a few percent for CPU spent on **every write**, and this is a laptop's home directory, not an archive. `compress` and not `compress-force` — plain `compress` gives up on a file whose first block does not compress, which is the right behaviour for a directory full of JPEGs and video |
| `noatime` | Not the usual micro-optimisation. On a snapshotted filesystem an atime update is a metadata copy-on-write that **unshares a block from every snapshot holding it** — so with `relatime`, merely reading a file makes the backup bigger. This is the option that keeps 24 hourly plus 30 daily snapshots from growing because someone opened a folder |
| `discard=async` | Nothing else is going to trim this disk: there is no `fstrim.timer` in the base set, and freed extents on a copy-on-write filesystem accumulate quickly. Async batches the discards off the commit path, which is the version that does not stall writes. A no-op on a VM's virtio-blk, harmlessly |
| `subvolid=5` on `/data` | The top level is mounted, not `@home` — that is what gives Rewind a path to every subvolume and to `.snapshots` without a second mount namespace |
| `space_cache=v2` — **absent** | Not because it is unwanted: the free-space tree is set in the superblock at `mkfs` time and mounting does not need to ask for it |
| `ssd` / `nossd` — **absent** | Autodetected from the device's rotational flag, and forcing it is only ever wrong: `ssd` on a spinning disk hurts, and a real Trinix machine is flash anyway |
| `autodefrag` — **absent, deliberately** | ⚠ It looks like the answer to SQLite and journal fragmentation and it is a trap here. Defragmenting a file rewrites its extents, which **unshares them from every snapshot** — turning an hourly snapshot regime into a space explosion. The fix for a fragmented database is `nodatacow` on the file, below |
| `nodatacow` — **absent, and cannot be set here** | ⚠ See below |

### ⚠ `nodatacow`, and why the image cannot apply it

A handful of workloads randomly rewrite large files in place — VM images, and
databases that already journal for themselves — and on a copy-on-write
filesystem each such write allocates a new extent. `nodatacow` is the escape
hatch, with two traps attached:

- It implies **`nodatasum`**: no checksums, and no compression either. It trades
  away exactly the two properties Btrfs was chosen for, so it is only ever right
  for data that is rebuildable or independently verified.
- It is a **per-file attribute set before the file exists**. `chattr +C` on a
  directory makes files *subsequently created in it* nocow; applying it to a
  directory that already has files does nothing to them.

Which is why it is not in the table above and not in this script: `mkfs.btrfs
--rootdir` does not carry file attributes, so there is no way to stamp `+C` at
image-assembly time at all. It has to be a `systemd-tmpfiles` `h` line applied
at boot, which belongs to `trinix-system` alongside the rest of the `/data`
provisioning. The candidates, when someone writes them:

- `/var/log/journal` — journald already sets `+C` on it itself when it creates
  the directory on Btrfs, so this one is handled and worth *not* duplicating.
- Doc 06's Beacon index (`~/Library/Beacon`) — a large SQLite file, randomly
  rewritten, and fully rebuildable, which is the profile `nodatacow` is for.
- Doc 02's settings database is **not** a candidate. It is small, it is not
  rebuildable, and losing its checksums to save a few extents is a bad trade.

### ⚠ The full-disk case

Doc 18 R8 says it plainly: a full Btrfs behaves badly in ways ext4 does not. The
allocator can fail a *delete*, because removing a file means writing metadata
first, and a filesystem with no unallocated space left has nowhere to write it.

Doc 10's snapshot budget — a percentage of free space, thinned aggressively as
the disk fills — is the runtime half of the answer. The image's half is smaller
and worth stating: `/data` ships at 2 GiB rather than 1, because Btrfs allocates
chunks up front (a fresh 1 GiB filesystem here comes out with ~126 MiB already
allocated) and metadata is DUP on a single device, so a 1 GiB `/data` starts its
life close to the size where the trouble begins. It costs nothing on disk — the
partition is written sparse — and on real hardware the installer grows it with
`btrfs filesystem resize max`, online, which unlike ext4 needs no unmount and no
grow-on-first-boot dance.

⚠ R8 also asks for this to be tested at a *genuinely* full disk rather than a
nearly full one. It has not been.

## ⚠ Migration: this is a breaking change

An existing `out/trinix-*.img`, and any machine installed from one, has an ext4
`/data`. **There is no in-place upgrade path, and this is reinstall-only.**

`btrfs-convert` does exist and does convert an ext4 filesystem in place. It is
not the answer here, for reasons that are about the layout rather than the
conversion: it produces one flat top-level subvolume, which is precisely what
the four-way split exists to avoid, so every file would still have to be moved
into `@home`/`@apps`/`@var` afterwards — a rewrite of the whole partition, done
in place, on a filesystem that cannot be mounted while it happens, on a machine
whose `/var` is inside it. That is a worse operation than a backup and a
reinstall, and it fails in a state that is neither. `base/recipes/btrfs-progs`
therefore builds with `--disable-convert`.

What doc 10's updater will eventually have to care about: an A/B image update
does not touch `/data` and cannot, so this change **cannot ship as an update**.
Any machine that exists before it lands needs its `/data` copied off, recreated,
and copied back — which is a Recovery-slot operation, and Recovery does not
exist yet either. The reason for doing this now, before either does, is doc 18
R8's: it is cheap in Phase 12 and expensive in Phase 13, and the population of
affected machines is currently zero.

## Why the root filesystem is read-only

It is the decision the whole update model rests on: an image that cannot be
modified in place can be replaced wholesale, verified as a unit, and rolled
back. Everything else in Phase 2 follows from it — see the header comment in
[`base/recipes/trinix-system/recipe.sh`](../base/recipes/trinix-system/recipe.sh)
for what it costs (users baked in at build time, `/var` and friends as symlinks
onto `/data`, a transient machine ID).

dm-verity, which is what makes "read-only" mean *verified* rather than merely
inconvenient to modify, is **not** here yet. Phase 6 built the signing
infrastructure and applied it to applications
([docs/app-bundles.md](../docs/app-bundles.md)); the base image is still merely
read-only.

The reason it did not land with the rest of the signing work is structural
rather than a matter of effort. Turning it on means computing a hash tree over
slot A, finding somewhere to put it, signing the root hash, and passing
`dm-mod.create=` on the kernel command line — Trinix has no initramfs, so the
device has to be assembled by the kernel itself before root is mounted. Every
one of those is written by the thing that also writes the loader entry and
flips the active slot, which is Phase 7's updater. Building it twice, once
without an updater and once with, would mean the version without one was never
exercised by an update.

The kernel is already configured for it (`CONFIG_DM_VERITY`), and the A/B layout
above is what it needs.

## Why nothing is mounted

Docker containers cannot attach loop devices without `--privileged`, and
requiring that for a build would undo the "Docker Desktop is the whole list"
promise. So each filesystem is populated by its own userspace tool —
`mke2fs -d` for ext4, `mkfs.btrfs --rootdir --subvol` for Btrfs, `mtools` for
FAT — and the partition images are written into the disk at offsets
`build-image.sh` computes itself.

That last one is why moving `/data` to Btrfs was possible without granting the
build privileges at all: `mkfs.btrfs` has had `--rootdir` for years, and since
6.11 it has `--subvol`, so a whole subvolume layout can be built from a staging
directory the same way `mke2fs -d` builds an ext4 tree.

Partition GUIDs, filesystem UUIDs and the ext4 hash seed are fixed constants
rather than values a tool generated from `/dev/urandom` — so two builds of the
same tree differ only where file timestamps do. Closing that last gap needs
`SOURCE_DATE_EPOCH` plumbed through the recipes; the identifiers are the half
that had to be decided up front, because the loader entry names the root
partition by GUID before the partition exists.

⚠ `/data` is now the exception to that paragraph. Its fsid and device UUID are
pinned the same way, but `mkfs.btrfs` stamps `otime`/`ctime` on every root item
from the wall clock and honours no `SOURCE_DATE_EPOCH`, so the data partition —
and therefore the image's `sha256` — differs between two builds of the same
tree. It is a real regression against the ext4 layout and it needs an upstream
change rather than a flag.

## Identifiers

Partition GUIDs spell `TRNX` and then the slot, so a partition table is
readable at a glance:

```
54524e58-0000-4000-a000-000000000001   trinix-esp
54524e58-0000-4000-a000-00000000000a   trinix-root-a
54524e58-0000-4000-a000-00000000000b   trinix-root-b
54524e58-0000-4000-a000-0000000000da   trinix-data
```

The kernel gets its root device as `root=PARTUUID=…` on the command line,
because udev is not running yet at that point. Everything mounted later —
`/data`, `/boot` — is named by `PARTLABEL=` in `/etc/fstab` instead, which
survives an image being rebuilt with different UUIDs.

Both root slots carry the generic Linux filesystem type GUID rather than the
architecture's discoverable-root GUID: two partitions claiming to be the root
make `systemd-gpt-auto-generator` ambiguous about which one is real, and Trinix
identifies its slots by label anyway.

## Booting it

`run-vm.ps1` runs QEMU *from a container* (`docker/vm.Dockerfile`), so the VM
tier needs nothing installed on the Mac either. There is no accelerator —
Docker Desktop's Linux VM does not pass virtualisation through — so this is TCG
emulation even when the guest architecture matches the host's.

```bash
./scripts/run-vm.ps1 -Arch arm64                 # interactive serial console
./scripts/run-vm.ps1 -Arch arm64 -Check          # boot unattended, assert a login prompt
./scripts/run-vm.ps1 -Arch arm64 -GraphicsCheck  # run a Wayland client under the compositor
```

`-Check` is the Phase 2 exit criterion expressed as a test, `-LoginCheck` is
Phase 3's, and `-GraphicsCheck` is Phase 4's. All three write the full console
to `out/serial-<arch>.log`, and CI runs all three.

The VM gets a `virtio-gpu-pci` device even though QEMU runs with `-display
none`: the guest needs a DRM device to modeset, and the host being headless
does not change that. What the host *cannot* provide is a GL context, so there
is no virgl and therefore no render node — which is the reason the compositor
composites in software. See
[`base/recipes/wlroots/recipe.sh`](../base/recipes/wlroots/recipe.sh).
