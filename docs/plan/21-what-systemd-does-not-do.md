# 21 — What systemd Does Not Do

⚠️ **Extends [04](04-sandbox-and-permissions.md) § What a permission actually stops (its ✅ measured
block), [05](05-identity-and-keychain.md) in full, [08](08-settings-and-system-services.md) § Sound
trap 3, and [10](10-updates-recovery-and-backup.md) §§ The update transaction and Recovery.**

Doc 04 ended its measurement with a paragraph it did not have room to follow up:

> ⚠ The same string reads `-PAM -AUDIT -SELINUX -ACL -OPENSSL -TPM2 -PCRE2 -BPF_FRAMEWORK`, and three
> of those are assumed elsewhere in this plan […] **systemd here can help with none of it.**

This document is that follow-up. It is the audit of every minus in the feature string against the
recipe that produced it and against the recipes that would have to exist to turn it into a plus.

The headline is not the one doc 04 expected. **`-PAM` and `-TPM2` are the two least serious entries in
the list**, because both name libraries that Trinix's own components would link directly and neither
was ever on systemd's side of the boundary. The serious one is a flag doc 04 did not quote, because it
sits between `-LIBCRYPTSETUP` and the sentence in doc 05 that says the disk unlocks itself.

## How the finding was made

`systemctl --version` was read off a booted Trinix image on **2026-08-25** — the same VM and the same
session as doc 04's measurement:

```
systemd 257 (257)
-PAM -AUDIT -SELINUX -APPARMOR -IMA +IPE -SMACK -SECCOMP -GCRYPT -GNUTLS -OPENSSL
-ACL +BLKID -CURL -ELFUTILS -FIDO2 -IDN2 -IDN -IPTC +KMOD -LIBCRYPTSETUP
-LIBCRYPTSETUP_PLUGINS +LIBFDISK -PCRE2 -PWQUALITY -P11KIT -QRENCODE -TPM2 -BZIP2
-LZ4 +XZ +ZLIB +ZSTD -BPF_FRAMEWORK -BTF -XKBCOMMON +UTMP +SYSVINIT -LIBARCHIVE
```

Everything after that was reading, not running. Each minus was traced to
[`base/recipes/systemd/recipe.sh`](../../base/recipes/systemd/recipe.sh) — where **every one of them is
an explicit `-D…=disabled`**, never a failed probe, exactly as that recipe's header promises — and then
to [`base/sources.json`](../../base/sources.json) and `base/recipes/` to establish whether the library
behind the flag exists anywhere in the tree. Where a conclusion here is judgement rather than a file, it
says so.

## The distinction the audit turns on

A missing systemd feature falls into one of three tiers, and they cost three different things. Sorting
the flags into them is most of the work of this document, because the tier — not the minus sign —
decides whether anything is actually blocked.

| Tier | Meaning | Fix |
|---|---|---|
| **Flag** | The library is already in the base image and the recipe declined it anyway | One line in `recipe.sh` and a rebuild |
| **Recipe** | The library is not in the tree at all | A new recipe, usually a `sources.json` pin, sometimes a kernel option and a QEMU device |
| **Not systemd's** | Trinix's own component links the library directly; systemd's integration was never on the path | Nothing. The minus is noise |

⚠ There is a fourth thing a flag can mean, and doc 04 named it: **accepted and not enforced**. Doc 04
proved that `RestrictRealtime=yes` reads back cleanly from `systemctl show` and does nothing. Nothing in
*this* document's list behaves that way — every flag below either removes a program, removes a directive,
or removes a step a program would otherwise take — but the possibility is why "the recipe says disabled"
was not accepted as the end of any of these answers.

## Flag by flag

