# Application bundles, signatures and distribution images

*Phase 6. Status: implemented and verified end to end — build, sign, package,
install, launch, and refuse a tampered bundle.*

This document is the normative description of how software is packaged,
published and admitted to a Trinix system. It covers three artifacts and the
relationships between them:

| Artifact | What it is | Signed by |
|---|---|---|
| `Hello.app/` | A directory. What an installed application *is*. | A developer certificate, over a manifest inside it |
| `Hello.tdi` | An EROFS filesystem image containing one bundle. Trinix's `.dmg`. | The same certificate, over the image's payload |
| `/usr/share/trinix/pki/roots/` | The certificate authorities this machine accepts | Nothing — it is part of the signed system image |

Everything below is implemented in `src/Trinix.Bundle`, driven by
`trinix-bundle` and `trinix-open`, and exercised by `src/app-sanity.sh` (in the
build) and `run-qemu.sh --app-check` (in a VM).

---

## 1. The bundle

```
Hello.app/
└── Contents/
    ├── Info.json                   identity, version, entry point, permissions
    ├── Bin/                        executables — the entry point lives here
    │   ├── hello                   an apphost, or any ELF binary
    │   ├── hello.dll
    │   └── hello.runtimeconfig.json
    ├── Resources/                  data: images, keymaps, localisations
    ├── Frameworks/                 private shared libraries and assemblies
    └── _Signature/
        ├── manifest.json           the signed document
        ├── manifest.sig            a detached ECDSA signature over its bytes
        └── certificates.pem        the signer, then any intermediates
```

The shape is macOS's, with one rename: `Contents/Bin` rather than
`Contents/MacOS`, because the Apple name is a historical accident that would
read as a mistake here. Nothing depends on the path — the launcher reads the
entry point out of `Info.json` rather than guessing.

### Why JSON and not TOML

The plan offered either. JSON won on three counts, in order of weight:

1. `System.Text.Json` has a source generator, so parsing an **untrusted**
   manifest needs no reflection and survives trimming and NativeAOT.
2. PowerShell reads JSON natively, and PowerShell is this system's
   administrative surface.
3. A TOML parser would be a dependency, and the assembly that decides whether
   code may run takes no dependencies (see §4).

### `Info.json`

```json
{
  "schema": "trinix.bundle/1",
  "identifier": "io.trinix.hello",
  "name": "Hello",
  "version": "1.0.0",
  "shortVersion": "1.0",
  "entryPoint": "Contents/Bin/hello",
  "minimumSystemVersion": "0.3",
  "permissions": ["files.home"],
  "categories": ["developer"]
}
```

`identifier` is reverse-DNS and unique per application. `version` is
dotted-numeric and ordered — it is what a future package manager compares.
`entryPoint` must exist in the bundle and must be executable; both are checked
at seal time, because the alternative is an exec failure at launch with nothing
to say the bundle was always like that.

### Declared permissions are signed, and almost nothing enforces them yet

`permissions` is recorded and signed. The vocabulary is
[doc 04](plan/04-sandbox-and-permissions.md)'s fourteen, deliberately coarse —
a permission model finer than the enforcement behind it produces a long list
nobody reads — and it lives in `BundlePermissions`, beside the flags it parses
into, the `PermissionSet` that does the parsing, and the phrasing a consent
dialog will use. Grouped the way doc 04 groups them:

| Group | Permissions |
|---|---|
| — | `display` |
| Network | `network.client`, `network.server` |
| Files | `files.home`, `files.removable` |
| Devices | `devices.camera`, `devices.microphone`, `devices.location`, `devices.usb` |
| System | `system.notifications.critical`, `system.automation`, `system.background`, `system.capture`, `system.input` |

⚠ Every one of those strings is **inside a signature**, so renaming one is not a
refactor; it is a format change that every bundle signed before it predates. The
vocabulary was replaced exactly once, when the fourteen displaced five
placeholders — `files.all`, `audio.input`, `audio.output`, `device.input`,
`system.services` — and that was affordable because Trinix has two applications
and between them they declare `display` and `files.home`, both of which survived.
The five that vanished have no replacements: playing audio is something a process
can do, and reaching Trinix's own services is scoped by the service to the
caller's identity rather than by a mount, so neither was a permission under doc
04's test.

The field was defined before anything enforced it, and that reason still holds. A
permission set is only worth something if it is inside the signature — a
permission added after the fact by whoever is running the application is not a
permission — and retrofitting the signature to cover a new field would invalidate
every bundle signed before it.

#### What enforcement there is

