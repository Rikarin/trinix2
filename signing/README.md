# Signing

Trinix signs three different things, with three different trust roots:

| What | Mechanism | Enforced by |
|---|---|---|
| Base system image (A/B slots) | dm-verity root hash, signed | Kernel, at mount time |
| Installed app bundles | fs-verity + signed bundle manifest | Gatekeeper-analog service, at launch |
| `.tdi` distribution images | Detached CMS signature over the image | `Trinix.Bundle` / the package manager |

The deliberate constraint: **no invented crypto**. X.509, CMS/COSE, fs-verity and
dm-verity are all standard and all already implemented by the kernel or by .NET's
`System.Security.Cryptography`. Only *policy* — who may sign what, and what a
verification failure does — is ours.

## Layout

```
signing/
├── ca/                 # Dev PKI *layout* and OpenSSL-free generation scripts
│   └── policy/         # Certificate profiles: root, intermediate, developer
├── trusted/            # Public roots baked into the image (committed)
└── local/              # Developer keys — gitignored, never committed
```

## Key policy

`.gitignore` refuses `*.key`, `*.pem`, `*.p12`, `*.pfx` anywhere under `signing/`
(public keys must be named `*.pub.pem` to be committable). This is a backstop, not
the policy — the policy is:

- The **production** root key never exists on a developer machine, and never in
  this repository at any point in its history.
- The **development** root is generated locally per developer (`signing/local/`),
  is not shared, and images signed by it are marked as such so they cannot be
  mistaken for release builds.
- CI signs nightly images with a key held in the CI secret store, distinct from
  the release key.

Real key generation lands with Phase 6; until then this directory records the
shape so nothing has to be retrofitted.