| Flag | Off because | What assumed it | What is actually true | Fix | EM |
|---|---|---|---|---|---|
| `-PAM` | `-Dpam=disabled`, recipe line 67. `linux-pam` is **not a recipe and not a `sources.json` pin** | [05](05-identity-and-keychain.md) § Authentication; [08](08-settings-and-system-services.md) § Sound trap 3; [04](04-sandbox-and-permissions.md) § Attribution by way of sessions | No session is ever registered, so `systemd --user` does not run and `loginctl` lists nothing. Password login **works** — shadow authenticates through `crypt(3)` | Recipe + policy + rewire | 1.0 |
| `-TPM2` | `-Dtpm2=disabled` and `-Dtpm=false`, lines 75–76. `tpm2-tss` is not a recipe | [05](05-identity-and-keychain.md) § Encryption; [10](10-updates-recovery-and-backup.md) § The update transaction step 4 | ⚠ **The VM has no TPM at all** — `run-qemu.sh` passes no `swtpm`, no `tpm-tis`, no `tpmdev` — and the kernel fragment sets no `CONFIG_TCG_*`. None of it is testable today | Kernel + QEMU + a managed TPM path | 0.75 |
| `-LIBCRYPTSETUP` | `-Dlibcryptsetup=disabled`, line 74. No `cryptsetup` recipe, no `CONFIG_DM_CRYPT` | [05](05-identity-and-keychain.md) § Authentication row 3; [10](10-updates-recovery-and-backup.md) § Recovery; [07](07-files-and-quick-look.md) § Volumes; [11](11-core-applications.md) § Setup Assistant, § Disk Utility | ⚠ **Trinix has no disk encryption at any layer.** See below — this is the real hole | Recipe stack + kernel | 1.5 |
| `-ACL` | `-Dacl=disabled`, line 118. No `acl`/`attr` recipe; `shadow` is `--without-acl --without-attr` too | journald's per-user journal permissions; [11](11-core-applications.md) § Console | journald creates journals `root:systemd-journal` mode 0640 and **skips the ACL step**. The group exists in `/etc/group` (gid 21) with **no members** | One line in `/etc/group`, or a recipe | 0.25 |
| `-OPENSSL` | `-Dopenssl=disabled`, line 71 — ⚠ **and `openssl` is a base recipe already** | Nothing in the plan, once traced | Costs `systemd-creds`, encrypted unit credentials, and journald's `Seal=`. Trinix uses none of them, and .NET's crypto is the same `libcrypto` | Flag | 0.1 |
| `-GCRYPT` | `-Dgcrypt=disabled`, line 72 | Nothing | The second backend for the same features `-OPENSSL` gates. With OpenSSL in the image there is no reason to add libgcrypt ever | None | — |
| `-FIDO2` | `-Dlibfido2=disabled`, line 77. No `libfido2` recipe | [05](05-identity-and-keychain.md) § Authentication row 2, a **1.0** factor | The flag itself is irrelevant — it gates `systemd-cryptenroll --fido2-device`, which doc 05 never wanted. `libfido2` being absent is the gap, and ⚠ **the VM has no USB controller**, so a key cannot be plugged in | Recipe + kernel + QEMU | 0.5 |
| `-PCRE2` | `-Dpcre2=disabled`, line 115. ⚠ **`pcre2` is pinned in `sources.json` with no recipe** — [08](08-settings-and-system-services.md) already found this | Nothing | Costs `journalctl --grep`. Doc 08 owes the recipe for WirePlumber regardless, so this becomes a flag for free | Flag, after doc 08 | 0 |
| `-BPF_FRAMEWORK` / `-BTF` | `-Dbpf-framework=disabled`, line 120 | Nothing **yet** | Gates `RestrictFileSystems=`, `SocketBindAllow/Deny=`, `RestrictNetworkInterfaces=`. `grep` finds no use of any of the three in `src/` | None for 1.0 | — |
| `-XKBCOMMON` | Not passed at all; it follows `-Dlocaled=false`, line 95 | Nothing | Gates only `systemd-localed`'s keymap validation. `libxkbcommon` **is** a recipe, for the compositor, where it matters | None | — |
| `-ELFUTILS`, `-LIBARCHIVE`, `-CURL`, `-IDN*`, `-QRENCODE`, `-P11KIT`, `-PWQUALITY`, `-IPTC`, `-BZIP2`, `-LZ4`, `-GNUTLS` | Each declined explicitly, lines 108–121 | Nothing | Backstops for `systemd-coredump`, `importd`, `sysupdate`, `resolved` and `networkd` — all of which the recipe already declines. ⚠ `-ELFUTILS` is *aligned* with [00](00-vision-and-principles.md) § 7: a system with no crash reporting has no use for a backtrace symboliser | None | — |

