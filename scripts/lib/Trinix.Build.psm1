#requires -Version 7.4
<#
    Shared helpers for the Trinix build scripts.

    Everything here is host-side orchestration only: it knows how to find the repo,
    how to talk to `docker buildx`, and how to read base/sources.json. The actual
    compilation always happens inside a container.
#>

Set-StrictMode -Version 3.0

# --- Repository ------------------------------------------------------------

function Get-TrinixRoot {
    <#  .SYNOPSIS  Absolute path to the repository root, derived from this module's location. #>
    [CmdletBinding()]
    [OutputType([string])]
    param()
    return (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
}

function Get-TrinixSourceCache {
    [CmdletBinding()]
    [OutputType([string])]
    param()
    $dir = Join-Path (Get-TrinixRoot) '.cache' 'sources'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    return (Resolve-Path $dir).Path
}

# --- Architectures ---------------------------------------------------------

# One table, used by every stage. `Triple` is what clang gets via --target;
# `KernelArch` is the kernel's ARCH=; `QemuUser` runs foreign test binaries in
# the build container; `QemuSystem` boots the finished image.
$script:Architectures = [ordered]@{
    'arm64' = [pscustomobject]@{
        Name           = 'arm64'
        Triple         = 'aarch64-trinix-linux-gnu'
        KernelArch     = 'arm64'
        DockerPlatform = 'linux/arm64'
        QemuUser       = 'qemu-aarch64-static'
        QemuSystem     = 'qemu-system-aarch64'
        EfiName        = 'BOOTAA64.EFI'
    }
    'x86_64' = [pscustomobject]@{
        Name           = 'x86_64'
        Triple         = 'x86_64-trinix-linux-gnu'
        KernelArch     = 'x86_64'
        DockerPlatform = 'linux/amd64'
        QemuUser       = 'qemu-x86_64-static'
        QemuSystem     = 'qemu-system-x86_64'
        EfiName        = 'BOOTX64.EFI'
    }
}

function Get-TrinixArch {
    <#
        .SYNOPSIS  Resolve one or all target architectures.
        .PARAMETER Name  'arm64', 'x86_64', or 'both'.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateSet('arm64', 'x86_64', 'both')][string]$Name)

    if ($Name -eq 'both') { return $script:Architectures.Values }
    return @($script:Architectures[$Name])
}

# --- Pinned sources -------------------------------------------------------

function Get-TrinixSourceManifestPath {
    [CmdletBinding()]
    [OutputType([string])]
    param()
    return (Join-Path (Get-TrinixRoot) 'base' 'sources.json')
}

function Get-TrinixSource {
    <#
        .SYNOPSIS  Read base/sources.json and return one object per pinned tarball.
        .PARAMETER Name   Optional name filter (wildcards allowed).
        .PARAMETER Phase  Optional phase filter.
    #>
    [CmdletBinding()]
    param(
        [string[]]$Name,
        [int[]]$Phase
    )

    $manifest = Get-Content -Raw -LiteralPath (Get-TrinixSourceManifestPath) | ConvertFrom-Json

    foreach ($property in $manifest.sources.PSObject.Properties) {
        $entry = $property.Value
        if ($Name -and -not ($Name | Where-Object { $property.Name -like $_ })) { continue }
        if ($Phase -and $entry.phase -notin $Phase) { continue }

        [pscustomobject]@{
            Name     = $property.Name
            Version  = $entry.version
            Phase    = $entry.phase
            Url      = Resolve-TrinixSourceUrl -Entry $entry
            Sha256   = $entry.sha256
            FileName = Split-Path -Leaf (Resolve-TrinixSourceUrl -Entry $entry)
            Notes    = if ($entry.PSObject.Properties.Name -contains 'notes') { $entry.notes } else { $null }
        }
    }
}

function Resolve-TrinixSourceUrl {
    <#  .SYNOPSIS  Expand ${version} / ${versionMajor} placeholders in a source URL. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][psobject]$Entry)

    $url = $Entry.url -replace '\$\{version\}', $Entry.version
    if ($Entry.PSObject.Properties.Name -contains 'versionMajor') {
        $url = $url -replace '\$\{versionMajor\}', $Entry.versionMajor
    }
    return $url
}

function Set-TrinixSourceChecksum {
    <#
        .SYNOPSIS  Write a computed sha256 back into base/sources.json.
        .DESCRIPTION
            Rewrites the whole manifest through ConvertTo-Json, which preserves
            property order, so the diff is limited to the sha256 lines that changed.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][hashtable]$Checksums
    )

    $path = Get-TrinixSourceManifestPath
    $manifest = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json

    $changed = 0
    foreach ($name in $Checksums.Keys) {
        $entry = $manifest.sources.$name
        if (-not $entry) { Write-Warning "No such source '$name' in the manifest; skipping."; continue }
        if ($entry.sha256 -ne $Checksums[$name]) {
            $entry.sha256 = $Checksums[$name]
            $changed++
        }
    }

    if ($changed -gt 0 -and $PSCmdlet.ShouldProcess($path, "update $changed checksum(s)")) {
        $json = $manifest | ConvertTo-Json -Depth 12
        Set-Content -LiteralPath $path -Value $json -Encoding utf8NoBOM
    }
    return $changed
}

# --- Docker ---------------------------------------------------------------

function Assert-TrinixDocker {
    <#  .SYNOPSIS  Fail early and legibly if docker/buildx is not usable. #>
    [CmdletBinding()]
    param()

    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'docker was not found on PATH. Install Docker Desktop — it is the only host requirement.'
    }
    & docker buildx version *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'docker buildx is unavailable. Trinix builds require BuildKit (Docker Desktop ships it).'
    }
}

function Invoke-TrinixDocker {
    <#
        .SYNOPSIS  Run docker with the given arguments, echoing the command and honouring -WhatIf.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Arguments)

    $rendered = 'docker ' + ($Arguments -join ' ')
    if (-not $PSCmdlet.ShouldProcess($rendered, 'run')) { Write-Host "would run: $rendered"; return }

    Write-Host "==> $rendered" -ForegroundColor Cyan
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "docker exited with code $LASTEXITCODE" }
}

Export-ModuleMember -Function `
    Get-TrinixRoot, Get-TrinixSourceCache, Get-TrinixArch, `
    Get-TrinixSourceManifestPath, Get-TrinixSource, Resolve-TrinixSourceUrl, Set-TrinixSourceChecksum, `
    Assert-TrinixDocker, Invoke-TrinixDocker
