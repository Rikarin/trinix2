# Vixen, vendored

The engine Trinix's applications are built on, as 41 NuGet packages, all of one
version:

```
0.1.0-trinix.6e46eee4180a
```

The suffix is Vixen's own commit. That is the pin — the same discipline
[`base/sources.json`](../../base/sources.json) applies to every upstream
tarball, applied to the one dependency that is not a tarball.

## Why packages and not a submodule

Trinix's C# is published *inside the build container*, whose Docker context is
this repository. A submodule at `vixen/` would work and would cost the whole
engine — 175 projects and every build gate Vixen enforces on itself — inside
every Trinix image build. What Trinix consumes is `Vixen.Ui.Desktop` and its
41-package closure, which is five megabytes and builds in seconds.

The trade is real and worth stating: there is no editing Vixen from here, and
no stepping into it in a debugger. Both belong in `~/Projects/Vixen`, which is
where Vixen is developed. When a change is needed on that side — the platform
hook in `UiApplicationOptions.Platform` was one — it is made there, committed
there, and arrives here as a new pin.

## Regenerating

From a Vixen checkout at the commit you want, with a clean tree:

```bash
./scripts/update-vixen.ps1 -VixenPath ~/Projects/Vixen
```

That walks `Vixen.Ui.Desktop`'s ProjectReference closure, packs each project at
`0.1.0-trinix.<sha>`, replaces the contents of this directory, and rewrites the
`VixenVersion` in [`src/Directory.Build.props`](../../src/Directory.Build.props).
Symbol packages are not kept: they are half the bytes and none of the build.

## Why the closure is bigger than it looks

`Vixen.Ui` never references `Vixen.Engine` — that boundary is the whole
application-framework claim and Vixen's own build asserts it. `Vixen.Ui.Renderer`
does reference `Vixen.Rendering`, because drawing a UI inside somebody else's
renderer is what that assembly is for, and `Vixen.Rendering` brings the engine.
So `Vixen.Engine` is in this directory and no Trinix code references it.