## `-PAM`, and what is standing in for a session

Doc 05 keeps PAM with this argument:

> it is what `sshd`, `login`, `sudo` and `systemd-logind` all call, and the alternative is patching four
> programs to call something else.

⚠ **Three of those four programs are not in this tree, and the fourth was chosen for not needing PAM.**
There is no `openssh` recipe and no `sudo` recipe. `base/recipes/shadow/recipe.sh` builds `login` with
`--without-libpam` and its header says why: util-linux's `login` has required PAM since 2.34, so shadow
was picked *specifically* to get an authenticating prompt without a PAM stack. `sources.json`'s note on
`shadow` says the same thing in one sentence. So doc 05's argument is not wrong about where PAM leads —
it is wrong that the decision has already been taken. It is open.

What PAM is actually worth here is narrower and still real: `pam_systemd` is the only thing that calls
`logind`'s `CreateSession`, and without a session, three separate mechanisms are inert. Trinix already
routes around all three, in three different places, and **nobody wrote down that they are one
workaround**:

| What a session would provide | What provides it today | Where |
|---|---|---|
| `XDG_RUNTIME_DIR` at `/run/user/1000` | `RuntimeDirectory=user/1000` on the compositor's own unit, with `RuntimeDirectoryPreserve=yes` because the Wayland socket lives in it | [`trinix-compositor.service`](../../src/Trinix.Compositor/trinix-compositor.service) |
| A seat, and DRM/input device handover | `seatd`, with `_seatd` group membership as the whole authorisation model, and `LIBSEAT_BACKEND=seatd` naming the backend so libseat does not try logind and fail | [`base/recipes/seatd/recipe.sh`](../../base/recipes/seatd/recipe.sh), and the unit's `Environment=` |
| The session's environment in a login shell | `/etc/profile.d/trinix-session.sh`, which **discovers** `XDG_RUNTIME_DIR` and `WAYLAND_DISPLAY` by looking for them rather than inheriting them | [`base/recipes/trinix-system/recipe.sh`](../../base/recipes/trinix-system/recipe.sh) § `_session_environment` |
| `systemd --user` / `user@1000.service` | Nothing. Doc 08 already decided the answer is Trinix-owned **system** units with a hardcoded `XDG_RUNTIME_DIR` | [08](08-settings-and-system-services.md) § Sound trap 3 |

Every one of those four is commented, deliberate and honest about being a stand-in. That is unusually
good. The consequence nothing states is the one that follows from reading them together:

⚠ **There is no login in the graphical path at all.** `trinix-compositor.service` is
`WantedBy=graphical.target` and runs `User=trinix`; the compositor starts at boot for a fixed user
whether or not a human has proved anything. Doc 05 § The keychain says the store is "unlocked at
login" and doc 03 § SessionUI plans a lock screen and a greeter — and there is currently no moment
those attach to. That is a Phase 8 integration problem, not a systemd problem, but it is the thing
`-PAM` is actually a symptom of.

**Verdict.** Not fatal, and not blocked. Password authentication works today without PAM. What PAM buys
is a *place to put a second factor* and a session object for `logind` to hang idle, lock and seat
handover on. My judgement: build it, because doc 05's lock screen, FIDO2 and re-authentication all want
one stack rather than three, and because `sshd` and `sudo` will arrive. But it should be argued as a
decision in Phase 8, not inherited as a premise.

