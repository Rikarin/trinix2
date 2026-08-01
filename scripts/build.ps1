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
          llvm        Phase 1  the single Clang/LLD install, shared by all targets
          toolchain   Phase 1  per-arch sysroot: headers, mini-GCC, glibc, LLVM runtimes
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
    [ValidateSet('host-tools', 'llvm', 'toolchain', 'base', 'image', 'all')]
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

if ($Stage -contains 'all') { $Stage = @('host-tools', 'llvm', 'toolchain', 'base', 'image') }

Assert-TrinixDocker

$outputDir = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }

function Get-CommonBuildArgs {
    param(
        [Parameter(Mandatory)][string]$Target,
        [string]$Dockerfile = 'host-tools.Dockerfile'
    )
    $result = @(
        'buildx', 'build',
        '--file', (Join-Path $root 'docker' $Dockerfile),
        '--target', $Target,
        '--progress', $Progress
    )
    if ($NoCache) { $result += '--no-cache' }
    return $result
}

# Sources already downloaded on the host (or restored from CI's cache) are handed
# to BuildKit as a named context, so a toolchain build does not refetch ~600 MB.
# Everything is still digest-verified inside the container either way.
function Get-SourcesContextArg {
    $cache = Get-TrinixSourceCache
    return @('--build-context', "sources=$cache")
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

# --- Stage: llvm (Phase 1, architecture-independent) ------------------------

# base/sources.json is baked into the host-tools image, and every later stage
# resolves its pins from that copy. Rebuilding host-tools first (a no-op when
# nothing changed) is what makes a version bump actually reach the build instead
# of silently compiling the previous pin.
function Assert-HostToolsCurrent {
    $dockerArgs = Get-CommonBuildArgs -Target 'host-tools'
    $dockerArgs += @('--tag', "$ImagePrefix/host-tools:$Tag", '--load', $root)
    Invoke-TrinixDocker @dockerArgs
}

function Build-Llvm {
    Assert-HostToolsCurrent
    $image = "$ImagePrefix/llvm:$Tag"

    $dockerArgs = Get-CommonBuildArgs -Target 'llvm' -Dockerfile 'toolchain.Dockerfile'
    $dockerArgs += Get-SourcesContextArg
    $dockerArgs += @('--build-arg', "HOST_TOOLS_IMAGE=$ImagePrefix/host-tools:$Tag")
    $dockerArgs += @('--tag', $image, '--load', $root)

    Invoke-TrinixDocker @dockerArgs

    Write-Host ''
    Write-Host "Clang/LLD ready: $image" -ForegroundColor Green
}

# --- Stage: toolchain (Phase 1, per architecture) ---------------------------

function Build-Toolchain {
    param([Parameter(Mandatory)][psobject]$Architecture)

    Assert-HostToolsCurrent
    $image = "$ImagePrefix/toolchain-$($Architecture.Name):$Tag"
    $target = if ($Verify) { 'toolchain-verify' } else { 'toolchain' }

    $dockerArgs = Get-CommonBuildArgs -Target $target -Dockerfile 'toolchain.Dockerfile'
    $dockerArgs += Get-SourcesContextArg
    $dockerArgs += @(
        '--build-arg', "HOST_TOOLS_IMAGE=$ImagePrefix/host-tools:$Tag",
        '--build-arg', "TRINIX_ARCH=$($Architecture.Name)",
        '--tag', $image, '--load', $root
    )

    Invoke-TrinixDocker @dockerArgs

    Write-Host ''
    Write-Host "Sysroot ready: $image ($($Architecture.Triple))" -ForegroundColor Green
}

# --- Stage: base (Phase 2, per architecture) --------------------------------

function Build-Base {
    param([Parameter(Mandatory)][psobject]$Architecture)

    $toolchainImage = "$ImagePrefix/toolchain-$($Architecture.Name):$Tag"
    $image = "$ImagePrefix/base-$($Architecture.Name):$Tag"
    $target = if ($Verify) { 'base-verify' } else { 'base' }

    $dockerArgs = Get-CommonBuildArgs -Target $target -Dockerfile 'base.Dockerfile'
    $dockerArgs += Get-SourcesContextArg
    $dockerArgs += @(
        '--build-arg', "TOOLCHAIN_IMAGE=$toolchainImage",
        '--build-arg', "TRINIX_ARCH=$($Architecture.Name)",
        '--tag', $image, '--load', $root
    )

    Invoke-TrinixDocker @dockerArgs

    Write-Host ''
    Write-Host "Base rootfs ready: $image ($($Architecture.Triple))" -ForegroundColor Green
}

# --- Stage: image (Phase 2, per architecture) -------------------------------

function Build-Image {
    param([Parameter(Mandatory)][psobject]$Architecture)

    $baseImage = "$ImagePrefix/base-$($Architecture.Name):$Tag"

    # The gate runs as its own target, then the export target extracts the
    # image. Two invocations rather than one because BuildKit exports from a
    # single target only — the second is a cache hit apart from the copy.
    if ($Verify) {
        $verifyArgs = Get-CommonBuildArgs -Target 'image-verify' -Dockerfile 'image.Dockerfile'
        $verifyArgs += @(
            '--build-arg', "BASE_IMAGE=$baseImage",
            '--build-arg', "TRINIX_ARCH=$($Architecture.Name)",
            $root
        )
        Invoke-TrinixDocker @verifyArgs
    }

    $dockerArgs = Get-CommonBuildArgs -Target 'image-export' -Dockerfile 'image.Dockerfile'
    $dockerArgs += @(
        '--build-arg', "BASE_IMAGE=$baseImage",
        '--build-arg', "TRINIX_ARCH=$($Architecture.Name)",
        '--output', "type=local,dest=$outputDir",
        $root
    )
    Invoke-TrinixDocker @dockerArgs

    $image = Join-Path $outputDir "trinix-$($Architecture.Name).img"
    Write-Host ''
    Write-Host "Disk image ready: $image" -ForegroundColor Green
    Write-Host "  boot it:  ./scripts/run-vm.ps1 -Arch $($Architecture.Name)"
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
        'llvm' {
            # One Clang install serves every target — no per-arch variant exists.
            Build-Llvm
            $summary.Add([pscustomobject]@{ Stage = $stageName; Arch = 'all targets'; Result = 'built' })
        }
        'toolchain' {
            foreach ($a in $architectures) {
                Build-Toolchain -Architecture $a
                $summary.Add([pscustomobject]@{ Stage = $stageName; Arch = $a.Name; Result = 'built' })
            }
        }
        'base' {
            foreach ($a in $architectures) {
                Build-Base -Architecture $a
                $summary.Add([pscustomobject]@{ Stage = $stageName; Arch = $a.Name; Result = 'built' })
            }
        }
        'image' {
            foreach ($a in $architectures) {
                Build-Image -Architecture $a
                $summary.Add([pscustomobject]@{ Stage = $stageName; Arch = $a.Name; Result = 'built' })
            }
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
