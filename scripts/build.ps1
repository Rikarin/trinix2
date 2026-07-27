#!/usr/bin/env pwsh
#requires -Version 7.4
<#
    .SYNOPSIS
        The Trinix build orchestrator: builds any stage, for any target architecture,
        entirely inside Docker.

    .DESCRIPTION
        Nothing is compiled on the host. Every stage is a BuildKit target in one of the
        Dockerfiles under docker/, and the build container is always native to the host —
        cross-compilation to aarch64 and x86_64 happens *inside* it via
        `clang --target=<triple>`, which is both faster and less arch-specific than
        running emulated foreign containers.

        Stages:
          host-tools  Phase 0  the build container itself
          toolchain   Phase 1  LLVM/Clang/LLD + mini-GCC + glibc sysroot per arch
          base        Phase 2  cross-built base system into a clean rootfs
          image       Phase 2  bootable, signed A/B disk image

    .PARAMETER Stage
        Stages to build, in order. 'all' expands to the full chain.

    .PARAMETER Arch
        Target architecture: arm64, x86_64, or both (default).

    .PARAMETER Output
        Host directory that stage artifacts are exported to. Default: ./out

    .PARAMETER Verify
        Also run the stage's acceptance gate (a `*-verify` Dockerfile target), so the
        build fails if the stage's exit criteria are not met.

    .PARAMETER NoCache
        Pass --no-cache to BuildKit.

    .PARAMETER Progress
        BuildKit progress style: auto, plain, tty. Use plain in CI and when debugging.

    .EXAMPLE
        ./scripts/build.ps1 -Stage host-tools -Verify
        Builds the build container and asserts it can cross-build for both arches.

    .EXAMPLE
        ./scripts/build.ps1 -Stage all -Arch arm64
        Full chain for arm64 only.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('host-tools', 'toolchain', 'base', 'image', 'all')]
    [string[]]$Stage = @('host-tools'),

    [ValidateSet('arm64', 'x86_64', 'both')]
    [string]$Arch = 'both',

    [string]$Output = 'out',

    [switch]$Verify,
    [switch]$NoCache,

    [ValidateSet('auto', 'plain', 'tty')]
    [string]$Progress = 'auto',

    [string]$ImagePrefix = 'trinix',
    [string]$Tag = 'dev'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'lib' 'Trinix.Build.psm1') -Force

$root = Get-TrinixRoot
$architectures = Get-TrinixArch -Name $Arch

if ($Stage -contains 'all') { $Stage = @('host-tools', 'toolchain', 'base', 'image') }

Assert-TrinixDocker

$outputDir = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }

function Get-CommonBuildArgs {
    param([string]$Target)
    $result = @(
        'buildx', 'build',
        '--file', (Join-Path $root 'docker' 'host-tools.Dockerfile'),
        '--target', $Target,
        '--progress', $Progress
    )
    if ($NoCache) { $result += '--no-cache' }
    return $result
}

# --- Stage: host-tools (Phase 0) -------------------------------------------

function Build-HostTools {
    $target = if ($Verify) { 'host-tools-verify' } else { 'host-tools' }
    $image = "$ImagePrefix/host-tools:$Tag"

    $dockerArgs = Get-CommonBuildArgs -Target $target
    $dockerArgs += @('--tag', $image)
    # The build container is intentionally native: no --platform.
    if ($target -eq 'host-tools') { $dockerArgs += '--load' }
    $dockerArgs += $root

    Invoke-TrinixDocker @dockerArgs

    if ($target -eq 'host-tools-verify') {
        # The verify stage runs the gate during build; also build+load the plain
        # image so the developer is left with something usable.
        $plain = Get-CommonBuildArgs -Target 'host-tools'
        $plain += @('--tag', $image, '--load', $root)
        Invoke-TrinixDocker @plain
    }

    Write-Host ''
    Write-Host "Build container ready: $image" -ForegroundColor Green
    Write-Host "  interactive shell:  docker run --rm -it -v `"$root`:/work`" $image bash"
    Write-Host "  tool inventory:     docker run --rm $image"
}

# --- Stages not yet implemented -------------------------------------------

function Assert-NotYetImplemented {
    param([string]$StageName, [int]$Phase, [string]$Blurb)
    throw @"
Stage '$StageName' is not implemented yet — it is Phase $Phase work.
  $Blurb
See IMPLEMENTATION_PLAN.md, section 4.
"@
}

# --- Dispatch -------------------------------------------------------------

$summary = [System.Collections.Generic.List[object]]::new()

foreach ($stageName in $Stage) {
    Write-Host ''
    Write-Host "### Stage: $stageName" -ForegroundColor Yellow

    switch ($stageName) {
        'host-tools' {
            # Arch-independent: one native container cross-builds everything.
            Build-HostTools
            $summary.Add([pscustomobject]@{ Stage = $stageName; Arch = 'native'; Result = 'built' })
        }
        'toolchain' {
            Assert-NotYetImplemented -StageName $stageName -Phase 1 `
                -Blurb 'Builds LLVM/Clang/LLD once, then per arch: linux headers -> mini-GCC -> glibc -> compiler-rt/libunwind/libc++.'
        }
        'base' {
            Assert-NotYetImplemented -StageName $stageName -Phase 2 `
                -Blurb 'Cross-builds the base recipe set (kernel, systemd, dash, ...) into a clean rootfs.'
        }
        'image' {
            Assert-NotYetImplemented -StageName $stageName -Phase 2 `
                -Blurb 'Assembles GPT + ESP (systemd-boot) + root A/B + /data and signs the result.'
        }
    }
}

Write-Host ''
Write-Host 'Targets requested:' -ForegroundColor Cyan
foreach ($a in $architectures) {
    Write-Host ("  {0,-8} {1,-28} kernel ARCH={2}" -f $a.Name, $a.Triple, $a.KernelArch)
}
Write-Host ''
$summary | Format-Table -AutoSize
Write-Host "Artifacts directory: $outputDir"