`src/Trinix.Sandbox` turns a verified `Info.json` and a set of resolved paths
into the transient systemd unit doc 04 describes — as *data*: a list of
`Name=value` properties, and beside it a list of **gaps**, being what this unit
does not enforce and who would have to. Nothing calls it. `open` verifies and
then launches the entry point exactly as § 7 describes, with no unit around it,
so an application on Trinix today is contained no more than any other process.

The unit that library builds is mostly floor rather than permission, and that is
the design rather than a shortfall — doc 04's containment is *nothing unless
declared*, and authority above that floor belongs to a broker:

- the floor is unconditional and permissions only ever add to it:
  `RootDirectory=` over a composed root, `PrivateNetwork=yes`,
  `PrivateDevices=yes`, `ProtectProc=invisible`, `NoNewPrivileges=yes`, and the
  application's `$HOME` bound to `~/Library/Containers/<id>/Data`;
- **three of the fourteen change the unit at all**: `display` binds the
  compositor's socket, and the two `network.*` join one shared namespace instead
  of getting no network stack;
- **ten are the broker's**, and `trinix-broker` does not exist. For those the
  grant is the user picking a thing and the application getting an fd back, so
  declaring one buys the right to ask and nothing yet answers;
- one, `system.background`, is the session manager's: whether a unit outlives its
  last window is not an exec property.

⚠ **Two things are missing underneath it, and one is unknown.** The systemd in
this image is built `-Dseccomp=disabled`, and systemd implements
`SystemCallFilter=`, `SystemCallArchitectures=`, `MemoryDenyWriteExecute=`,
`RestrictRealtime=` and `RestrictSUIDSGID=` as seccomp filters — so the whole
syscall floor is a set of properties this system does not have, and the builder
records each absence as a gap rather than emitting a line that would do nothing.
`RootDirectory=` also needs the *system* manager, and the launcher has no
privileged path to it. The unknown is what a transient unit does with a property
systemd cannot implement: a unit file on disk logs it and starts anyway, but
these properties go over D-Bus, where an unknown one may instead fail the method
call. Those are opposite outcomes — a hardened application, or no application —
and **nothing here has yet been through a real `systemd-run`**, so none of the
above is a claim about a running system.

An unknown permission string is refused rather than dropped or approximated.
Dropping it would run the application with less authority than its developer
designed for and than its consent screen described; approximating it would grant
authority nobody wrote down. A bundle asking for something this system has never
heard of was built for a newer Trinix, and `open` says so in those words rather
than saying anything about tampering.

---

## 2. What the signature covers

`manifest.json` lists every file in the bundle except the signature material
itself, ordered ordinally by path:

```json
{
  "schema": "trinix.signature/1",
  "identifier": "io.trinix.hello",
  "version": "1.0.0",
  "architecture": "arm64",
  "signedAt": "2026-08-02T09:14:11+00:00",
  "merkleRoot": "b8c6477518a39a99…",
  "entries": [
    { "path": "Contents/Bin/hello", "size": 72016, "executable": true, "sha256": "…" }
  ]
}
```

Each entry contributes one leaf to a Merkle tree:

```
leaf  = SHA256( 0x00 ‖ path ‖ 0x00 ‖ executable ‖ int64le(size) ‖ SHA256(content) )
node  = SHA256( 0x01 ‖ left ‖ right )
```

Construction is RFC 6962's, and the two details that matter are both about
attacks rather than about hashing:

- **Leaves and nodes have different prefixes.** Without that distinction an
  interior node can be presented as a leaf — the classic second-preimage attack
  on naive Merkle trees.
- **An odd node is promoted, not duplicated.** Duplicating it makes two
  different file lists produce the same root.

The path, the executable bit and the length are hashed *with* the content. Hash
content alone and a bundle can be rearranged — the same bytes at a different
path, or a data file made executable — without the root changing.

`manifest.sig` is a detached ECDSA-P-256/SHA-256 signature, DER-encoded, over
the **literal bytes** of `manifest.json`. The file is written once and read back
byte for byte, so there is no canonicalisation step and therefore no
canonicalisation bug.

---

## 3. Verification order

`BundleVerifier` performs four steps, and each may only use what the previous
one established:

1. **Chain.** `certificates.pem`'s leaf must chain to a root in
   `/usr/share/trinix/pki/roots`, with the code-signing EKU
   (`1.3.6.1.5.5.7.3.3`) required. Intermediates in the file are *offered*, not
   trusted.
2. **Signature.** ECDSA over the manifest's bytes, under that leaf's public key.
   From here on the manifest is the signer's statement rather than the disk's.