## `-TPM2`, and whether a recipe is even the answer

Doc 05's diagram wraps the keychain master key twice, once from the password and once from a TPM-sealed
blob under a PCR policy; doc 10 step 4 re-seals it on every update. `systemd-cryptenroll` is not built,
so systemd contributes nothing to either. The question is what would.

| Option | What it needs | Assessment |
|---|---|---|
| `tpm2-tss` + P/Invoke from `trinix-secrets` | A new recipe (autotools; `openssl` is already there, and building ESYS only avoids the `json-c`/`libcurl` that FAPI drags in), plus a hand-written interop layer | The conventional answer, and the expensive one |
| **Managed TPM 2.0 command marshalling over `/dev/tpmrm0`** | The kernel resource-manager device, and nothing native | ⚠ My judgement, and it is the one I would take. The TPM 2.0 wire format is a byte protocol, `/dev/tpmrm0` handles session and object housekeeping, and a managed implementation keeps the security-critical layer in one language — **the same argument the `openssl` recipe already makes** for why bundle and image signing are pure C# |
| systemd's | — | Not available, and would not have been used: doc 02 gives `trinix-secrets` its own process precisely so key material is not in anything else's address space |

Either way the blocker is below both:

- ⚠ **`base/recipes/linux/config/trinix.config` sets no `CONFIG_TCG_*`.** The fragment is merged over
  `defconfig`, so whether a TPM driver exists is inherited and unpinned — and `trinix_check`'s list of
  options the image cannot boot without does not guard it. Unverified, and it should become a `grep -qx`
  line in that list the day anything depends on it.
- ⚠ **`image/scripts/run-qemu.sh` creates no TPM.** No `swtpm`, no `-tpmdev`, no `tpm-tis`/`tpm-crb`
  device. Grepping the whole harness for `tpm`, case-insensitively, returns nothing.

**Verdict.** Not blocked, and cheaper than doc 05 implies — but **untestable until the harness grows a
software TPM**, and doc 05 budgets 1.5 EM and doc 10 another 0.5 EM for work whose first prerequisite is
a QEMU flag nobody has added. Write the swtpm line before writing the sealing code, or the 2.0 EM is
spent against a machine that cannot say whether it worked.

## `-LIBCRYPTSETUP` — the one that is actually a hole

Doc 04 did not quote this flag and this document nearly did not either. It is the most serious entry in
the string.

| Claim | Where | Status |
|---|---|---|
| "TPM-bound auto-unlock of **the disk** … yes" for 1.0 | [05](05-identity-and-keychain.md) § Authentication | ⚠ There is no encrypted disk to unlock |
| "unlock and repair `/data`" | [10](10-updates-recovery-and-backup.md) § Recovery | ⚠ Same |
| "the disk's recovery key escrow if the user chooses" | [05](05-identity-and-keychain.md) § What it holds | ⚠ Same |
| First boot offers "disk encryption" | [11](11-core-applications.md) § Setup Assistant | ⚠ Same |
| "LUKS volumes prompt, and the passphrase can go in the keychain" | [07](07-files-and-quick-look.md) § Volumes | ⚠ Same |
| Disk Utility does "LUKS" | [11](11-core-applications.md) | ⚠ Same |

Read together: **six places across four documents describe a full-disk-encryption feature, and Trinix
has no dm-crypt in the kernel, no `cryptsetup` recipe, no `sources.json` pin, and systemd's own
`systemd-cryptsetup` unbuilt.** `cryptsetup-bin` appears once in the tree, in
`docker/host-tools.Dockerfile` — that is the build machine, not the image.

This is a genuine gap rather than a convenience one, and it is not one systemd could have covered
anyway: `-Dlibcryptsetup=disabled` only removes systemd's generator and `cryptenroll`; the missing
things are a kernel target, a userspace library and a format. It is also the *reason* the TPM work
exists — a TPM seal that protects a keychain file on an unencrypted `/data` is a smaller claim than doc
05 makes for it.

