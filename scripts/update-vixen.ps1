#!/usr/bin/env pwsh
#requires -Version 7.4
<#
    .SYNOPSIS
        Re-pin Trinix to a Vixen commit: pack its packages, vendor them, move the version.

    .DESCRIPTION
        Vixen is developed in its own repository and consumed here as packages —
        vendor/vixen/README.md argues why. This is the one supported way to move that
        pin, and it does all three halves of it together: pack the closure at a version
        naming Vixen's commit, replace vendor/vixen/, and rewrite $(VixenVersion) in
        src/Directory.Build.props.

        The closure is computed rather than listed. The ProjectReference graph is walked
        transitively from $closureRoots below, so a Vixen release that adds an assembly
        does not need a corresponding edit here — which is the failure this would
        otherwise have: a missing package is a restore error at image-build time, hours
        away. Adding a *root* is a decision and is therefore written down; adding a
        dependency of one is not, and is therefore not.

        Packing runs in the .NET SDK container for the same reason everything else
        does: nothing is meant to be installed on the Mac. -Local is the exit, and
        its cost is measured rather than assumed — see the parameter.

    .PARAMETER VixenPath
        The Vixen checkout to pack. Default: ../Vixen beside this repository.

    .PARAMETER Ref
        Pack this commit rather than the checkout's working tree. The tree is exported
        with `git archive`, so the checkout is neither modified nor even read after the
        export, and a Vixen that is being committed to while this runs cannot move the
        pin out from under it. ⚠ Prefer this to the default whenever somebody else may
        be working in that repository: without it the pin names whatever HEAD was at the
        instant of `rev-parse`, which is a commit nobody chose.

    .PARAMETER AllowDirty
        Pack a checkout with uncommitted changes. Off by default, because the version
        this writes names a commit, and a package built from a working tree that
        commit does not describe is a pin that lies. Meaningless with -Ref, which packs
        a commit and nothing else.

    .PARAMETER Local
        Pack with the .NET SDK on this machine instead of in the container.

        ⚠ **The packages this produces are not byte-identical to the container's**, and
        that was measured rather than feared: packing an unchanged Vixen.Ui.Layout at the
        same commit and the same version, host against container, produced two different
        assembly hashes. Nothing in Trinix's gates compares them — check-determinism.ps1
        builds *Trinix* twice on one machine — so a -Local set restores, builds and
        verifies. What it costs is the pin's meaning: vendor/ then holds what one Mac
        compiled rather than what the build image compiles, and the two disagree in bytes
        while agreeing in source. Use it when the container is unavailable, and re-run
        without it before the pin is anything anyone depends on.

    .PARAMETER Verify
        Check that vendor/ matches $(VixenVersion) and change nothing. What CI runs.

    .EXAMPLE
        ./scripts/update-vixen.ps1
        Pack ../Vixen at its HEAD and re-pin to it.

    .EXAMPLE
        ./scripts/update-vixen.ps1 -Ref a17fb05016a2 -Local
        Pack that commit with the SDK on this machine — what to run when the container
        is busy and the Vixen checkout is being written to.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$VixenPath,
    [string]$Ref,
    [switch]$AllowDirty,
    [switch]$Local,
    [switch]$Verify
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'lib' 'Trinix.Build.psm1') -Force

$root = Get-TrinixRoot
$vendor = Join-Path $root 'vendor' 'vixen'
$propsPath = Join-Path $root 'src' 'Directory.Build.props'

function Get-PinnedVersion {
    $props = Get-Content -Raw -LiteralPath $propsPath
    if ($props -notmatch '<VixenVersion>([^<]+)</VixenVersion>') {
        throw "src/Directory.Build.props has no <VixenVersion> to read."
    }
    return $Matches[1]
}

# --- -Verify: does the directory hold what the property claims? ------------
#
# Both halves, because they fail differently: a version nothing was packed at is
# a restore error, and a directory holding two versions restores whichever
# resolves first and is the harder one to notice.
if ($Verify) {
    $pinned = Get-PinnedVersion
    $packages = @(Get-ChildItem -LiteralPath $vendor -Filter '*.nupkg' -ErrorAction SilentlyContinue)

    if ($packages.Count -eq 0) {
        throw "vendor/vixen holds no packages, but $propsPath pins $pinned."
    }

    $wrong = @($packages | Where-Object { $_.Name -notlike "*.$pinned.nupkg" })
    if ($wrong.Count -gt 0) {
        Write-Host "Pinned: $pinned" -ForegroundColor Cyan
        $wrong | ForEach-Object { Write-Host "  not that version: $($_.Name)" -ForegroundColor Red }
        throw "$($wrong.Count) vendored package(s) are not the pinned version."
    }

    Write-Host "vendor/vixen: $($packages.Count) package(s), all at $pinned." -ForegroundColor Green
    return
}