3. **Self-consistency.** The manifest's recorded Merkle root must match the one
   recomputed from its own entries.
4. **Contents.** The bundle on disk must be exactly that file list — same set,
   same order, same sizes, same execute bits, same hashes.

Reversing any two of those produces a verifier that looks correct and is not.
Parsing the manifest before checking its signature, in particular, means an
attacker chooses what the verifier believes about the bundle it is verifying.

Failures are enumerated (`BundleFailure`) rather than left to a message,
because the difference between *this was tampered with* and *this was signed by
someone we do not know* is the whole content of what the user is told — and the
difference between an incident and a configuration mistake. The newest of them,
`UnknownPermission`, is neither: the signature was good and the developer was
careful, and the bundle asks for authority this system has never defined. It
reads as "this needs a newer Trinix", which is the point of enumerating them at
all.

A verification that passes hands back the signed manifest and not only a verdict.
Nothing in § 7 needs it today; doc 04's sandbox does, because the one thing it
must know about a bundle that `Info.json` cannot say — whether the entry point
carries a JIT — is legible in the file list, and that list has to be the signed
one rather than a directory listing taken afterwards.

### Revocation is not checked

Stated rather than omitted: there is no CRL distribution point and no OCSP
responder to reach. On an image-based system the answer to a compromised signing
key is an update that replaces the trust store. Turning the check on without
infrastructure behind it would fail closed on a machine with no network, which
is most of them.

---

## 4. Why not CMS

The Phase 6 plan named "X.509 + COSE/CMS via .NET crypto". Trinix uses X.509
with a plain detached signature instead, and the reason is a rule rather than a
preference:

> The component that decides whether code may execute depends only on what the
> platform already ships.

`System.Security.Cryptography.Pkcs` — where `SignedCms` lives — is a NuGet
package and is **not** in the shared framework. This was checked against the
runtime Trinix actually installs, not assumed. Using it would put nuget.org in
the trust path of the launcher, on a system whose every other component is built
from a tarball pinned by digest in `base/sources.json`.

What CMS would buy is a standard envelope that other toolchains can read. Nothing
else reads these signatures, and the properties it would carry are covered:
there is exactly one signer, the certificate chain is a file beside the
signature, and the signing time is *inside* the manifest — covered by the
signature — rather than in an unauthenticated attribute where it would look like
evidence without being any.

The signature format itself is standard (`ECDSA-with-SHA256`, DER), so
`openssl dgst -verify` can check one by hand when something has gone wrong at
three in the morning.

---

## 5. The `.tdi` distribution image

```
┌─────────────────────────────┐ 0
│ EROFS filesystem image      │   contains exactly one *.app at its root
├─────────────────────────────┤ certificatesOffset
│ certificates (PEM)          │
├─────────────────────────────┤ signatureOffset
│ detached ECDSA signature    │
├─────────────────────────────┤ EOF − 96
│ footer (96 bytes)           │   ends with the magic "TRINIXDI"
└─────────────────────────────┘ EOF
```

The kernel addresses EROFS blocks from the superblock and never reads past the
last one, so the footer is invisible to `mount`. That is what lets one file be
both a mountable filesystem and a signed artifact — no wrapper to strip, no
second file to lose. (Verified: `fsck.erofs` and the kernel both read a padded
image without complaint.)

The signature covers a domain-separated descriptor, not the file:

```
"trinix-tdi/1\n" ‖ int64le(payloadLength) ‖ SHA256(payload[0 .. payloadLength))
```

The length is in there because a hash alone says nothing about where the
filesystem ends, and a payload that could be re-declared as longer would let
signed bytes be reinterpreted as a different filesystem. Appending to a signed
image therefore cannot change what the signature covers — and in practice
breaks the footer first.

**EROFS rather than SquashFS**: it is the modern read-only filesystem in the
kernel, its metadata is laid out for random access, and `mkfs.erofs` takes a
directory directly — which matters because the whole build runs unprivileged in
a container and cannot use a loop device to construct an image.

**Block size is pinned at 4 KiB.** EROFS refuses to mount an image whose block
size exceeds the kernel's page size, and `mkfs.erofs` defaults to the *build*
machine's page size — 16 KiB in a container on an Apple Silicon Mac. Left to
default, every image built there would be unmountable on every Trinix machine.

### Reproducibility, precisely

The EROFS payload is built with a fixed timestamp, a fixed owner and a UUID
derived from the bundle's name, so nothing about *when or where* it was built
leaks into it. That is **not** a byte-reproducible `.tdi`: the bundle inside
carries a signature, ECDSA is randomised, and the manifest records when it was
signed. Two builds of identical source produce identical *content* — the same
Merkle root — inside two images that differ.