**Verdict.** Doc 05 § Authentication's third row and doc 10 § Recovery should not say "yes" for 1.0
until this is scheduled. It is roughly **1.5 EM** of recipe work (`cryptsetup` and the `libdevmapper`,
`json-c` and `argon2` beneath it), plus `CONFIG_DM_CRYPT` and the crypto API it selects, plus the
image-assembly change that makes `/data` a LUKS container — and that last one collides with doc 10's
already-⚠'d Btrfs migration and [18](18-risks-and-open-questions.md) R8, so it should be done in the
same pass or not started.

## `-ACL` — the cheapest fix in the list, and it may not need a recipe

journald's per-user journal permissions are the only consumer that matters. What actually happens with
`HAVE_ACL` off, given `Storage=persistent` in `base/recipes/trinix-system/recipe.sh`'s `journald.conf`:
journald still creates and rotates journal files, still splits them by UID, and still owns them
`root:systemd-journal` at mode 0640. It **silently skips** the step that adds a per-user ACL on top.
Nothing breaks; the user simply is not on the file.

The substitute is already half-built. `trinix-system` writes `systemd-journal:x:21:` into `/etc/group`
— **with no members**. Group membership grants exactly the access mode 0640 was designed to grant, so
adding the `trinix` user to gid 21 makes doc 11's Console able to read the journal with no recipe at
all. What it loses is granularity: one member sees every journal, not only their own. ⚠ On a machine
doc 05 § Accounts defines as "one primary user, occasionally a second", **that is not a loss** — and
that is my judgement, not a measurement. It becomes one the day there is a second user.

⚠️ **RETRACTED 2026-08-26 — both of these are false, and were false when written.**
`base/recipes/linux/config/trinix.config` lines 84 and 90 set `CONFIG_BTRFS_FS=y` and
`CONFIG_BTRFS_FS_POSIX_ACL=y`, the second carrying a comment naming the exact journald ACL need this
document argues above cannot be met, and `/data` is Btrfs with four subvolumes.

The cause is worth more than the error. This document was written in a git worktree created **before**
the Btrfs work merged: Btrfs landed at 23:02:56 and this document at 23:11:53, against a tree nine
minutes stale. ⚠ **An audit run from a stale worktree audits a repository that no longer exists**, and
that has now happened twice — so a research task's first act should be to confirm its base is current.
The substantive findings above were separately re-verified and stand.

*The original two paragraphs are kept below for the record.*

Two corrections to the premise this audit started from, both worth recording because both were assumed
and neither is true:

- ⚠ **There is no `CONFIG_BTRFS_FS_POSIX_ACL` and no Btrfs at all.** `grep -i btrfs` over the kernel
  fragment returns nothing; the only Btrfs in the tree outside the plan documents is shadow's
  `--without-btrfs`. Btrfs is a doc 10 decision and an R8 risk, not a kernel option somebody enabled.
- ⚠ **`CONFIG_EXT4_FS_POSIX_ACL` is not asserted either.** The fragment sets only
  `CONFIG_TMPFS_POSIX_ACL=y`. Whether the ext4 `/data` supports ACLs at all is inherited from
  `defconfig` and unguarded by `trinix_check` — the same unpinned-inheritance shape as the TPM driver
  above, and the same one-line fix.

**Verdict.** No recipe needed for 1.0. Add the group member, and add both config symbols to
`trinix_check`'s required list so the answer stops being inherited.

## `-OPENSSL` and `-GCRYPT` — a flag, not a gap