# --- Locate the checkout ---------------------------------------------------
if (-not $VixenPath) {
    $VixenPath = Join-Path (Split-Path -Parent $root) 'Vixen'
}
$VixenPath = (Resolve-Path -LiteralPath $VixenPath).Path
if (-not (Test-Path -LiteralPath (Join-Path $VixenPath 'Vixen.slnx'))) {
    throw "$VixenPath does not look like a Vixen checkout (no Vixen.slnx)."
}

if ($Ref) {
    $sha = (& git -C $VixenPath rev-parse --short=12 "$Ref^{commit}").Trim()
    if ($LASTEXITCODE -ne 0) { throw "$VixenPath has no commit '$Ref'." }
}
else {
    $status = & git -C $VixenPath status --porcelain
    if ($status -and -not $AllowDirty) {
        throw "$VixenPath has uncommitted changes. Commit them, or pass -AllowDirty and accept that the pin names a commit these packages were not built from."
    }

    $sha = (& git -C $VixenPath rev-parse --short=12 HEAD).Trim()
}

$version = "0.1.0-trinix.$sha"
Write-Host "Vixen $sha -> $version" -ForegroundColor Cyan

# --- The source tree that gets packed --------------------------------------
#
# With -Ref, a `git archive` export rather than the checkout. Two reasons, and the
# second is why it is not merely tidier: a commit is what the version names, so the
# tree packed should be that commit and not a working directory that happens to be
# on it — and a Vixen somebody else is committing to moves between `rev-parse` and
# `dotnet pack`, which would pack one commit under another one's name.
#
# ⚠ Exempt from -WhatIf, and it has to be: the closure is computed by reading the
# exported tree, so a -WhatIf that skipped the export would report a closure of
# nothing and call that the plan. Nothing here leaves out/.
#
# ⚠ **out/ and not the system temp directory, and that is a bug fix rather than a
# preference.** On macOS `[System.IO.Path]::GetTempPath()` is under `/var/folders/…`,
# and `/var` is a symlink to `/private/var`. Hand the compiler a project through the
# `/var` spelling and it reports its *source files* through the `/private/var` one —
# at which point every path-scoped section in Vixen's `.editorconfig`
# (`[Core/Vixen.Core/Pooling/PooledDictionary.cs]` and a dozen others) stops matching,
# because the file no longer looks like it is under the directory the file lives in.
# The pack then fails with the very CA rule that file exists to suppress, naming a
# type nobody touched. Measured, not guessed: the same commit packs clean from a path
# with no symlink in it and fails from the temp one. out/ is inside the repository,
# is already where every build artefact goes, is gitignored, and has no symlink over
# it — and it leaves the exported tree where a person can look at it when a pack does
# fail, which the temp directory did not.
$staging = Join-Path $root 'out' "vixen-$sha"
if (Test-Path -LiteralPath $staging) { Remove-Item -Recurse -Force -LiteralPath $staging -WhatIf:$false }
New-Item -ItemType Directory -Force -Path $staging -WhatIf:$false | Out-Null

$source = $VixenPath
if ($Ref) {
    $source = Join-Path $staging 'src'
    New-Item -ItemType Directory -Force -Path $source -WhatIf:$false | Out-Null

    Write-Host "exporting $sha" -ForegroundColor DarkGray
    & git -C $VixenPath archive $sha | & tar -x -C $source
    if ($LASTEXITCODE -ne 0) { throw "exporting $sha from $VixenPath failed." }
}

