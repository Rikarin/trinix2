#!/usr/bin/env pwsh
#requires -Version 7.4
<#
    .SYNOPSIS
        Fail if two builds of the same source produce different bundle contents.

    .DESCRIPTION
        docs/plan/16-build-ci-and-testing.md § The gates asks for this one: "two builds of
        the same source produce identical .tdi contents". src/pack-apps.sh already reasons
        about it in prose — read its header — and the distinction it draws is the one this
        script is built on:

          * A .tdi is *not* reproducible and must not be. The bundle inside carries an
            ECDSA signature, which is randomised, and the manifest records when it was
            signed. Two builds of identical source produce two different files.
          * The *contents* are reproducible, and that is the property worth gating. The
            Merkle root over the sealed file list is what pack-apps.sh names as the thing
            to compare when asking whether two builds agree, and it is what this compares.

        So: build the applications twice, seal both, and compare the two manifests. The
        signatures will differ; the roots must not. A difference means something in the
        build read the clock, the filesystem's order, a machine name or a random number —
        and until it is found, "the same source" and "the same application" are not the
        same claim.

        ⚠ What this proves and what it does not. Both builds run from this working tree,
        so it catches nondeterminism in the compiler's inputs, in generated code, in file
        ordering and in anything that embeds the time. It does not prove path
        independence — two different checkouts at two different paths — which is a
        stronger property and needs a second checkout to test. src/Directory.Build.props
        sets Deterministic, and ContinuousIntegrationBuild under CI, which is what should
        make that hold; nothing here has checked it.

        The two builds are given separate ArtifactsPath directories. Without that they
        would share one obj/, the second build would find everything up to date, and the
        gate would compare a directory with itself and always pass — which is the most
        expensive way for a gate to be useless.

    .PARAMETER Arch
        arm64 or x86_64. Which one hardly matters to what is being proved; it is a
        cross-compile either way, and neither needs the target machine.

    .PARAMETER WorkDirectory
        Where to build. Defaults to a new temporary directory, which is removed on
        success and — deliberately — kept on failure, because the two trees are the
        evidence.

    .EXAMPLE
        ./scripts/check-determinism.ps1
        Build, seal and compare. Takes a couple of minutes; needs no Docker and no key.
#>
[CmdletBinding()]
param(
    [ValidateSet('arm64', 'x86_64')]
    [string]$Arch = 'arm64',
    [string]$WorkDirectory
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'lib' 'Trinix.Build.psm1') -Force

$root = Get-TrinixRoot
$packApps = Join-Path $root 'src' 'pack-apps.sh'

if (-not (Test-Path -LiteralPath $packApps)) {
    throw "check-determinism.ps1: no $packApps."
}

$work = if ($WorkDirectory) { $WorkDirectory } else {
    Join-Path ([System.IO.Path]::GetTempPath()) "trinix-determinism-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
}
New-Item -ItemType Directory -Path $work -Force | Out-Null
$work = (Resolve-Path -LiteralPath $work).Path

Write-Host "Building twice under $work"

# --- A throwaway signing identity -------------------------------------------
#
# Its own, in the temporary directory, rather than the machine's signing/local one.
# Two reasons and both matter: Assert-TrinixSigningIdentity creates that identity
# through the build container, and a gate that needs Docker is a gate that does not run
# on the machine that needs it most; and a check should not be able to write to the key
# material a developer signs with. The key here is thrown away with the directory.
$tool = Join-Path $work 'tool'
$certificate = Join-Path $work 'pki' 'identity.pub.pem'
$key = Join-Path $work 'pki' 'identity.key.pem'
$rootCertificate = Join-Path $work 'pki' 'root.pub.pem'
$rootKey = Join-Path $work 'pki' 'root.key.pem'

New-Item -ItemType Directory -Path (Join-Path $work 'pki') -Force | Out-Null

dotnet publish (Join-Path $root 'src' 'Trinix.Bundle.Tool' 'Trinix.Bundle.Tool.csproj') `
    --configuration Release --output $tool --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'check-determinism.ps1: could not build trinix-bundle.' }

$bundleTool = Join-Path $tool 'trinix-bundle'

& $bundleTool pki init --certificate $rootCertificate --key $rootKey --name 'Trinix Determinism Gate Root'
if ($LASTEXITCODE -ne 0) { throw 'check-determinism.ps1: could not create a throwaway root.' }

& $bundleTool pki identity --root-certificate $rootCertificate --root-key $rootKey `
    --certificate $certificate --key $key --name 'Trinix Determinism Gate'
if ($LASTEXITCODE -ne 0) { throw 'check-determinism.ps1: could not create a throwaway identity.' }

# --- Two builds -------------------------------------------------------------

$saved = @{
    ArtifactsPath               = $env:ArtifactsPath
    TRINIX_SIGNING_CERTIFICATE  = $env:TRINIX_SIGNING_CERTIFICATE
    TRINIX_SIGNING_KEY          = $env:TRINIX_SIGNING_KEY
    TRINIX_BUNDLE_TOOL          = $env:TRINIX_BUNDLE_TOOL
}

$runs = @()

