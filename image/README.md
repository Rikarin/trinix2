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
| 4 | `trinix-data` | ext4 (`-O verity`), 1 GiB | `/var`, `/home`, `/root`, `/Applications` |

`/data` carries the ext4 `verity` feature because `/Applications` is on it:
installing an application asks the kernel to seal each of its files with
fs-verity, and the feature has to be in the superblock, which is decided when
the filesystem is created and never afterwards. Without it the installer still
works and reports that it could not seal anything — a considerably more
confusing thing to read than one `mke2fs` flag is to add.

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
`mke2fs -d` for ext4, `mtools` for FAT — and the partition images are written
into the disk at offsets `build-image.sh` computes itself.

Partition GUIDs, filesystem UUIDs and the ext4 hash seed are fixed constants
rather than values a tool generated from `/dev/urandom` — so two builds of the
same tree differ only where file timestamps do. Closing that last gap needs
`SOURCE_DATE_EPOCH` plumbed through the recipes; the identifiers are the half
that had to be decided up front, because the loader entry names the root
partition by GUID before the partition exists.

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
