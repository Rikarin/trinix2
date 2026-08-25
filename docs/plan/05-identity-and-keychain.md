# 05 — Identity and Keychain

Who the user is, how they prove it, and where every secret on the machine lives.

## Accounts

Trinix is a personal machine: one primary user, occasionally a second, no directory service.

| | Decision |
|---|---|
| Backing | Ordinary glibc users through `shadow` (already a base recipe), not a bespoke database. Everything from `sudo` to `sshd` to systemd's `User=` already reads it, and replacing it means reimplementing NSS |
| The first user is an administrator | And an administrator is *a member of one group*, not root. Privileged operations go through `polkit`-shaped checks in `trinixd`, which re-authenticates rather than caching a timestamp for five minutes |
| Full name, picture, and the mac-shaped home | `~/Documents`, `~/Library`, … per doc 01 § Paths, created by the first-boot assistant |
| Guest, and switching | Post-1.0. Fast user switching means a second compositor on a second seat and doc 03 has quite enough to do |
| ⚠ No network accounts in 1.0 | No LDAP, no AD, no SSO login. This is a personal machine and the whole enterprise identity surface is a category of work, not a feature |

## Authentication

| Factor | 1.0 | Note |
|---|---|---|
| Password | yes | PAM, `pam_unix` with yescrypt |
| FIDO2 / security key | yes | `pam_u2f`-shaped, in C# against `libfido2`. The one second factor that is genuinely usable on a laptop |
| TPM-bound auto-unlock of the disk | yes | With PCR policy, and a recovery key the user must record — doc 10 § Recovery |
| Fingerprint | ⚠ no | `libfprint` supports a fraction of readers and the good ones are the ones on Macs, which we do not run on. Post-1.0, and only with a device to test against |
| Smartcard, Yubikey PIV | post-1.0 | The plumbing is the same as FIDO2 |

**PAM.** Trinix keeps PAM rather than writing an authentication stack: it is what `sshd`, `login`,
`sudo` and `systemd-logind` all call, and the alternative is patching four programs to call something
else. The Trinix-specific parts — the lock screen, the authorisation prompt in Settings — talk to a
small C# PAM conversation host over the system bus, so no UI process ever links PAM directly. ⚠ That
host is the one place a plaintext password exists in a Trinix process; it is a separate executable,
it holds the buffer for the length of one call, and it is on the short list of things doc 16 wants a
memory-hygiene test for.

## The keychain

The brief's § 7. One store, one API, one place the user can look.

```csharp
await Keychain.SetAsync(service: "github.com", account: "user", secret: token);
var token = await Keychain.GetAsync("github.com", "user");
```

### What it holds

Passwords · API keys · OAuth tokens (with refresh, and the refresh happens *in* the keychain daemon
so a token-stealing app gets a short-lived access token rather than a refresh token) · SSH keys ·
X.509 identities and their private keys · Wi-Fi PSKs · per-app opaque secrets · the disk's recovery
key escrow if the user chooses.

### Structure

| Decision | Reason |
|---|---|
| One encrypted store per user, in `~/Library/Keychains/`, unlocked at login | A per-app store means no shared credentials — and "the browser and the mail client both know the Google password" is a feature, not a leak |
| An item is `(service, account, kind, secret, acl, metadata)` | The mac shape, and it is right: `service` is a domain or a purpose, `account` is a user, and the pair is the key |
| **The ACL is a list of bundle identities**, not "any app" | An item created by Mail is not readable by a downloaded application. The reader is identified the same way doc 04 identifies a caller, by the transient unit and hence the verified bundle |
| A read by an identity not on the ACL prompts, once, and the answer is stored on the item | The mac model, and it works because it is rare — a well-behaved application only reads what it wrote |
| Unattested callers (doc 04 § Attribution) get **session-scoped** access and cannot be added to an ACL permanently | Otherwise the compat face is a bypass of the ACL |
| ⚠ There is no "allow all" | Not even for the user. A one-click grant of the whole keychain is the thing every credential-stealer looks for |

### Encryption, and the honest version of "hardware-backed"

```
user password ──argon2id──▶ KEK ──┐
                                  ├──▶ master key ──AES-256-GCM──▶ item secrets
TPM sealed blob (PCR policy) ─────┘        (unwrapped only in trinix-secrets)
```

- The master key is wrapped **twice**: by a key derived from the password, and — where a TPM 2.0
  exists — by a TPM-sealed blob bound to the boot state. Either unwraps it. Both are needed to change
  it.
- The TPM binding is what makes an offline attack against a stolen laptop hard, and it is what makes
  "hardware-backed" a true claim rather than a marketing one. On a machine with no TPM the store is
  password-derived only, and Settings says so rather than implying otherwise.
- ⚠ **PCR policy has a cost that must be stated:** binding to the boot measurement means a firmware
  update or a kernel update invalidates the seal. Doc 10's updater re-seals as part of an update, and
  the recovery path when it fails is the password. A design that does not plan for re-sealing produces
  a machine that locks its user out after a routine update, which has happened to every system that
  tried this casually.
- Secrets are `AES-256-GCM` per item with a per-item nonce; the store's index — the service and
  account names — is encrypted too, because the *list* of sites you have accounts on is itself the
  interesting data.
- Memory hygiene: unwrapped material lives only in `trinix-secrets`, in memory locked with `mlock`,
  zeroed after use, and the process is `ProtectKernelTunables`/`ProtectProc=invisible` and
  non-dumpable. This is why doc 02 gives it its own process.

### The compat face

`org.freedesktop.Secret` — the Secret Service API. This is not optional: Chromium-based browsers,
VS Code, `git-credential-libsecret`, NetworkManager-shaped tools and a long tail of the ecosystem all
call it, and a system where those each fall back to a plaintext file has no keychain at all.

⚠ Two of its concepts do not survive the projection and the mapping is documented in code with tests:
**collections** (the Secret Service has several, Trinix has one, and a request for a named collection
maps to a label on items) and **session algorithms** (the DH-based transport encryption is
implemented, because clients use it, even though the socket is already a local one).

### The keychain application

A Settings pane, not a separate application — the brief's list has no "Keychain Access" and it should
not. It shows items grouped by service, what may read each, when each was last read and by whom, and
lets the user delete, re-scope or reveal one behind re-authentication. **The access log is the
feature**: a store that cannot tell you which application read your Google token last Tuesday is a
store you cannot audit.

## Certificates and trust

- The system trust store is `ca-certificates` (already a recipe), and it is **read-only in the image**
  — a CA added to the base is an image update.
- A user may add a certificate to a *user* trust store, and doing so requires authentication and
  shows a warning naming what it enables. Corporate MITM certificates are a legitimate need and a
  perfect attack; the honest handling is to allow it loudly.
- Trinix's own developer PKI ([`Trinix.Bundle/DeveloperPki.cs`](../../src/Trinix.Bundle/DeveloperPki.cs))
  is a *separate* root and a separate store from the TLS trust store. A code-signing root that is
  also a TLS root is a compromise that spreads.

## Effort

| Piece | EM |
|---|---|
| Accounts, first-boot assistant's user creation, the admin group and authorisation checks | 1.0 |
| PAM conversation host, lock/authorise UI plumbing, FIDO2 | 1.5 |
| `trinix-secrets`: store format, crypto, ACLs, access log | 2.0 |
| TPM sealing, PCR policy, re-sealing across updates | 1.5 |
| Secret Service compat face | 1.0 |
| Keychain pane in Settings | 0.5 |
| **Total** | **7.5** |
