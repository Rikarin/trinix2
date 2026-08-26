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
          app         Phase 6  signed .app bundles, packaged as .tdi images
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
    [ValidateSet('host-tools', 'llvm', 'toolchain', 'app', 'base', 'image', 'all')]
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

# ⚠ -WhatIf does not survive the module boundary, and the failure is silent and expensive.
#
# Preference variables are scoped, and a function exported from a module runs in the
# module's scope rather than this script's — so $WhatIfPreference, set for us by
# [CmdletBinding(SupportsShouldProcess)] above, is simply not visible to
# Invoke-TrinixDocker. Its ShouldProcess guard is written correctly and was being asked
# the wrong question: `build.ps1 -Stage base -WhatIf` ran a real multi-hour build.
#
# Bridged here, once, rather than by threading -WhatIf:$WhatIfPreference through ~14 call
# sites where the fourteenth would eventually be forgotten. Keyed on the verb-noun prefix
# so it reaches every Trinix cmdlet the module exports, present and future.
if ($WhatIfPreference) {
    $PSDefaultParameterValues['*-Trinix*:WhatIf'] = $true
}

$root = Get-TrinixRoot
$architectures = Get-TrinixArch -Name $Arch

if ($Stage -contains 'all') { $Stage = @('host-tools', 'llvm', 'toolchain', 'app', 'base', 'image') }

Assert-TrinixDocker

$outputDir = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }

function Get-CommonBuildArgs {
    param(
        [Parameter(Mandatory)][string]$Target,
        [string]$Dockerfile = 'host-tools.Dockerfile',

        # The stage whose provenance this build should stamp into the image it
        # produces: the hash of the Dockerfile above, and the identity of the
        # image it is built FROM. Omitted for the export-only targets, which
        # write a local directory from `scratch` and leave no image to label.
        [string]$Stage,
        [string]$Architecture
    )
    $result = @(
        'buildx', 'build',
        '--file', (Join-Path $root 'docker' $Dockerfile),
        '--target', $Target,
        '--progress', $Progress
    )
    if ($NoCache) { $result += '--no-cache' }
    if ($Stage) {
        $result += Get-TrinixStageLabelArgs -Name $Stage -ImagePrefix $ImagePrefix -Tag $Tag -Arch $Architecture
    }
    return $result
}

# Sources already downloaded on the host (or restored from CI's cache) are handed
# to BuildKit as a named context, so a toolchain build does not refetch ~600 MB.
# Everything is still digest-verified inside the container either way.
function Get-SourcesContextArg {
    $cache = Get-TrinixSourceCache
    return @('--build-context', "sources=$cache")
}

# The roots a built image will accept signatures from, staged into one directory
# and handed to BuildKit as a named context. Recomputed on every build rather
# than cached: adding a trust anchor should take effect on the next build, and
# a stale trust store is a failure whose symptom (an application refusing to
# launch) points nowhere near its cause.
function Get-TrustContextArg {
    $staged = Update-TrinixTrustStore -OutputDirectory $outputDir
    return @('--build-context', "trust=$staged")
}

# Where the app stage's output lands, and where the base stage picks it up.
function Get-AppsDirectory {
    $apps = Join-Path $outputDir 'apps'
    if (-not (Test-Path $apps)) { New-Item -ItemType Directory -Path $apps -Force | Out-Null }
    return $apps
}

# --- Stage: host-tools (Phase 0) -------------------------------------------