# --- The closure -----------------------------------------------------------
#
# ⚠ **Every root is a decision that was taken, and the comment beside it is the
# decision.** Walking from Vixen.Ui.Desktop alone is what left the pin without the
# advanced controls and without the markup library for as long as it did: the
# controls are a *second* package precisely so that an application that wants a
# button does not link a virtualiser, which means nothing an application references
# by default reaches them, which means the closure never sees them.
#
# ReferenceOutputAssembly="false" references are skipped: those are source generators
# and analysers, which travel inside the packages that use them — Vixen.Ui carries
# both Vixen.Ui.Generators.dll and Vixen.Ui.Markup.Generators.dll in
# analyzers/dotnet/cs/, which is why a .vxml in a Trinix project compiles without
# Vixen.Ui.Markup being pinned at all.
$closureRoots = @(
    # What a Trinix application's Main calls into.
    'Platform/Vixen.Ui.Desktop/Vixen.Ui.Desktop.csproj'

    # Tables, trees, docking, property grids, code editing — doc 01's table promises
    # these to applications, and doc 07's Files and doc 11's System Monitor are built
    # out of them. ⚠ Brings Vixen.Core.Yaml, and with it YamlDotNet: a docking layout
    # is a YAML document. Both are trimmed away in a PublishTrimmed application that
    # never saves a layout; neither is, in a framework-dependent one.
    'Core/Vixen.Ui.Controls.Advanced/Vixen.Ui.Controls.Advanced.csproj'

    # The VXML front end as a *library* — the parser, binder and emitter that tooling
    # and hot reload run at run time. ⚠ Not what makes markup compile; that is the
    # generator inside Vixen.Ui, and it always was. Brings Vixen.Core.Syntax.
    'Core/Vixen.Ui.Markup/Vixen.Ui.Markup.csproj'

    # The headless document and the assertion library doc 16's UI test tier is
    # specified against. Costs exactly itself — its whole dependency set is already
    # here for the runtime.
    'Core/Vixen.Ui.Testing/Vixen.Ui.Testing.csproj'
)

$closure = & {
    $seen = [System.Collections.Generic.HashSet[string]]::new()
    $stack = [System.Collections.Generic.Stack[string]]::new()
    foreach ($rootProject in $closureRoots) {
        $stack.Push([System.IO.Path]::GetFullPath((Join-Path $source ($rootProject -replace '/', [System.IO.Path]::DirectorySeparatorChar))))
    }

    while ($stack.Count -gt 0) {
        $project = $stack.Pop()
        if (-not (Test-Path -LiteralPath $project) -or -not $seen.Add($project)) { continue }

        $directory = Split-Path -Parent $project
        foreach ($match in [regex]::Matches((Get-Content -Raw -LiteralPath $project), 'ProjectReference\s+Include="([^"]+)"([^/>]*)')) {
            if ($match.Groups[2].Value -match 'ReferenceOutputAssembly="false"') { continue }
            $relative = $match.Groups[1].Value -replace '\\', [System.IO.Path]::DirectorySeparatorChar
            $stack.Push([System.IO.Path]::GetFullPath((Join-Path $directory $relative)))
        }
    }

    $seen | Sort-Object
}

Write-Host "$($closure.Count) project(s) in the closure." -ForegroundColor Cyan

if (-not $PSCmdlet.ShouldProcess($vendor, "replace with $($closure.Count) package(s) at $version")) {
    return
}

# --- What must be built before anything is packed --------------------------
#
# ⚠ **A project that packs content out of *another* project's `bin/` ships an
# incomplete package when that project has not been built, and it does it in
# silence.** `Core/Vixen.Ui.Styling.Utilities` packs the `Vixen.StyleGen` tool into
# its `tools/` — by path and not by ProjectReference, because doc 00's layer rules
# forbid a Core assembly from referencing anything under Tools — and every one of
# those `<None Include=…>` items carries `Condition="Exists(…)"`. The condition is
# there so that packing before the tool is built fails *soft*. The cost of failing
# soft is that the package is produced, is valid, restores, and is missing the tool.
# The .csproj says so in its own comments: "packing has to follow a solution build".
#
# ⚠ **This is not a -Local or a -Ref problem, and the pin it already damaged proves
# it.** The 41-package pin of 2026-08-24 *did* carry `tools/` — because the container
# mounted the live checkout, which happened to hold `Tools/Vixen.StyleGen/bin/Release/`
# from whatever the developer had built that day. So the contents of a vendored package
# depended on a directory that is not in git and that nobody looked at. A `git archive`
# export has no `bin/` at all, which is what turned an invisible accident into a visible
# 927 KiB difference — the export did not break this, it revealed it.
#
# What it costs when it is wrong: nothing at all until a project has a `vixen.ui.vcss`
# palette or names a shared token source, because the utility step is skipped without
# one. The moment a project has one — `Trinix.Sdk.Theme` and its `tokens.yaml` are
# exactly that, per docs/plan/01 — the step's third fallback looks for the tool inside
# a Vixen checkout that is not there, and the build stops on an error telling a Trinix
# developer to add a ProjectReference to a Vixen project.
$packPrerequisites = @(
    'Tools/Vixen.StyleGen/Vixen.StyleGen.csproj'
)

