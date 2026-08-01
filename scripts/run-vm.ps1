#!/usr/bin/env pwsh
#requires -Version 7.4
<#
    .SYNOPSIS
        Boot a built Trinix image in QEMU.

    .DESCRIPTION
        Docker cannot boot a kernel, so this is the other half of Trinix's testing
        story — and QEMU itself runs from a container (docker/vm.Dockerfile), so the
        host still needs nothing but Docker Desktop. UTM on the host is nicer for
        graphical work once Phase 4 has a compositor; it stays optional.

        The VM has no accelerator: Docker Desktop's Linux VM does not pass through
        virtualisation, so this is TCG emulation even when the guest architecture
        matches the Mac's. Booting to a login prompt takes a couple of minutes on
        arm64 and considerably longer for x86_64.

    .PARAMETER Arch
        Which image to boot. Defaults to the host's own architecture, which is the
        one that boots fastest.

    .PARAMETER Check
        Boot unattended and assert that a login prompt appears, instead of handing
        over an interactive console. This is the Phase 2 exit criterion as a test,
        and what CI runs.

    .PARAMETER Timeout
        Seconds to wait in -Check mode before giving up. Default: the per-architecture
        value in image/scripts/run-qemu.sh.

    .PARAMETER ImageDir
        Where trinix-<arch>.img lives. Default: ./out, which is where
        `build.ps1 -Stage image` puts it.

    .PARAMETER Rebuild
        Rebuild the QEMU container from scratch. It is rebuilt (from cache) on
        every run regardless; this forces --no-cache.

    .EXAMPLE
        ./scripts/run-vm.ps1 -Arch arm64
        Interactive serial console. Ctrl-a x quits, Ctrl-a c reaches the QEMU monitor.

    .EXAMPLE
        ./scripts/run-vm.ps1 -Arch arm64 -Check
        Boots, waits for the login prompt, and exits non-zero if it never arrives.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('arm64', 'x86_64')]
    [string]$Arch,

    [switch]$Check,
    [int]$Timeout = 0,

    [string]$ImageDir = 'out',

    [switch]$Rebuild,
    [string]$ImagePrefix = 'trinix',
    [string]$Tag = 'dev'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'lib' 'Trinix.Build.psm1') -Force

$root = Get-TrinixRoot
Assert-TrinixDocker

if (-not $Arch) {
    # An arm64 guest on an arm64 host is still emulated, but it avoids
    # translating between instruction sets on top of that.
    $Arch = if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') { 'arm64' } else { 'x86_64' }
    Write-Host "No -Arch given; booting $Arch (the host's own architecture)." -ForegroundColor DarkGray
}

$imageDirPath = if ([System.IO.Path]::IsPathRooted($ImageDir)) { $ImageDir } else { Join-Path $root $ImageDir }
$imagePath = Join-Path $imageDirPath "trinix-$Arch.img"

if (-not (Test-Path -LiteralPath $imagePath)) {
    throw @"
No image at $imagePath.
  Build one first:  ./scripts/build.ps1 -Stage image -Arch $Arch
"@
}

# --- The QEMU container ----------------------------------------------------

$vmImage = "$ImagePrefix/vm:$Tag"

# Built every time rather than only when missing. It is a cache hit in about a
# second once the apt layer exists, and the alternative is that an edit to
# run-qemu.sh silently does not take effect — which costs far more than the
# second it saves.
$buildArgs = @(
    'buildx', 'build',
    '--file', (Join-Path $root 'docker' 'vm.Dockerfile'),
    '--target', 'vm',
    '--tag', $vmImage,
    '--load',
    '--progress', 'quiet'
)
if ($Rebuild) { $buildArgs += '--no-cache' }
$buildArgs += $root

Invoke-TrinixDocker @buildArgs

# --- Boot ------------------------------------------------------------------

$dockerArgs = @('run', '--rm')

if ($Check) {
    # No TTY: the console is redirected to a log file that the container writes
    # into the mounted image directory, so it survives for inspection afterwards.
    $dockerArgs += @('--init')
} else {
    $dockerArgs += @('--interactive', '--tty')
}

$dockerArgs += @('--volume', "${imageDirPath}:/images", $vmImage, $Arch)
if ($Check) {
    $dockerArgs += '--check'
    if ($Timeout -gt 0) { $dockerArgs += "$Timeout" }
}

if ($Check) {
    Write-Host "Booting trinix-$Arch.img and waiting for a login prompt..." -ForegroundColor Cyan
} else {
    Write-Host "Booting trinix-$Arch.img. Ctrl-a x quits; log in as root." -ForegroundColor Cyan
}

Invoke-TrinixDocker @dockerArgs

if ($Check) {
    Write-Host ''
    Write-Host "Boot check passed for $Arch." -ForegroundColor Green
    Write-Host "  console log: $(Join-Path $imageDirPath "serial-$Arch.log")"
}
