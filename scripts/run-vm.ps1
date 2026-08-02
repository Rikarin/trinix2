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

    .PARAMETER LoginCheck
        Everything -Check does, and then log in and drive the session: assert that
        it lands in PowerShell, that `dotnet --version` works, that Trinix's own
        module loaded, and that the C# system service is running. The Phase 3 exit
        criteria as a test.

    .PARAMETER GraphicsCheck
        Everything -Check does, and then start a Wayland client under the C#
        compositor and read the verdict out of the journal: did the compositor
        find a display, did a client that knows nothing about it get a window on
        that display. The Phase 4 exit criterion as a test.

    .PARAMETER AppCheck
        Everything -Check does, and then install a signed .tdi distribution image,
        launch the application inside it, tamper with the installed bundle and
        assert that it stops launching. The Phase 6 exit criterion as a test.

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
    [switch]$LoginCheck,
    [switch]$GraphicsCheck,
    [switch]$AppCheck,
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

$unattended = $Check -or $LoginCheck -or $GraphicsCheck -or $AppCheck

$dockerArgs = @('run', '--rm')

if ($unattended) {
    # No TTY: the console is redirected to a log file that the container writes
    # into the mounted image directory, so it survives for inspection afterwards.
    $dockerArgs += @('--init')
} else {
    $dockerArgs += @('--interactive', '--tty')
}

$dockerArgs += @('--volume', "${imageDirPath}:/images", $vmImage, $Arch)

if ($AppCheck) {
    $dockerArgs += '--app-check'
} elseif ($GraphicsCheck) {
    $dockerArgs += '--graphics-check'
} elseif ($LoginCheck) {
    $dockerArgs += '--login-check'
} elseif ($Check) {
    $dockerArgs += '--check'
}
if ($unattended -and $Timeout -gt 0) { $dockerArgs += "$Timeout" }

if ($AppCheck) {
    Write-Host "Booting trinix-$Arch.img and installing, launching and tampering with a signed application..." -ForegroundColor Cyan
} elseif ($GraphicsCheck) {
    Write-Host "Booting trinix-$Arch.img and running a Wayland client under the compositor..." -ForegroundColor Cyan
} elseif ($LoginCheck) {
    Write-Host "Booting trinix-$Arch.img, logging in, and checking PowerShell and .NET..." -ForegroundColor Cyan
} elseif ($Check) {
    Write-Host "Booting trinix-$Arch.img and waiting for a login prompt..." -ForegroundColor Cyan
} else {
    Write-Host "Booting trinix-$Arch.img. Ctrl-a x quits; log in as trinix (PowerShell) or root (bash)." -ForegroundColor Cyan
}

Invoke-TrinixDocker @dockerArgs

if (-not $unattended) {
    <#
        Drain whatever the terminal said back.

        An interactive session hands this terminal to the guest, and the guest
        asks it questions — PSReadLine wants the cursor position, terminfo wants
        the device attributes. The terminal answers, but by the time it does,
        QEMU has exited and nothing is reading its input any more. The replies
        sit in the tty buffer until the shell that ran this script reads them as
        a command line, and because a device-attributes reply contains '>', what
        that shell does with them is create a file whose name is the rest of the
        answer. Four of them appeared in the repository root before anyone
        worked out where they came from.

        Reading until the terminal has been quiet for a moment consumes the
        replies before the prompt can.
    #>
    if (-not [Console]::IsInputRedirected) {
        try {
            $quietUntil = [DateTime]::UtcNow.AddMilliseconds(250)
            while ([DateTime]::UtcNow -lt $quietUntil) {
                if ([Console]::KeyAvailable) {
                    [void][Console]::ReadKey($true)
                    $quietUntil = [DateTime]::UtcNow.AddMilliseconds(250)
                } else {
                    Start-Sleep -Milliseconds 20
                }
            }
        } catch [System.InvalidOperationException] {
            # No console to drain — nothing was handed over, so nothing replied.
        }
    }
}

if ($unattended) {
    Write-Host ''
    Write-Host "Boot check passed for $Arch." -ForegroundColor Green
    Write-Host "  console log: $(Join-Path $imageDirPath "serial-$Arch.log")"
}