try {
    $env:TRINIX_SIGNING_CERTIFICATE = $certificate
    $env:TRINIX_SIGNING_KEY = $key
    $env:TRINIX_BUNDLE_TOOL = $tool

    foreach ($pass in 1, 2) {
        $apps = Join-Path $work "apps$pass"
        # See src/Directory.Build.props: the environment wins, so each pass compiles from
        # scratch instead of finding the other pass's output already up to date.
        $env:ArtifactsPath = Join-Path $work "obj$pass"

        Write-Host ''
        Write-Host "--- build $pass of 2 ---" -ForegroundColor Cyan
        # Through the pipeline rather than straight to the console: the child writes to a
        # pipe when CI captures the log, block-buffers it, and its output would otherwise
        # arrive after the comparison it is supposed to explain.
        bash $packApps --seal-only $Arch $apps 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "check-determinism.ps1: build $pass failed." }

        $runs += $apps
    }
} finally {
    foreach ($name in $saved.Keys) {
        Set-Item -Path "env:$name" -Value $saved[$name] -ErrorAction SilentlyContinue
    }
}

# --- Compare ----------------------------------------------------------------

function Read-Manifest([string]$bundle) {
    $path = Join-Path $bundle 'Contents' '_Signature' 'manifest.json'
    if (-not (Test-Path -LiteralPath $path)) {
        throw "check-determinism.ps1: $bundle was not sealed — no $path."
    }
    return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
}

$first = @(Get-ChildItem -LiteralPath $runs[0] -Directory -Filter '*.app' | Sort-Object Name)
$second = @(Get-ChildItem -LiteralPath $runs[1] -Directory -Filter '*.app' | Sort-Object Name)

if ($first.Count -eq 0) {
    throw "check-determinism.ps1: the first build produced no bundles, so there is nothing to compare."
}

# The set of bundles is itself part of what must be reproducible: a build that sometimes
# packages one application and sometimes two is not a build anyone should be comparing
# the insides of.
$namesA = ($first.Name | Sort-Object) -join ','
$namesB = ($second.Name | Sort-Object) -join ','
if ($namesA -ne $namesB) {
    Write-Host 'The two builds did not even produce the same bundles:' -ForegroundColor Red
    Write-Host "    first:  $namesA"
    Write-Host "    second: $namesB"
    Write-Host "The trees are kept at $work."
    exit 1
}

$differing = 0
$files = 0

foreach ($bundle in $first) {
    $a = Read-Manifest $bundle.FullName
    $b = Read-Manifest (Join-Path $runs[1] $bundle.Name)
    $files += $a.entries.Count

    if ($a.merkleRoot -eq $b.merkleRoot) {
        Write-Host ("  {0,-14} {1} — {2} files, identical" -f $bundle.Name, $a.merkleRoot.Substring(0, 16), $a.entries.Count)
        continue
    }

    $differing++

    Write-Host ''
    Write-Host "$($bundle.Name): the two builds disagree." -ForegroundColor Red
    Write-Host "    merkleRoot  $($a.merkleRoot)"
    Write-Host "                $($b.merkleRoot)"

    $byPathA = @{}
    foreach ($entry in $a.entries) { $byPathA[$entry.path] = $entry }
    $byPathB = @{}
    foreach ($entry in $b.entries) { $byPathB[$entry.path] = $entry }

    foreach ($path in ($byPathA.Keys + $byPathB.Keys | Sort-Object -Unique)) {
        $x = $byPathA[$path]
        $y = $byPathB[$path]

        if ($null -eq $y) { Write-Host "  - $path" -ForegroundColor Red; continue }
        if ($null -eq $x) { Write-Host "  + $path" -ForegroundColor Red; continue }

        if ($x.sha256 -ne $y.sha256) {
            $sizes = if ($x.size -eq $y.size) { "$($x.size) bytes" } else { "$($x.size) → $($y.size) bytes" }
            Write-Host "  ~ $path  $($x.sha256.Substring(0, 12)) → $($y.sha256.Substring(0, 12))  ($sizes)"
        } elseif ($x.executable -ne $y.executable) {
            Write-Host "  ~ $path  executable $($x.executable) → $($y.executable)"
        }
    }
}

Write-Host ''

if ($differing -gt 0) {
    # The failure has to say what to do with it, or it becomes the gate somebody deletes.
    Write-Host "check-determinism.ps1: $differing of $($first.Count) bundles did not reproduce." -ForegroundColor Red
    Write-Host ''
    Write-Host '  Two builds of identical source produced different bundle contents. Something in'
    Write-Host '  the build read the clock, the filesystem order, a machine name or a random number.'
    Write-Host '  A file marked ~ is the place to start: `~` on a .dll usually means a generator or'
    Write-Host '  an embedded resource; on a .deps.json or .runtimeconfig.json it usually means'
    Write-Host '  ordering; on everything at once it usually means a build property that differs.'
    Write-Host ''
    Write-Host "  Both trees are kept at $work — diff the two apps1/apps2 directories."
    Write-Host '  ⚠ The signatures under Contents/_Signature *are* expected to differ, and so is'
    Write-Host '    the manifest''s signedAt. Only merkleRoot and the entries are the promise.'
    exit 1
}

Write-Host ("OK: $($first.Count) bundles, $files files, identical across two builds of the same source.")
Write-Host ''
Write-Host '  The signatures differ, as they must: ECDSA is randomised and the manifest records' -ForegroundColor DarkGray
Write-Host '  when it was signed. See src/pack-apps.sh. The contents are what is reproducible.' -ForegroundColor DarkGray

if (-not $WorkDirectory) {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

exit 0