**The Merkle root is what to compare when asking whether two builds agree.** The
file digest is not.

---

## 6. Installing

`trinix-bundle install Hello.tdi` (root only):

1. Verify the **image** signature — before the mount. This is the point of
   having an image-level signature at all: mounting is the moment
   attacker-controlled bytes reach a kernel filesystem driver, which is a far
   larger attack surface than anything in userspace here.
2. `mount -t erofs -o ro,loop,nodev,nosuid,noexec` on a directory under
   `/run/trinix/images`.
3. Find the single `*.app` at the image root; more or fewer is an error.
4. Verify the **bundle** — from the read-only mount, so what is verified is what
   gets copied.
5. Copy to `/Applications/Hello.app`, staged as a sibling and renamed into
   place, so an interrupted install leaves either the old application or none.
6. Ask the kernel to seal each installed file with **fs-verity**.
7. Write a receipt to `/var/lib/trinix/bundles/<identifier>.json`.
8. Unmount.

An installed application is a plain directory on a writable filesystem, not a
mounted image — the same choice macOS makes. Everything downstream is simpler
against a directory, and an application that stops working when a mount goes
away is a support problem that never ends.

The receipt is **not** a trust anchor. Nothing in the launch path reads it; it
exists so `trinix-bundle list` can answer without walking `/Applications`.

### fs-verity

Enabling verity makes a file permanently read-only and gives the kernel a Merkle
tree over it, checked page by page as it is read. That is a different guarantee
from the signed manifest: the manifest proves a bundle was unmodified *at the
moment it was checked*; fs-verity means a modification cannot happen at all —
including in the window between the check and the `execve`.

It requires the filesystem to carry the feature, which is why
`image/scripts/build-image.sh` creates `/data` with `mke2fs -O verity`, and the
kernel to have `CONFIG_FS_VERITY`, which `base/recipes/linux` asserts.

It is best-effort: a bundle run from a build directory or a bind mount has no
such protection, so nothing in the launch path depends on it. What happened is
recorded in the receipt rather than left to be guessed.

---

## 7. Launching

`open /Applications/Hello.app [arguments...]` is Trinix's Gatekeeper, and the
name is the interface: on a system meant to feel like a Mac, launching an
application is a word a user already knows. It is installed twice — as `open`,
which is what to type, and as `trinix-open`, which is what to write in a script,
because `open` is a common enough word that a script should not depend on whose
`PATH` wins.

It verifies, and then does one of two things.

**By default it launches and returns**, through `posix_spawn` with
`POSIX_SPAWN_SETSID`. That is what `open` means — a request to the system to run
something, not a way to run it here — and the new session is the load-bearing
part: without it the application stays in the shell's session, and closing the
terminal it was typed into sends it a hangup. On a system whose shell is the
point, an application that could not outlive its terminal would be most of them.

`posix_spawn` rather than fork and exec, and the reason is that the launcher is
a .NET process. A forked child of a multithreaded runtime may only reach
`execve` through async-signal-safe calls, and nothing promises that of a managed
frame: one allocation between the two, one lazy P/Invoke stub, and the child
deadlocks holding a lock its thread does not exist to release. `posix_spawn`
does the whole thing inside libc, from one call, on the calling thread.

**`--wait` verifies and then `execve`s**, replacing this process with the
application. That is for anything that has to observe what it started — a
service manager, an unattended check, a debugger — and it is the older of the
two behaviours for a reason that still holds: a launcher that stayed alive as a
parent would put a .NET runtime between the session and the application, owning
its controlling terminal, forwarding its signals, and translating its exit code.
Replacing the process image makes all of those questions disappear. What it
cannot do is return, which is why it is not the default.

The application receives:

| Variable | Meaning |
|---|---|
| `TRINIX_BUNDLE` | Absolute path to the `.app` directory |
| `TRINIX_BUNDLE_IDENTIFIER` | Its `identifier` |
| `TRINIX_BUNDLE_RESOURCES` | `…/Contents/Resources` |

These are built into an explicit `envp`, because
`Environment.SetEnvironmentVariable` on .NET for Unix modifies a managed copy
and never calls `setenv` — a variable set the ordinary way would not survive the
exec.

⚠ None of this happens inside a sandbox. The transient unit a bundle's signed
permissions describe is constructed by `Trinix.Sandbox` and started by nothing
(§ 1), so what `open` produces is an ordinary process of the user's session that
happens to have been verified first.

