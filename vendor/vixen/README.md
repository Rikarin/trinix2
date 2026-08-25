# Vixen, vendored

The engine Trinix's applications are built on, as 46 NuGet packages, all of one
version:

```
0.1.0-trinix.a17fb05016a2
```

The suffix is Vixen's own commit. That is the pin — the same discipline
[`base/sources.json`](../../base/sources.json) applies to every upstream
tarball, applied to the one dependency that is not a tarball.

## ⚠ This pin is provisional: its bytes came off a Mac

These packages were built with `-Local` — the .NET SDK installed on the
development machine — because the build container was occupied for hours by
another job. **Every other pin in this directory's history was packed in
`mcr.microsoft.com/dotnet/sdk:10.0`, and these are not.**

The difference is not theoretical and it was measured rather than assumed:
packing an *unchanged* `Vixen.Ui.Layout`, at the same commit and the same
version, host against container, produced two different assembly hashes. The
source agrees; the bytes do not.

Nothing in Trinix's gates notices. `check-determinism.ps1` builds *Trinix*
twice on one machine and compares that; `update-vixen.ps1 -Verify` compares
versions, not hashes. So this set restores, builds, and verifies clean — which
is exactly why the warning has to live here rather than in a build log.

**Before anything depends on these bytes** — a signed image, a reproducibility
claim, a bug traced into Vixen — re-run the pack in the container and commit
the result:

```bash
./scripts/update-vixen.ps1 -VixenPath ~/Projects/Vixen -Ref a17fb05016a2
```

Same commit, same version, no `-Local`. The directory should change and the
pin should not. Delete this section when it has been done.

## Why packages and not a submodule

Trinix's C# is published *inside the build container*, whose Docker context is
this repository. A submodule at `vixen/` would work and would cost the whole
engine — 175 projects and every build gate Vixen enforces on itself — inside
every Trinix image build. What Trinix consumes is the closure of four roots,
which is six megabytes and builds in seconds.

The trade is real and worth stating: there is no editing Vixen from here, and
no stepping into it in a debugger. Both belong in `~/Projects/Vixen`, which is
where Vixen is developed. When a change is needed on that side — the platform
hook in `UiApplicationOptions.Platform` was one — it is made there, committed
there, and arrives here as a new pin.

## Regenerating

From a Vixen checkout, naming the commit you want:

```bash
./scripts/update-vixen.ps1 -VixenPath ~/Projects/Vixen -Ref <sha>
```

That walks the ProjectReference closure of the roots listed in the script,
packs each project at `0.1.0-trinix.<sha>`, replaces the contents of this
directory, and rewrites the `VixenVersion` in
[`src/Directory.Build.props`](../../src/Directory.Build.props). Symbol packages
are not kept: they are half the bytes and none of the build.

⚠ **Name the commit.** Without `-Ref` the script pins to whatever `HEAD` is at
the instant it runs, which is a commit nobody chose — and if somebody is
working in that checkout it can move between `rev-parse` and the last `pack`,
so the version would name one commit and the packages hold another. With
`-Ref` the tree is exported with `git archive` and the checkout is not read
again.

⚠ **The roots are a decision and live in the script**, commented one by one.
Adding a root is how a package that nothing references by default — the
advanced control set, the markup library, the headless test host — gets into
this directory at all. Adding a *dependency* of one is automatic, which is the
point: a Vixen release that splits an assembly does not need an edit here.

## Why the closure is bigger than it looks

`Vixen.Ui` never references `Vixen.Engine` — that boundary is the whole
application-framework claim and Vixen's own build asserts it. `Vixen.Ui.Renderer`
does reference `Vixen.Rendering`, because drawing a UI inside somebody else's
renderer is what that assembly is for, and `Vixen.Rendering` brings the engine.
So `Vixen.Engine` is in this directory and no Trinix code references it.