# --- Pack ------------------------------------------------------------------
$output = Join-Path $staging 'pack'
New-Item -ItemType Directory -Force -Path $output | Out-Null

$relative = @($closure | ForEach-Object { [System.IO.Path]::GetRelativePath($source, $_) })

# ContinuousIntegrationBuild, so that the packages do not carry this machine's
# absolute paths in their source-link metadata.
if ($Local) {
    Write-Host "packing with the SDK on this machine — see -Local's help for what that costs." -ForegroundColor Yellow

    foreach ($project in $packPrerequisites) {
        Write-Host "building $project (packed into another project's package)" -ForegroundColor DarkGray
        & dotnet build (Join-Path $source $project) -c Release --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "building $project failed." }
    }

    foreach ($project in $relative) {
        & dotnet pack (Join-Path $source $project) -c Release --nologo -o $output `
            -p:Version=$version -p:ContinuousIntegrationBuild=true --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "packing $project failed." }
    }
}
else {
    $listPath = Join-Path $staging 'closure.txt'
    ($relative -join "`n") | Set-Content -LiteralPath $listPath

    $prerequisitePath = Join-Path $staging 'prerequisites.txt'
    ($packPrerequisites -join "`n") | Set-Content -LiteralPath $prerequisitePath

    Assert-TrinixDocker

    $script = @'
set -e
while read -r project; do
  [ -n "$project" ] || continue
  dotnet build "/vixen/$project" -c Release --nologo >/dev/null
done < /prerequisites.txt
while read -r project; do
  [ -n "$project" ] || continue
  dotnet pack "/vixen/$project" -c Release --nologo -o /pack \
      -p:Version=__VERSION__ -p:ContinuousIntegrationBuild=true >/dev/null
done < /closure.txt
'@ -replace '__VERSION__', $version

    & docker run --rm `
        -v "${source}:/vixen" `
        -v "${listPath}:/closure.txt:ro" `
        -v "${prerequisitePath}:/prerequisites.txt:ro" `
        -v "${output}:/pack" `
        -w /vixen `
        'mcr.microsoft.com/dotnet/sdk:10.0' bash -c $script
    if ($LASTEXITCODE -ne 0) { throw "packing Vixen failed." }
}

# --- Replace the vendored set ----------------------------------------------
#
# Emptied rather than merged: two versions in one folder is a restore that picks
# one of them, and the pin then describes half of what was built.
New-Item -ItemType Directory -Force -Path $vendor | Out-Null
Get-ChildItem -LiteralPath $vendor -Filter '*.nupkg' | Remove-Item -Force

# Symbol packages are half the bytes and none of the build.
$packed = @(Get-ChildItem -LiteralPath $output -Filter '*.nupkg' | Where-Object { $_.Name -notlike '*.snupkg' })
if ($packed.Count -ne $closure.Count) {
    throw "packed $($packed.Count) package(s) for $($closure.Count) project(s) — the closure and the output disagree."
}
$packed | Copy-Item -Destination $vendor

# --- Move the pin ----------------------------------------------------------
$props = Get-Content -Raw -LiteralPath $propsPath
$props = $props -replace '<VixenVersion>[^<]+</VixenVersion>', "<VixenVersion>$version</VixenVersion>"
Set-Content -LiteralPath $propsPath -Value $props -NoNewline

Remove-Item -Recurse -Force -LiteralPath $staging

$size = '{0:N1} MiB' -f (($packed | Measure-Object -Property Length -Sum).Sum / 1MB)
Write-Host "vendor/vixen: $($packed.Count) package(s), $size, pinned at $version." -ForegroundColor Green
Write-Host 'src/Directory.Build.props updated. Rebuild the app stage to pick it up.' -ForegroundColor DarkGray