function Build-HostTools {
    $target = if ($Verify) { 'host-tools-verify' } else { 'host-tools' }
    $image = "$ImagePrefix/host-tools:$Tag"

    $dockerArgs = Get-CommonBuildArgs -Target $target -Stage 'host-tools'
    $dockerArgs += @('--tag', $image)
    # The build container is intentionally native: no --platform.
    if ($target -eq 'host-tools') { $dockerArgs += '--load' }
    $dockerArgs += $root

    Invoke-TrinixDocker @dockerArgs

    if ($target -eq 'host-tools-verify') {
        # The verify stage runs the gate during build; also build+load the plain
        # image so the developer is left with something usable.
        $plain = Get-CommonBuildArgs -Target 'host-tools' -Stage 'host-tools'
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
#
# What this is *not* is a staleness check for anything downstream — that was the
# confidence it could not deliver, and Assert-TrinixStageCurrent is now the thing
# that delivers it. Its role here is narrower and load-bearing: the gate judges a
# cached toolchain against the host-tools image it was built on, and that
# comparison is worthless if host-tools is itself out of date. It also covers
# what a Dockerfile hash cannot see — base/sources.json, docker/scripts/*,
# toolchain/cmake, toolchain/meson are COPYed in, and BuildKit is the only thing
# that knows whether they moved. A cache hit re-exports byte-identical layers, so
# a no-op rebuild leaves every descendant's recorded base content id matching.
function Assert-HostToolsCurrent {
    $dockerArgs = Get-CommonBuildArgs -Target 'host-tools' -Stage 'host-tools'
    $dockerArgs += @('--tag', "$ImagePrefix/host-tools:$Tag", '--load', $root)
    Invoke-TrinixDocker @dockerArgs
}

function Build-Llvm {
    Assert-HostToolsCurrent
    Assert-TrinixStageCurrent -Name 'llvm' -ImagePrefix $ImagePrefix -Tag $Tag
    $image = "$ImagePrefix/llvm:$Tag"

    $dockerArgs = Get-CommonBuildArgs -Target 'llvm' -Dockerfile 'toolchain.Dockerfile' -Stage 'llvm'
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
    Assert-TrinixStageCurrent -Name 'toolchain' -ImagePrefix $ImagePrefix -Tag $Tag -Arch $Architecture.Name
    $image = "$ImagePrefix/toolchain-$($Architecture.Name):$Tag"
    $target = if ($Verify) { 'toolchain-verify' } else { 'toolchain' }

    $dockerArgs = Get-CommonBuildArgs -Target $target -Dockerfile 'toolchain.Dockerfile' -Stage 'toolchain' -Architecture $Architecture.Name
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

# --- Stage: app (Phase 6, per architecture) ---------------------------------

function Build-App {
    param([Parameter(Mandatory)][psobject]$Architecture)

    Assert-HostToolsCurrent
    Assert-TrinixStageCurrent -Name 'app' -ImagePrefix $ImagePrefix -Tag $Tag -Arch $Architecture.Name
    Assert-TrinixSigningIdentity -HostToolsImage "$ImagePrefix/host-tools:$Tag"
    $identity = Get-TrinixSigningIdentity

    $apps = Join-Path (Get-AppsDirectory) $Architecture.Name

    # The gate is its own target, run before the export, the same way the image
    # stage does it: BuildKit exports from a single target only, and the export
    # is then a cache hit apart from the copy.
    if ($Verify) {
        $verifyArgs = Get-CommonBuildArgs -Target 'app-verify' -Dockerfile 'app.Dockerfile'
        $verifyArgs += Get-TrustContextArg
        $verifyArgs += @(
            '--build-arg', "HOST_TOOLS_IMAGE=$ImagePrefix/host-tools:$Tag",
            '--build-arg', "TRINIX_ARCH=$($Architecture.Name)",
            '--secret', "id=trinix-signing-certificate,src=$($identity.Certificate)",
            '--secret', "id=trinix-signing-key,src=$($identity.Key)",
            $root
        )
        Invoke-TrinixDocker @verifyArgs
    }

    $dockerArgs = Get-CommonBuildArgs -Target 'app-export' -Dockerfile 'app.Dockerfile'
    $dockerArgs += Get-TrustContextArg
    $dockerArgs += @(
        '--build-arg', "HOST_TOOLS_IMAGE=$ImagePrefix/host-tools:$Tag",
        '--build-arg', "TRINIX_ARCH=$($Architecture.Name)",
        '--secret', "id=trinix-signing-certificate,src=$($identity.Certificate)",
        '--secret', "id=trinix-signing-key,src=$($identity.Key)",
        '--output', "type=local,dest=$apps",
        $root
    )
    Invoke-TrinixDocker @dockerArgs

    Write-Host ''
    Write-Host "Applications packaged: $apps" -ForegroundColor Green
    Get-ChildItem -Path $apps -Filter '*.tdi' -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host ("  {0,-24} {1,10:N0} bytes" -f $_.Name, $_.Length) }
}

# --- Stage: base (Phase 2, per architecture) --------------------------------

function Build-Base {
    param([Parameter(Mandatory)][psobject]$Architecture)

    Assert-TrinixDiskSpace -Stage "base ($($Architecture.Name))"

    # host-tools first, the gate second, and the order is the point: the gate
    # judges the cached toolchain against the host-tools image it was built on,
    # and that comparison says nothing if host-tools is itself behind the tree.
    Assert-HostToolsCurrent
    Assert-TrinixStageCurrent -Name 'base' -ImagePrefix $ImagePrefix -Tag $Tag -Arch $Architecture.Name

    # The base image carries both the trust store and the reference application,
    # so a base built before the applications exist would boot a system with
    # nothing to install. Built here for the same reason host-tools is: a cache
    # hit when nothing changed, and the alternative is a stage that silently does
    # not reflect an edit. (This was Assert-TrinixAppsCurrent, a one-line
    # passthrough whose name promised a check it did not perform — the promise is
    # now kept by the line above, and the build is what it always was.)
    Build-App -Architecture $Architecture

    $toolchainImage = "$ImagePrefix/toolchain-$($Architecture.Name):$Tag"
    $image = "$ImagePrefix/base-$($Architecture.Name):$Tag"
    $target = if ($Verify) { 'base-verify' } else { 'base' }

    $dockerArgs = Get-CommonBuildArgs -Target $target -Dockerfile 'base.Dockerfile' -Stage 'base' -Architecture $Architecture.Name
    $dockerArgs += Get-SourcesContextArg
    $dockerArgs += Get-TrustContextArg
    $dockerArgs += @('--build-context', "apps=$(Join-Path (Get-AppsDirectory) $Architecture.Name)")
    $dockerArgs += @(
        '--build-arg', "TOOLCHAIN_IMAGE=$toolchainImage",
        '--build-arg', "TRINIX_ARCH=$($Architecture.Name)",
        # The C# publish stage installs into the rootfs by path, and the path
        # is per-triple.
        '--build-arg', "TRINIX_TRIPLE=$($Architecture.Triple)",
        '--tag', $image, '--load', $root
    )

    Invoke-TrinixDocker @dockerArgs

    Write-Host ''
    Write-Host "Base rootfs ready: $image ($($Architecture.Triple))" -ForegroundColor Green
}

# --- Stage: image (Phase 2, per architecture) -------------------------------

function Build-Image {
    param([Parameter(Mandatory)][psobject]$Architecture)

    Assert-TrinixDiskSpace -Stage "image ($($Architecture.Name))"

    # This stage rebuilds nothing below itself, so the whole chain is read back
    # from labels rather than refreshed: a host-tools edit three links down is
    # precisely the failure that reached image assembly twice without a mkfs.
    Assert-TrinixStageCurrent -Name 'image' -ImagePrefix $ImagePrefix -Tag $Tag -Arch $Architecture.Name

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
        'app' {
            foreach ($a in $architectures) {
                Build-App -Architecture $a
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
