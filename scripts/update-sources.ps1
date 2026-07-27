#!/usr/bin/env pwsh
#requires -Version 7.4
<#
    .SYNOPSIS
        Download every pinned upstream tarball, compute its sha256, and write it back
        into base/sources.json.

    .DESCRIPTION
        This is the tool that keeps the "nothing unpinned" rule enforceable. Downloads
        land in .cache/sources/ (gitignored) and are reused by later runs and by the
        Docker build stages.

        Entries whose sha256 is already set are skipped unless -Force is given, so the
        normal workflow after bumping a version is simply:

            ./scripts/update-sources.ps1 -Name llvm-project -Force

    .PARAMETER Name
        Only process these sources (wildcards allowed). Default: all.

    .PARAMETER Phase
        Only process sources belonging to these build phases.

    .PARAMETER Force
        Re-download and re-hash even if a checksum is already recorded.

    .PARAMETER Jobs
        Parallel downloads. Default 4 — be kind to ftp.gnu.org.

    .PARAMETER Verify
        Verify recorded checksums instead of writing them; exits non-zero on mismatch.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string[]]$Name,
    [int[]]$Phase,
    [switch]$Force,
    [ValidateRange(1, 16)][int]$Jobs = 4,
    [switch]$Verify
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'lib' 'Trinix.Build.psm1') -Force

$cache = Get-TrinixSourceCache
$sources = @(Get-TrinixSource -Name $Name -Phase $Phase)

if (-not $Verify -and -not $Force) {
    $sources = @($sources | Where-Object { -not $_.Sha256 })
}

if ($sources.Count -eq 0) {
    Write-Host 'Nothing to do — every selected source already has a checksum (use -Force to recompute).'
    return
}

Write-Host "Fetching $($sources.Count) source archive(s) into $cache with $Jobs parallel job(s)..." -ForegroundColor Cyan

$results = $sources | ForEach-Object -ThrottleLimit $Jobs -Parallel {
    $source = $_
    $cacheDir = $using:cache
    $target = Join-Path $cacheDir $source.FileName

    $status = 'ok'
    $failReason = $null

    try {
        if (-not (Test-Path -LiteralPath $target) -or (Get-Item -LiteralPath $target).Length -eq 0) {
            # --location follows redirects (GitHub, kernel.org CDN); --fail turns HTTP
            # errors into a non-zero exit instead of a saved error page; the speed
            # limit aborts a stalled transfer so --retry can actually retry it
            # (ftp.gnu.org will happily hold a dead connection open for hours).
            $curlArgs = @(
                '--silent', '--show-error', '--fail', '--location',
                '--retry', '3', '--retry-delay', '2', '--retry-all-errors',
                '--connect-timeout', '20', '--speed-limit', '2048', '--speed-time', '30',
                '--output', "$target.partial"
            )

            # Resume a partial left behind by an interrupted run; if the server
            # cannot honour the range, start over rather than corrupt the file.
            $attempts = if (Test-Path -LiteralPath "$target.partial") { @('resume', 'fresh') } else { @('fresh') }

            $downloaded = $false
            foreach ($attempt in $attempts) {
                if ($attempt -eq 'fresh') { Remove-Item -LiteralPath "$target.partial" -ErrorAction SilentlyContinue }
                $thisRun = if ($attempt -eq 'resume') { @('--continue-at', '-') + $curlArgs } else { $curlArgs }

                $curlOutput = & curl @thisRun $source.Url 2>&1
                if ($LASTEXITCODE -eq 0) { $downloaded = $true; break }
            }

            if (-not $downloaded) {
                Remove-Item -LiteralPath "$target.partial" -ErrorAction SilentlyContinue
                throw ("download failed (curl exit $LASTEXITCODE): " + ($curlOutput -join ' ')).Trim()
            }
            Move-Item -LiteralPath "$target.partial" -Destination $target -Force
        }
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    catch {
        $status = 'failed'
        $failReason = $_.Exception.Message
        $hash = $null
    }

    [pscustomobject]@{
        Name     = $source.Name
        Version  = $source.Version
        Url      = $source.Url
        Expected = $source.Sha256
        Actual   = $hash
        Bytes    = if ($hash) { (Get-Item -LiteralPath $target).Length } else { 0 }
        Status   = $status
        Error    = $failReason
    }
}

$failed = @($results | Where-Object Status -EQ 'failed')
$ok = @($results | Where-Object Status -EQ 'ok')

foreach ($r in $ok | Sort-Object Name) {
    $size = '{0,8:N1} MiB' -f ($r.Bytes / 1MB)
    Write-Host ("  {0,-16} {1,-10} {2} {3}" -f $r.Name, $r.Version, $size, $r.Actual)
}

if ($Verify) {
    $mismatched = @($ok | Where-Object { $_.Expected -and $_.Expected -ne $_.Actual })
    $unpinned = @($ok | Where-Object { -not $_.Expected })
    foreach ($m in $mismatched) {
        Write-Host "CHECKSUM MISMATCH  $($m.Name) $($m.Version)" -ForegroundColor Red
        Write-Host "  expected $($m.Expected)"
        Write-Host "  actual   $($m.Actual)"
    }
    foreach ($u in $unpinned) { Write-Host "UNPINNED  $($u.Name) has no recorded sha256" -ForegroundColor Yellow }
    foreach ($f in $failed) { Write-Host "UNREACHABLE  $($f.Name): $($f.Error)" -ForegroundColor Red }

    if ($mismatched.Count -or $unpinned.Count -or $failed.Count) { exit 1 }
    Write-Host "All $($ok.Count) pinned source(s) verified." -ForegroundColor Green
    return
}

$checksums = @{}
foreach ($r in $ok) { $checksums[$r.Name] = $r.Actual }

$changed = Set-TrinixSourceChecksum -Checksums $checksums
Write-Host "Updated $changed checksum(s) in $(Get-TrinixSourceManifestPath)." -ForegroundColor Green

if ($failed.Count -gt 0) {
    Write-Host ''
    Write-Host "$($failed.Count) source(s) could not be fetched — the pinned version or URL is probably wrong:" -ForegroundColor Yellow
    foreach ($f in $failed | Sort-Object Name) {
        Write-Host "  $($f.Name) $($f.Version)" -ForegroundColor Yellow
        Write-Host "    $($f.Url)"
        Write-Host "    $($f.Error)"
    }
    exit 1
}