systemd wants a crypto backend for `systemd-creds` and `LoadCredentialEncrypted=`, for journald's
Forward Secure Sealing (`Seal=`, `journalctl --setup-keys`), for `systemd-repart`'s verity signing, and
for DNSSEC in `resolved`. Grepping `src/` and `base/` for `LoadCredential`, `SetCredential`,
`systemd-creds` and `Seal=` returns **nothing**; `resolved` and `repart` are declined in the recipe
already; and doc 10 verifies image signatures in C# over .NET's own X.509 and CMS, which
`base/recipes/openssl/recipe.sh` says in as many words is a deliberate choice so that "the
security-critical policy layer is one language and one review".

⚠ The notable part is that **`openssl` is already a base recipe** — .NET's
`System.Security.Cryptography` is a thin layer over its `libcrypto` and Phase 3 does not start without
it. So `-OPENSSL` is not a missing dependency at all. It is a recipe line that declined a library
sitting in the same image, under a heading — "crypto and TLS stacks not yet in the base" — that is
**factually wrong for this one entry**.

**Verdict.** Harmless today, misleading in the recipe, and the fix is deleting one line. `-GCRYPT` needs
no fix ever: libgcrypt is the alternative backend for the same features, and adding a second crypto
library to an image that already has one is a cost with no matching benefit.

## Plan claims that are now wrong

Stated as claims, so each can be edited or defended individually. Doc 04's ✅ block already carries the
seccomp and polkit versions of this list; these are the ones it did not reach.

| # | Document | The claim | Why it is wrong |
|---|---|---|---|
| 1 | [05](05-identity-and-keychain.md) § Authentication, row 3 | TPM-bound auto-unlock **of the disk**, 1.0, yes | There is no encrypted disk. No `CONFIG_DM_CRYPT`, no `cryptsetup` recipe, no pin |
| 2 | [05](05-identity-and-keychain.md) § What it holds | "the disk's recovery key escrow" | Same; there is no such key |
| 3 | [05](05-identity-and-keychain.md) § Authentication, ¶ **PAM** | PAM is kept because `sshd`, `login`, `sudo` and `logind` all call it; the alternative is patching four programs | Three are not in the tree and the fourth was chosen for not needing PAM. The reasoning is sound; the premise that it is already settled is not |
| 4 | [05](05-identity-and-keychain.md) § Authentication, row 2 | FIDO2 is a 1.0 factor | Defensible as a plan, but `libfido2` is not a recipe and ⚠ the VM has no USB controller, so it cannot be exercised even once written |
| 5 | [05](05-identity-and-keychain.md) § The keychain | The store is "unlocked at login" | There is no login in the graphical path. The compositor starts at `graphical.target` as `User=trinix` |
| 6 | [05](05-identity-and-keychain.md) § Effort | TPM sealing and PCR policy, 1.5 EM | Prices the sealing, not the TPM: no kernel driver pinned, no software TPM in the harness, and no chosen interop path |
| 7 | [10](10-updates-recovery-and-backup.md) § The update transaction, step 4 | "Re-seal the TPM policy for the new boot state" | Presupposes 6, and presupposes 1 for half of what it protects |
| 8 | [10](10-updates-recovery-and-backup.md) § Recovery | Recovery can "unlock and repair `/data`" | Nothing to unlock |
| 9 | [07](07-files-and-quick-look.md) § Volumes, [11](11-core-applications.md) §§ Setup Assistant, Disk Utility | LUKS volumes, disk encryption at first boot, LUKS in Disk Utility | All three inherit 1 |

⚠ **Not on this list, deliberately:** doc 08's power management. It is backed by `logind`, and logind's
lid-switch and power-key handling come from udev-tagged input devices rather than from a session, so
`-PAM` does not take it with it. That was worth checking and it survives.

## Recommendation

Doc 17 puts docs 04 and 05 together in **Phase 8**, at 24.0 EM. Nothing in this audit changes the design
of either document; what it changes is that Phase 8 opens with a week of recipe work that is not in
anyone's number.

