# 18 — Risks and Open Questions

Ranked by expected cost, which is not the same as likelihood. The five marked **decision** need a
person to choose; the rest need work.

## R1 — The scale is four years of four people, and it is one person

**Cost: the project.** Doc 17 says 190 engineer-months out loud. Everything else in this list is
smaller than this.

Mitigations that are real rather than hopeful:

- **The milestones are usable, not just measurable.** M2 (Phase 11) is a machine its author uses all
  day. A plan whose first usable output is at 80 % completion is a plan that never gets there.
- **The cut lines are drawn in advance** (doc 17), so cutting is a decision already made rather than a
  crisis.
- **The largest single line — doc 13's runtime bundles, 6 EM — is recipe work with no design content**
  and is the most delegable thing in the plan.
- **Flatpak is accepted as a guest format** precisely because the alternative is packaging a catalogue
  by hand forever. Where an existing project's work can be taken rather than reproduced, it is.
- ⚠ The mitigation that is *not* available is reducing quality: the sandbox, the update transaction
  and the test gates are the things that cannot be retrofitted, and they are the ones most tempting to
  defer.

## R2 — There is no GPU, and much of the plan is written as if a display pipeline exists

The image ships Mesa with **lavapipe only** and the compositor composites through pixman
([the platform contract](../vixen-platform-contract.md) § 1). Every animation in doc 03, the overview,
fractional scaling, video playback in doc 11, and all of doc 13's gaming are measured against a CPU
rasteriser or not measured at all.

- The compositor's animator must be built against a **measured** frame budget, with a degradation path
  that is exercised, not theoretical. "Reduce motion" is the same path, which is why doc 15 makes it a
  first-class token rather than a courtesy.
- The screenshot goldens (doc 16) are generated on the software path, so a later GPU path has an
  oracle to be checked against.
- ⚠ The risk is not that things are slow now — it is that a year of design decisions get made against
  software rendering and are wrong the day hardware arrives. The specific ones to watch: buffer
  formats, whether damage tracking is worth its complexity, and whether the overview's live thumbnails
  are affordable.

## R3 — The sandbox migration will break bundles, and the window to do it cheaply is now

Doc 04 § Migration has a three-step plan, and its cost scales with the number of applications in
existence. Trinix has three. Every month of delay makes audit mode longer and the cliff steeper. This
is the strongest argument in the plan for doing Phase 8 early rather than after the desktop is
pleasant.

## R4 — The D-Bus stack under NativeAOT is unverified

Doc 01 pins `Tmds.DBus.Protocol` and notes it is unverified against NativeAOT on `linux-arm64`.
Everything in doc 02 rests on it, and the compositor and `trinixd` are AOT.

**Retire this with a spike, in Phase 7, before the service contracts are written**: a NativeAOT
`linux-arm64` binary that calls a method, receives a signal and passes an fd, published and run in the
container tier. If it fails, the fallback is writing the marshaller — the D-Bus wire format is small,
well specified and entirely deterministic, and it is a few hundred lines plus tests. That is a real
fallback, which is why this is R4 and not R1.

## R5 — Vixen is in development, and now two things depend on it rather than one

The firewall is [the platform contract](../vixen-platform-contract.md) and it works. This plan adds a
second dependency that the contract does not cover: **`Trinix.Sdk`'s entire UI story is Vixen's
controls, markup, styling and text**, and doc 01 explicitly forbids forking them.

- The pin plus `vendor/vixen/` plus `scripts/update-vixen.ps1` keeps a Vixen change from being a
  surprise, and that machinery already exists.
- ⚠ What is not covered: a Vixen API break lands as a compile error across every Trinix application at
  once. Doc 16's `CheckApi` protects Trinix's surface and not Vixen's. The mitigation is that Trinix
  updates its pin deliberately and on its own schedule, and that the applications are gated headlessly
  so the break is found in CI rather than in a bundle.
- The specific Vixen gaps this plan is exposed to are named where they land: accessibility (doc 15),
  localisation (doc 01 § Open), CSS Grid, and variable-height virtualisation for Files' list view.

## R6 — Accessibility depends on Vixen work that is not scheduled by this plan

Doc 15 is honest that Vixen's accessibility tree is designed and unaudited, and that a Trinix gate
cannot precede it. **Decision needed:** whether that work happens in Vixen (correct, and it is not
this repository's schedule) or as a Trinix-side shim (fast, wrong, and permanent). Recommendation:
Vixen, scheduled explicitly, in time for Phase 13 — about 2 EM upstream, and it must be asked for by
Phase 9 or it will not be there.

## R7 — There is no localisation story anywhere, in either project

**Decision needed.** Vixen has none, `Trinix.Sdk` has none, and every application in doc 11 will be
written with literal strings unless one exists first. Retrofitting localisation into forty windows is
several times the cost of building it in. The decision is not *which* library — it is whether Trinix
ships English-only for 1.0 (defensible, and it must then still keep strings extractable) or supports
languages from the start.

## R8 — Btrfs for `/data` is a change to a working image

Doc 10 needs it for Rewind and doc 07's version history. The current image is ext4-shaped. The change
touches image assembly, the installer, and the recovery path, and it is the sort of change that is easy
in Phase 12 and painful in Phase 13 once machines exist with data on them.

⚠ Btrfs has failure modes that ext4 does not — a full filesystem behaves badly, and `nodatacow` is
needed for a handful of workloads. The snapshot budget in doc 10 exists to keep the first one from
happening, and it needs testing at a genuinely full disk rather than a nearly full one.

## R9 — Packaging somebody else's browser is an ongoing security commitment

Doc 13 says this and it belongs in the risk list because it is a *recurring* cost with a deadline
attached: a browser security update that takes a week is a week of users being exploitable.
**Decision needed:** whether Trinix maintains the Zen bundle itself, or ships Flathub's and accepts
the second mechanism as the default rather than the fallback. Recommendation: Flathub's, until there
is someone whose job this is.

## R10 — The Store's review process is a person, and there is no person

Doc 09 § Submission says so plainly. Until there is, the honest configuration is: **one repository,
operated by the project, containing only what the project builds**, plus sideloading and Flatpak for
everything else. That is a smaller promise than "an App Store" and it is one that can be kept.

## R11 — PowerShell start-up cost meets a terminal that opens tabs

Doc 12 § `/bin/sh`. A 700 ms tab is a terminal that feels broken, and the profile will grow, because
profiles always grow. The gate in doc 16 is the mitigation and it must exist before the profile does.

## R12 — Two sandboxes, and a permission mapping that is approximate

Accepted in doc 13. The specific danger is not the two mechanisms — it is that the Store shows a
Flatpak's permissions in Trinix's vocabulary and the mapping is lossy, so a user reads a narrower
promise than the application actually holds. The mitigation is the explicit label, and ⚠ it must not
be softened into something reassuring during a design review.

## The five decisions

| | Question | Recommendation |
|---|---|---|
| R6 | Where does the accessibility tree get built? | Vixen, requested by Phase 9 |
| R7 | Localisation from the start, or English-only 1.0? | English-only, strings extractable, decided *now* rather than by default |
| R9 | Who maintains the browser bundle? | Flathub's, until someone owns it |
| R10 | Is there a Store, or a project repository? | A project repository. Call it that |
| — | Does automation ship in 1.0? | Doc 17's cut line 1 says no. It is the most interesting thing being cut, and that is a judgement call, not an analysis |