### It verifies on every launch, not the first

Apple's Gatekeeper checks once and then relies on the kernel to enforce code
signing for the life of the process. Trinix has fs-verity, which does that job —
but only where the filesystem supports it. A cached verdict would therefore be
trusted in exactly the cases where it is least justified.

A full verification is a SHA-256 pass over a few megabytes: single-digit
milliseconds. That is a good trade for never having to reason about when the
cached answer stopped being true. If bundles grow to where it is not, the fast
path is "fs-verity is on for every file in the manifest", not a timestamp.

Refusals name the reason in one line, written for the person at the machine:

```
open: refused to launch Hello.app
  Files have been added to or removed from the application since it was signed.
  the bundle does not contain the files it is signed for (added: Contents/Resources/extra.txt)
```

---

## 8. The PKI

```
signing/
├── trusted/     committed anchors — CI, eventually a release root
└── local/       this machine's development authority — gitignored, whole
```

`scripts/build.ps1` creates `signing/local/` on first use by running Trinix's
own tool in the build container, then stages both directories into
`out/pki/roots` and hands that to BuildKit as a named context. The base image
copies it to `/usr/share/trinix/pki/roots`.

Certificate profiles live in `src/Trinix.Bundle/DeveloperPki.cs` rather than in
`openssl.cnf`, and that is the point: the constraints that matter — `pathLen`,
key usage, the code-signing EKU — are three lines that can be read together
instead of a directory of files whose relationship is implicit.

- **Root**: P-256, CA, `pathLenConstraint = 1`, `keyCertSign|cRLSign`, 20 years.
  It may delegate to an intermediate; it may not authorise a franchise.
- **Developer**: P-256, not a CA, `digitalSignature`, code-signing EKU marked
  **critical** — so a verifier that does not understand code signing refuses the
  certificate rather than accepting it for something else. 5 years.

The development root is never committed. Its public half *could* be — the
`.gitignore` rule above it would allow it — but committing it would publish a
trust anchor whose private key exists on exactly one laptop, and every other
clone would trust a key it can never use and never revoke.

The signing key reaches the build as a **BuildKit secret**. A `--build-arg` is
visible in `docker history`; a `COPY` is a layer extractable from any image
built on top; a bind mount would be readable by every later stage.

---

## 9. What is not done

| | |
|---|---|
| **Sandboxing** | Half built, and not the enforcing half. `Trinix.Sandbox` constructs the transient unit a bundle's signed permissions describe, and records what that unit would *not* enforce — but nothing in the launch path builds one, and no unit it has produced has been through a `systemd-run` on a booted system. See § 1. |
| **dm-verity on the base image** | The kernel is configured for it and the A/B layout is in place, but the root filesystem is not yet verity-protected. It belongs with Phase 7's updater, which is what writes the root hash and flips the boot entry. |
| **Revocation** | No CRL, no OCSP. See §3. |
| **Timestamping** | `signedAt` is the signer's claim. A timestamping authority matters once certificates start expiring. |
| **Intermediates** | The chain supports them; the development PKI issues directly from the root. |
| **An application from outside** | Two bundles ship, and both are Trinix's own: `Hello.app`, and `HelloUi.app`, which carries the whole of Vixen — forty assemblies and two native libraries — and is what showed the format holds a framework and not only a console program. Nothing built elsewhere has been packaged this way. |

---

## 10. Trying it

On the Mac:

```bash
./scripts/build.ps1 -Stage app -Arch arm64 -Verify
```

Produces `out/apps/arm64/Hello.tdi` and `HelloUi.tdi`, and runs the seven tamper assertions in
`src/app-sanity.sh` — a modified file, an added file, a removed file, a data
file made executable, a stripped signature, a valid signature from an untrusted
authority, and an edited image payload.

In a VM:

```bash
./scripts/run-vm.ps1 -Arch arm64 -AppCheck
```

Boots the image, installs the `.tdi` that ships in
`/usr/share/trinix/applications`, launches it, adds a file to the installed
bundle, and asserts that it stops launching.

By hand, from a root shell in the VM:

```bash
trinix-bundle install /usr/share/trinix/applications/Hello.tdi
trinix-bundle list
```

And then as the user, in PowerShell, which is the way it is meant to be typed:

```powershell
open /Applications/Hello.app
```

⚠ As the *user*. The graphical session belongs to `trinix`, and
`/etc/profile.d/trinix-session.sh` is what hands a login shell the
`XDG_RUNTIME_DIR` that PAM would set on another system — Trinix has none. `open`
as root finds no display and says so, here as on a Mac.
