#!/usr/bin/env pwsh
#requires -Version 7.4
<#
    .SYNOPSIS
        Fail if a Trinix library's public surface is not the one committed beside it.

    .DESCRIPTION
        docs/plan/16-build-ci-and-testing.md § The gates asks for `CheckApi`, "a
        shipped/unshipped public API baseline, Vixen-style". This is that gate: for every
        covered project it compares the public surface of the built assembly with the
        PublicAPI.Shipped.txt and PublicAPI.Unshipped.txt files committed next to the
        .csproj, and fails when they disagree.

        ⚠ Both directions. An addition nobody wrote down fails, and so does a removal —
        and the second is the reason the gate exists. An unapproved addition is untidy;
        a silent removal is the breaking change that compiles here and breaks somebody
        else, and a baseline that only watched additions would wave it through.

        The comparison itself is src/Trinix.ApiCheck; see its .csproj for why Trinix
        reads the built assembly rather than using the analyser package that shares the
        file format. This script owns exactly one decision the tool does not: which
        projects are covered.

    .PARAMETER Update
        Rewrite each covered project's PublicAPI.Unshipped.txt from what its assembly
        currently contains, instead of failing. Shipped API is never rewritten: a shipped
        entry that has gone becomes a *REMOVED* line, so a break stays visible in the
        diff. Read that diff — an approval nobody looked at approves whatever was there.

    .PARAMETER Fold
        The release ritual: fold Unshipped into Shipped and empty it. Run at a tag, never
        as part of a check. Trinix has released nothing, so every Shipped file is empty
        and every entry lives in Unshipped, which is the honest state.

    .PARAMETER NoBuild
        Skip the build and check whatever is already in out/dotnet. For CI, where the
        build step just ran; not for a developer, where it is how you check a surface
        that no longer exists.

    .EXAMPLE
        ./scripts/check-api.ps1
        Build src/ in Release and compare every covered assembly with its baseline.

    .EXAMPLE
        ./scripts/check-api.ps1 -Update
        Approve what is there now. Then read `git diff` before committing it.
#>
[CmdletBinding()]
param(
    [switch]$Update,
    [switch]$Fold,
    [switch]$NoBuild
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'lib' 'Trinix.Build.psm1') -Force

if ($Update -and $Fold) {
    throw 'check-api.ps1: -Update and -Fold do different things to the same files; pick one.'
}

$root = Get-TrinixRoot
$sourceDirectory = Join-Path $root 'src'
$solution = Join-Path $sourceDirectory 'Trinix.slnx'

# Release, always, and not a parameter. A public surface is a promise about what Trinix
# ships, `dotnet publish` ships Release, and the two configurations need not agree — a
# `public const bool` behind #if DEBUG is enough to make them differ. A gate with two
# possible subjects is a gate that passes on one machine and fails on another.
$configuration = 'Release'

# --- Which projects are covered --------------------------------------------
#
# Covered by default, opted out by declaration — the direction matters. The reverse
# (covered only when a project says so) means a new library is ungated until somebody
# remembers, which is exactly the failure scripts/check-solution.ps1 exists to stop one
# directory up.
#
# The three exclusions:
#   * *.Tests — their surface is xunit's business. Named before the OutputType rule
#     only so the report says "tests"; xunit v3 makes them Exe, so either would catch
#     them, and the name rule is what would still catch one that was not.
#   * OutputType Exe — a program's public members promise nobody anything; nothing
#     compiles against trinixd or the compositor.
#   * <TrinixPublicApi>false</TrinixPublicApi> — an explicit decision, which belongs in
#     the .csproj next to the reason for it rather than in a list over here.
#
# Read through MSBuild rather than by parsing the XML, because OutputType is `Library`
# by *absence* and TrinixPublicApi may be set anywhere in the import chain. Evaluation
# only — no targets are named, so this does not build.
$projects = @(Get-ChildItem -LiteralPath $sourceDirectory -Recurse -Filter '*.csproj' -File | Sort-Object FullName)

if ($projects.Count -eq 0) {
    throw "check-api.ps1: no projects under $sourceDirectory."
}

$covered = @()
$skipped = @()

foreach ($project in $projects) {
    $json = dotnet msbuild $project.FullName `
        -getProperty:TargetPath -getProperty:OutputType -getProperty:TrinixPublicApi `
        -p:Configuration=$configuration 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Host ($json -join [Environment]::NewLine)
        throw "check-api.ps1: could not evaluate $($project.Name)."
    }

    $properties = ($json -join [Environment]::NewLine | ConvertFrom-Json).Properties
    $name = [System.IO.Path]::GetFileNameWithoutExtension($project.Name)

    $reason =
        if ($properties.TrinixPublicApi -eq 'false') { 'TrinixPublicApi=false' }
        elseif ($properties.TrinixPublicApi -eq 'true') { $null }
        elseif ($name.EndsWith('.Tests', [StringComparison]::Ordinal)) { 'tests' }
        elseif ($properties.OutputType -eq 'Exe') { 'a program' }
        else { $null }

    if ($reason) {
        $skipped += [pscustomobject]@{ Name = $name; Reason = $reason }
    } else {
        $covered += [pscustomobject]@{
            Name       = $name
            Project    = $project.FullName
            TargetPath = $properties.TargetPath
        }
    }
}

if ($covered.Count -eq 0) {
    throw 'check-api.ps1: nothing is covered, which cannot be right — the rule above is wrong.'
}

Write-Host "Covered: $(($covered.Name) -join ', ')"
Write-Host "Not covered: $(($skipped | ForEach-Object { "$($_.Name) ($($_.Reason))" }) -join ', ')" -ForegroundColor DarkGray

# --- Build, then check ------------------------------------------------------

if (-not $NoBuild) {
    dotnet build $solution --configuration $configuration --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'check-api.ps1: the build failed, so there is nothing to compare.' }
}

# The checker itself, found the same way as everything else it will read. Running the
# built assembly rather than `dotnet run` keeps this step from quietly rebuilding under
# -NoBuild, which would make -NoBuild a lie.
$checkerProject = Join-Path $sourceDirectory 'Trinix.ApiCheck' 'Trinix.ApiCheck.csproj'
$checker = (dotnet msbuild $checkerProject -getProperty:TargetPath -p:Configuration=$configuration).Trim()

if (-not (Test-Path -LiteralPath $checker)) {
    throw "check-api.ps1: no checker at $checker — build src/Trinix.slnx first, or drop -NoBuild."
}

$arguments = @()
if ($Update) { $arguments += '--update' }
if ($Fold) { $arguments += '--fold' }

foreach ($item in $covered) {
    # ⚠ Not a warning. An assembly that is not where the build put every other one is
    # either a project this script should not have matched or a build that did not
    # happen, and both make the gate pass by checking less than it claims to.
    if (-not (Test-Path -LiteralPath $item.TargetPath)) {
        throw "check-api.ps1: $($item.Name) has no built assembly at $($item.TargetPath)."
    }

    $arguments += $item.Project
    $arguments += $item.TargetPath
}

dotnet $checker @arguments
exit $LASTEXITCODE