| Prerequisite | Tier | EM |
|---|---|---|
| `cryptsetup` and its stack, `CONFIG_DM_CRYPT`, `/data` as a LUKS container — **scheduled with doc 10's Btrfs migration or not at all** | Recipe | 1.5 |
| `linux-pam` recipe and pin, `/etc/pam.d` policy, systemd rebuilt `-Dpam=enabled`, shadow and the compositor unit rewired onto a real session | Recipe | 1.0 |
| `swtpm` in `run-qemu.sh`, `CONFIG_TCG_*` pinned in `trinix_check`, and the interop decision above taken | Kernel + harness | 0.75 |
| `libfido2` (with `libcbor` beneath it), `CONFIG_HIDRAW` pinned, a USB controller in the VM | Recipe | 0.5 |
| `-Dacl=enabled` **or** `trinix` in gid 21, plus `CONFIG_EXT4_FS_POSIX_ACL` pinned | Flag | 0.25 |
| `-Dopenssl=enabled`, and the recipe's "not yet in the base" heading corrected | Flag | 0.1 |
| `-Dpcre2=enabled`, once doc 08 writes the recipe it already owes | Flag | 0 |
| `bpf-framework`, `xkbcommon`, `gcrypt`, and the rest of the tail | None | — |
| **Total not budgeted anywhere** | | **4.0** |

These are my estimates rather than measurements, and they are recipe-and-plumbing figures: none of them
includes the feature above it.

**The recommendation is to split them.** The 0.35 EM of flag work — ACL, OpenSSL, and the two kernel
symbols added to `trinix_check` — is an afternoon and should be done now, with doc 04's already-booked
`libseccomp` rebuild, because it is one systemd rebuild rather than two. The 1.5 EM of LUKS work belongs
to whoever schedules doc 10's Btrfs migration, since both rewrite `/data` and doing them separately means
doing the image-assembly change twice. The remaining 2.15 EM — PAM, TPM, FIDO2 — is Phase 8's opening
and should be stated as such in doc 17's Phase 8 line, which today reads as though doc 05 starts with
C#.

⚠ **What this document does not claim.** With one exception, nothing in doc 05 or doc 10 is
*unachievable*; PAM, TPM2, libfido2 and cryptsetup are ordinary libraries and the plan's decision to keep
the security-critical layer in C# on top of them is unaffected. `-PAM` does not stop a password from
being checked, `-TPM2` does not stop a key from being sealed, and `-OPENSSL` does not stop anything at
all. The exception is `-LIBCRYPTSETUP`, and even there the flag is the smallest part of the problem. What
the audit establishes is narrower and duller than "systemd cannot do this": **nothing arrives for free,
four documents were written as though some of it would, and the prerequisites are 4.0 EM that no phase
currently contains.**

## What could not be determined from the tree

- Whether `CONFIG_EXT4_FS_POSIX_ACL`, `CONFIG_TCG_TPM` and `CONFIG_HIDRAW` are actually on. The fragment
  is merged over each architecture's `defconfig` and the kernel source is not vendored here, so the
  answer is inherited and differs per arch. Reading `/usr/lib/trinix/kernel-config` from a built image
  settles all three in one grep — the recipe already installs it for exactly this purpose.
- Whether systemd 257 installs `70-uaccess.rules` and the `uaccess` udev builtin when `HAVE_ACL` is off.
  It is moot today — seatd owns DRM and input, and [08](08-settings-and-system-services.md) records that
  the kernel has no `CONFIG_SND` and the VM no audio device — but it becomes live with PipeWire and with
  the first USB device, and it is answered by listing `/usr/lib/udev/rules.d` in a built image.
- Whether `Tpm2Lib`-style managed marshalling over `/dev/tpmrm0` is viable under NativeAOT. The
  ⚠ recommendation above rests on it and it is unverified; [18](18-risks-and-open-questions.md) R4 is the
  precedent for how that question gets answered, which is by building the smallest thing that would fail.
- No build was run and no image was rebuilt for this document. Every flag verdict is a reading of
  `recipe.sh` against the feature string doc 04 measured, not a fresh observation.
