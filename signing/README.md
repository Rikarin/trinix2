# Signing

Trinix signs three different things, with three different trust roots:

| What | Mechanism | Enforced by | Status |
|---|---|---|---|
| Base system image (A/B slots) | dm-verity root hash, signed | Kernel, at mount time | Phase 7 — the kernel is configured for it; the updater is what writes the root hash |
| Installed app bundles | Signed manifest + fs-verity | `trinix-open`, at every launch | **Done** (Phase 6) |
| `.tdi` distribution images | Detached signature over the payload | `Trinix.Bundle`, before the mount | **Done** (Phase 6) |

The deliberate constraint: **no invented crypto**. X.509, ECDSA, SHA-256,
fs-verity and dm-verity are all standard and all already implemented by the
kernel or by .NET's `System.Security.Cryptography`. Only *policy* — who may sign
what, and what a verification failure does — is ours.

A second constraint, added when Phase 6 was implemented and worth stating
because it decided a design question: **the component that decides whether code
may execute depends only on what the platform already ships.** That is why the
signature container is a detached ECDSA signature over a file rather than
CMS/PKCS#7 — `System.Security.Cryptography.Pkcs` is a NuGet package and is not
in the shared framework, and putting a package feed in the launcher's trust path
is not worth a standard envelope that nothing else reads. The reasoning in full
is in [`docs/app-bundles.md`](../docs/app-bundles.md) §4.

## Layout

```
signing/
├── trusted/            # Public roots baked into every image built here (committed)
└── local/              # This machine's development authority — gitignored, whole
    ├── dev-root.pub.pem        the trust anchor
    ├── dev-root.key.pem        its private key
    ├── dev-identity.pub.pem    the certificate bundles are signed with
    └── dev-identity.key.pem    its private key
```

`signing/local/` is created on the first build that needs it, by
`Assert-TrinixSigningIdentity` in `scripts/lib/Trinix.Build.psm1`, which runs
Trinix's own `trinix-bundle pki` inside the build container. The certificate
profiles — path length, key usage, the critical code-signing EKU — live in
`src/Trinix.Bundle/DeveloperPki.cs`, not in an `openssl.cnf`: one implementation
of the policy, in the language everything else that reads it is written in.

Before each build, `scripts/build.ps1` stages `trusted/` and `local/` into
`out/pki/roots` and hands that directory to BuildKit as a named context; the
base image copies it to `/usr/share/trinix/pki/roots`. Only certificate
authorities are staged, checked by reading each certificate's basic constraints
rather than by trusting its filename.

## Key policy

`.gitignore` refuses `*.key`, `*.pem`, `*.p12` and `*.pfx` anywhere under
`signing/` (public keys must be named `*.pub.pem` to be committable), and then
excludes `signing/local/` in its entirety. The second rule is the one that
matters, and it is not redundant: the development root's *public* half would
pass the first rule, and committing it would publish a trust anchor whose
private key exists on exactly one laptop — which every other clone would then
trust and could never use or revoke.

That is the backstop. The policy is:

- The **production** root key never exists on a developer machine, and never in
  this repository at any point in its history.
- The **development** root is generated locally per developer
  (`signing/local/`), is not shared, and is the only anchor a locally built
  image trusts — so an image built here cannot be mistaken for a release build:
  a release-signed application would not launch on it, and vice versa.
- CI signs nightly images with a key held in the CI secret store, distinct from
  the release key, with its public root committed to `trusted/`.

The signing key reaches a build as a **BuildKit secret**, never a `--build-arg`
(visible in `docker history`), never a `COPY` (a layer extractable from any
image built on top), and never a bind mount (readable by every later stage).

## Rotating the development identity

Delete `signing/local/` and rebuild. Applications signed by the old identity
will stop verifying on images built afterwards, which is the correct behaviour
and the cheapest possible demonstration that the trust store is doing something.
