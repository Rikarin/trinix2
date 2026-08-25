# 09 — Applications and the Store

Phase 6 built the hard half: a bundle with a signed Merkle manifest, a `.tdi` image, a developer PKI,
an installer and a Gatekeeper that refuses a tampered bundle
([`docs/app-bundles.md`](../app-bundles.md)). What is missing is everything about *getting* an
application: where it comes from, how it updates, and how a user finds it.

## The model, restated

```
System packages          one immutable, signed image. Updated as an image. Doc 10.
Applications             self-contained signed bundles, outside the image. This document.
Runtimes                 signed bundles that applications depend on by name+version. Doc 13.
```

There is no dependency solver and there will not be one. An application declares at most a runtime,
and a runtime is one of a handful of things we build. The whole reason the base recipe count can stay
near sixty is that this line is not crossed.

## Repositories

A repository is **static files behind any HTTP server**. No API, no accounts, no server-side code.

```
repo/
  index.json                signed: every application, its versions, hashes, sizes, metadata
  index.json.sig
  apps/<id>/<version>.tdi
  apps/<id>/<version>.tdi.sig
  media/<id>/…              icons and screenshots, content-addressed
```

| Decision | Reason |
|---|---|
| Static files, signed index | It can be hosted on an object store for a few dollars, it can be mirrored by anyone, and it cannot leak what a user installed because there is nothing to log beyond a web server's access log |
| The index is signed by a **repository key**, distinct from developer keys | Two questions — "is this the real index?" and "who wrote this application?" — with two answers. The bundle signature is already checked at launch, every launch ([`app-bundles.md`](../app-bundles.md) § 7); the index signature is checked at fetch |
| Index fetch is **conditional and chunked**; a client downloads a delta of the index | An index that grows to tens of megabytes and is refetched hourly is how a package system becomes a bandwidth story |
| ⚠ **No per-install telemetry, and the design makes it impossible rather than optional** | Doc 00. There is no install endpoint to call. Popularity, if it is ever wanted, comes from a voluntary, aggregated, opt-in count and not from the fetch path |
| Several repositories, ordered, user-editable | The system repository plus whatever the user adds. A third-party repository is a **trust decision with a dialog**, and applications from it are labelled in the Store |
| Mirrors are a property of a repository, not of the client | So a mirror cannot be substituted by an attacker who controls DNS but not the signing key |

## `tpkg`

The CLI, and the PowerShell module beside it — both over the same C# library, which is also what the
Store application uses.

```
tpkg search blender          tpkg install io.example.notes     tpkg list --updates
tpkg info io.example.notes   tpkg remove io.example.notes      tpkg update [--all]
tpkg install ./Notes.tdi     tpkg pin io.example.notes 1.4     tpkg verify --all
```

`Get-TrinixApp`, `Install-TrinixApp`, `Update-TrinixApp` — the PowerShell verbs, emitting objects, in
`Trinix.Management` beside the cmdlets that already exist. ⚠ The brief's `os install firefox` shape is
tempting and rejected: a single `os` verb that manages applications *and* the system image *and*
services is one command whose help output nobody can read. `tpkg` for applications, doc 10's
`tsystem` for the image.

## Install and lifecycle

Install is a copy, and that is the point of the format.

```
fetch .tdi → verify index entry hash → verify footer signature (exists)
           → mount read-only → verify bundle Merkle manifest (exists)
           → copy into /Applications or ~/Applications
           → enable fs-verity (exists)
           → register: content types, URL schemes, verbs, search providers, Glance providers
           → done. No scripts ran.
```

| Decision | Reason |
|---|---|
| **No install scripts. None.** | The single most important line in this document. Every package system that allows a maintainer script has, as a consequence, a package system where installing software means running arbitrary code as root. Everything a script would do — registering a handler, a service, a file type — is a **declaration in `Info.json`** that the installer performs |
| A first launch, not a post-install step, does per-user setup | And it runs sandboxed like everything else |
| `/Applications` (system, needs authorisation) and `~/Applications` (user, does not) | The mac split, and it is the one that lets a user install software without becoming an administrator |
| Uninstall removes the bundle, and **asks separately about the container** | Data outliving its application by accident is a disk-space mystery; data being deleted with its application by surprise is a catastrophe. Ask, default to keep, and show the size |
| Updates are **atomic replacement**: new bundle beside old, verify, swap, remove | An application being updated while running keeps the old bundle alive until it exits — which the fd on the running executable already guarantees, and which is why the swap is a rename |
| Rollback to the previous version is kept for 30 days or one version | Cheap with EROFS compression, and the alternative is that a bad update is unrecoverable without a network |
| ⚠ **Downgrade protection**: the installer refuses a version lower than the installed one unless the user explicitly rolls back | Otherwise a repository can serve an old, vulnerable version as an "update" |

## Automatic updates

Off for the first release, then on by default with a per-application opt-out — and the reason for
that order is that automatic updates are only safe once rollback is proven, and rollback is only
proven by people using it.

Checked daily, downloaded on unmetered networks, applied when the application is not running, and
never applied to an application whose *permissions grew* without asking. That last rule matters more
than the rest of the paragraph: a silent update that adds `devices.microphone` is a bypass of doc 04's
entire consent model, and the diff of the signed permission sets is exactly what makes catching it
trivial.

## The Store

A `Trinix.Sdk` application. Search, categories, an application page with screenshots, description,
version history, size, **the declared permissions rendered the same way the consent dialog renders
them**, the developer's identity from the signing certificate, and the source repository.

| | Decision |
|---|---|
| Reviews and ratings | ⚠ **Not in 1.0.** A review system needs accounts, moderation, and a server — the three things the static-repository design exists to avoid. When it arrives it is a separate signed feed, and the Store reads it optionally |
| Paid applications | Not in 1.0, and not without a payment relationship that does not exist. The bundle format has room for a receipt; nothing else is designed |
| Editorial | A curated JSON in the repository. A front page is worth having and costs nothing |
| Submission | A developer signs and submits a `.tdi` plus metadata to a repository; the repository operator runs `trinix doctor --store`, a signature check, and a manual review of the permission declarations against the description. This is a **process**, honestly staffed by whoever runs the repository, and pretending otherwise would be the plan's least honest sentence |
| Sideloading | Always allowed. Double-clicking a `.tdi` installs it, Gatekeeper checks the signature, and an unsigned one needs Developer Mode. The Store is a convenience, not a gate |

## Effort

| Piece | EM |
|---|---|
| Repository format, signed index, delta fetch, mirrors, multi-repo trust | 1.5 |
| `tpkg` + the PowerShell cmdlets over one library | 1.5 |
| Install/update/uninstall lifecycle, registration, rollback, downgrade protection | 2.0 |
| Automatic updates incl. the permission-diff rule | 0.5 |
| The Store application | 2.5 |
| Repository tooling for whoever operates one (sign, publish, validate) | 1.0 |
| **Total** | **9.0** |
