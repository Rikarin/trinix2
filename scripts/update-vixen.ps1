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

        The closure is computed rather than listed. Vixen.Ui.Desktop's ProjectReference
        graph is walked transitively, so a Vixen release that adds an assembly does not
        need a corresponding edit here — which is the failure this would otherwise have:
        a missing package is a restore error at image-build time, hours away.

        Packing runs in the .NET SDK container for the same reason everything else
        does: nothing is installed on the Mac.

    .PARAMETER VixenPath
        The Vixen checkout to pack. Default: ../Vixen beside this repository.

    .PARAMETER AllowDirty
        Pack a checkout with uncommitted changes. Off by default, because the version
        this writes names a commit, and a package built from a working tree that
        commit does not describe is a pin that lies.

    .PARAMETER Verify
        Check that vendor/ matches $(VixenVersion) and change nothing. What CI runs.

    .EXAMPLE
        ./scripts/update-vixen.ps1
        Pack ../Vixen at its HEAD and re-pin to it.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$VixenPath,
    [switch]$AllowDirty,
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

$status = & git -C $VixenPath status --porcelain
if ($status -and -not $AllowDirty) {
    throw "$VixenPath has uncommitted changes. Commit them, or pass -AllowDirty and accept that the pin names a commit these packages were not built from."
}

$sha = (& git -C $VixenPath rev-parse --short=12 HEAD).Trim()
$version = "0.1.0-trinix.$sha"
Write-Host "Vixen $sha -> $version" -ForegroundColor Cyan

# --- The closure -----------------------------------------------------------
#
# Walked from Vixen.Ui.Desktop, which is what a Trinix application's Main calls
# into. ReferenceOutputAssembly="false" references are skipped: those are source
# generators and analysers, which are already inside the packages that use them.
$closure = & {
    $seen = [System.Collections.Generic.HashSet[string]]::new()
    $stack = [System.Collections.Generic.Stack[string]]::new()
    $stack.Push((Join-Path $VixenPath 'Platform' 'Vixen.Ui.Desktop' 'Vixen.Ui.Desktop.csproj'))

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

# --- Pack ------------------------------------------------------------------
$staging = Join-Path ([System.IO.Path]::GetTempPath()) "trinix-vixen-$sha"
if (Test-Path -LiteralPath $staging) { Remove-Item -Recurse -Force -LiteralPath $staging }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

$listPath = Join-Path $staging 'closure.txt'
($closure | ForEach-Object { [System.IO.Path]::GetRelativePath($VixenPath, $_) }) -join "`n" | Set-Content -LiteralPath $listPath

Assert-TrinixDocker

# ContinuousIntegrationBuild, so that the packages do not carry this machine's
# absolute paths in their source-link metadata.
$script = @'
set -e
while read -r project; do
  [ -n "$project" ] || continue
  dotnet pack "/vixen/$project" -c Release --nologo -o /pack \
      -p:Version=__VERSION__ -p:ContinuousIntegrationBuild=true >/dev/null
done < /closure.txt
'@ -replace '__VERSION__', $version

& docker run --rm `
    -v "${VixenPath}:/vixen" `
    -v "${listPath}:/closure.txt:ro" `
    -v "${staging}:/pack" `
    -w /vixen `
    'mcr.microsoft.com/dotnet/sdk:10.0' bash -c $script
if ($LASTEXITCODE -ne 0) { throw "packing Vixen failed." }

# --- Replace the vendored set ----------------------------------------------
#
# Emptied rather than merged: two versions in one folder is a restore that picks
# one of them, and the pin then describes half of what was built.
New-Item -ItemType Directory -Force -Path $vendor | Out-Null
Get-ChildItem -LiteralPath $vendor -Filter '*.nupkg' | Remove-Item -Force

# Symbol packages are half the bytes and none of the build.
$packed = @(Get-ChildItem -LiteralPath $staging -Filter '*.nupkg' | Where-Object { $_.Name -notlike '*.snupkg' })
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
