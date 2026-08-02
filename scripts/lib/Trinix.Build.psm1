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

# --- Signing (Phase 6) -----------------------------------------------------

<#
    The development PKI.

    Generated on this machine, once, and never committed: the repository has no
    key material in it at any point in its history, which is a property that is
    easy to keep and impossible to restore. What that costs is that a fresh
    clone has no trust anchor until the first build creates one — so this is
    called before any stage that either signs something or bakes a trust store
    into an image, and it is a no-op every time after the first.

    The certificates are made by Trinix's own tool, running in the build
    container, because that tool is the thing whose certificate profiles are
    the policy (see src/Trinix.Bundle/DeveloperPki.cs). Generating them with
    openssl here would mean two implementations of the same policy, and the one
    that is only used at bootstrap would be the one that drifts.
#>

function Get-TrinixSigningDirectory {
    [CmdletBinding()]
    [OutputType([string])]
    param()
    return (Join-Path (Get-TrinixRoot) 'signing' 'local')
}

function Get-TrinixSigningIdentity {
    <#  .SYNOPSIS  Paths to the development root and signing certificate. #>
    [CmdletBinding()]
    param()

    $dir = Get-TrinixSigningDirectory
    return [pscustomobject]@{
        Directory       = $dir
        RootCertificate = Join-Path $dir 'dev-root.pub.pem'
        RootKey         = Join-Path $dir 'dev-root.key.pem'
        Certificate     = Join-Path $dir 'dev-identity.pub.pem'
        Key             = Join-Path $dir 'dev-identity.key.pem'
    }
}

function Assert-TrinixSigningIdentity {
    <#
        .SYNOPSIS  Create the development PKI if this machine does not have one.
        .PARAMETER HostToolsImage  The build container to run the tool in.
        .DESCRIPTION
            Returns nothing on purpose. A native command's output goes into the
            PowerShell pipeline, so a function that both runs `docker` and
            returns an object returns the docker transcript with the object
            stapled to the end of it — and the caller gets an array where it
            expected a record. Ask Get-TrinixSigningIdentity for the paths.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$HostToolsImage)

    $identity = Get-TrinixSigningIdentity
    $complete = @($identity.RootCertificate, $identity.RootKey, $identity.Certificate, $identity.Key) |
        ForEach-Object { Test-Path -LiteralPath $_ }

    if ($complete -notcontains $false) { return }

    if (-not (Test-Path $identity.Directory)) {
        New-Item -ItemType Directory -Path $identity.Directory -Force | Out-Null
    }

    Write-Host ''
    Write-Host 'No development signing identity on this machine; creating one.' -ForegroundColor Yellow
    Write-Host "  $($identity.Directory) — never committed, never shared." -ForegroundColor DarkGray

    $root = Get-TrinixRoot
    # Paths inside the container. The repository is bind-mounted rather than
    # copied so the generated key lands on the host and survives the container.
    $script = @'
set -euo pipefail
cd /work
dotnet publish src/Trinix.Bundle.Tool/Trinix.Bundle.Tool.csproj \
    --configuration Release --output /tmp/tool --nologo --verbosity quiet
/tmp/tool/trinix-bundle pki init \
    --certificate signing/local/dev-root.pub.pem \
    --key signing/local/dev-root.key.pem \
    --name "Trinix Development Root ($(hostname))"
/tmp/tool/trinix-bundle pki identity \
    --root-certificate signing/local/dev-root.pub.pem \
    --root-key signing/local/dev-root.key.pem \
    --certificate signing/local/dev-identity.pub.pem \
    --key signing/local/dev-identity.key.pem \
    --name "Trinix Developer ($(hostname))"
'@

    Invoke-TrinixDocker @(
        'run', '--rm',
        '--volume', "${root}:/work",
        '--volume', 'trinix-nuget:/nuget',
        '--workdir', '/work',
        '--entrypoint', 'bash',
        $HostToolsImage, '-c', $script
    ) | Out-Host
}

function Update-TrinixTrustStore {
    <#
        .SYNOPSIS  Collect the roots that go into the system image.
        .DESCRIPTION
            Two sources, deliberately separate. signing/trusted/ holds anchors
            that are committed — a CI root, eventually a release root — and
            signing/local/ holds the one this machine generated. They are staged
            into a single directory because a build context is a directory, and
            because "the set of roots this image trusts" should be one list that
            can be read at a glance rather than two rules in a Dockerfile.
        .OUTPUTS  The staging directory, for use as a named build context.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$OutputDirectory)

    $staging = Join-Path $OutputDirectory 'pki' 'roots'
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    $sources = @(
        (Join-Path (Get-TrinixRoot) 'signing' 'trusted'),
        (Get-TrinixSigningDirectory)
    )

    $count = 0
    foreach ($source in $sources) {
        if (-not (Test-Path -LiteralPath $source)) { continue }
        foreach ($certificate in Get-ChildItem -LiteralPath $source -Filter '*.pub.pem' -File) {
            # Only certificate authorities. A developer certificate in the trust
            # store would make that one key able to sign anything with no
            # authority above it to revoke — and since both live in the same
            # directory, the mistake is one filename away. Asked of the
            # certificate rather than of its name, because a name is not a fact.
            $parsed = [System.Security.Cryptography.X509Certificates.X509Certificate2]::CreateFromPem(
                (Get-Content -Raw -LiteralPath $certificate.FullName))
            try {
                $constraints = $parsed.Extensions |
                    Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] } |
                    Select-Object -First 1
                if (-not $constraints -or -not $constraints.CertificateAuthority) { continue }
            } finally {
                $parsed.Dispose()
            }

            Copy-Item -LiteralPath $certificate.FullName -Destination (Join-Path $staging $certificate.Name)
            $count++
        }
    }

    if ($count -eq 0) {
        throw "No trust anchors found. Expected at least signing/local/dev-root.pub.pem."
    }

    return (Resolve-Path $staging).Path
}

Export-ModuleMember -Function `
    Get-TrinixRoot, Get-TrinixSourceCache, Get-TrinixArch, `
    Get-TrinixSourceManifestPath, Get-TrinixSource, Resolve-TrinixSourceUrl, Set-TrinixSourceChecksum, `
    Assert-TrinixDocker, Invoke-TrinixDocker, `
    Get-TrinixSigningDirectory, Get-TrinixSigningIdentity, Assert-TrinixSigningIdentity, Update-TrinixTrustStore
