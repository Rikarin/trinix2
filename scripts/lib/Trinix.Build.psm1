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
            FileName = Resolve-TrinixSourceFileName -Entry $entry
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

function Resolve-TrinixSourceFileName {
    <#
        .SYNOPSIS  What this source is called in the download cache.
        .DESCRIPTION
            The URL's last segment, unless the entry overrides it with `fileName`.

            The override exists because the cache is one flat directory keyed by
            that name, and two upstreams can disagree about who owns a name.
            Vulkan-Headers and Vulkan-Loader are the case: both are GitHub
            auto-generated archives of a tag called `vulkan-sdk-<version>`, so
            both resolve to `vulkan-sdk-1.4.357.0.tar.gz` and the second fetch
            finds the first one's bytes sitting at its path. That surfaces as a
            checksum mismatch rather than a wrong build, which is the good
            failure mode — but it is still a collision, and naming the file is
            how an entry gets out of it.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][psobject]$Entry)

    if ($Entry.PSObject.Properties.Name -contains 'fileName') {
        $name = $Entry.fileName -replace '\$\{version\}', $Entry.version
        if ($Entry.PSObject.Properties.Name -contains 'versionMajor') {
            $name = $name -replace '\$\{versionMajor\}', $Entry.versionMajor
        }
        return $name
    }

    return (Split-Path -Leaf (Resolve-TrinixSourceUrl -Entry $Entry))
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

function Assert-TrinixDiskSpace {
    <#  .SYNOPSIS  Fail early and legibly if the host volume cannot hold a build. #>
    [CmdletBinding()]
    param(
        # Lower bound 1 on purpose: 0 or a negative would be a guard that passes
        # unconditionally, which is the failure this function exists to prevent.
        [ValidateRange(1, 65536)][int]$RequiredGB = 30,
        [string]$Stage = 'this build'
    )

    # Measured on the host, never inside the container, and that distinction is
    # the whole point. Docker Desktop's Linux VM reports the size of its virtual
    # disk — ~814G on this machine — while the file backing that disk grows on
    # the host volume, so an in-VM `df` reads hundreds of gigabytes free right up
    # until the Mac is at zero bytes and needs manual recovery. Any check that
    # runs in the container is worse than none, because it reassures.
    #
    # DriveInfo resolves a path to its containing volume, which on macOS is the
    # data volume rather than the synthesized root; verified equal to
    # `df -k /System/Volumes/Data` to within a block. The repository is the path
    # asked about because stage artifacts are exported into it — and on macOS
    # Docker's own disk image lives under $HOME on that same volume.
    $root = Get-TrinixRoot
    $free = [System.IO.DriveInfo]::new($root).AvailableFreeSpace
    $freeGB = [math]::Round($free / 1GB, 1)

    if ($free -lt ($RequiredGB * 1GB)) {
        throw "Only $freeGB GiB free on the host volume holding $root, and $Stage needs about $RequiredGB GiB. Free up space before building: BuildKit will fill this disk to zero rather than stop, because the space it sees is the VM's, not yours."
    }

    Write-Host "Host disk: $freeGB GiB free, $RequiredGB GiB required for $Stage." -ForegroundColor DarkGray
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

# --- Stage provenance and staleness ----------------------------------------

<#
    Why a stage image records what it was built from.

    Twice in one week a build ran to completion against a cached stage image
    whose inputs had moved weeks earlier, and both times the symptom pointed
    nowhere near the cause: mesa reporting `Python >= 3.10 not found` because a
    three-week-old toolchain predated the commit that added mako/yaml/packaging
    to host-tools.Dockerfile, and image assembly unable to call `mkfs.btrfs`
    because the cached toolchain predated the commit that installed
    btrfs-progs. Nothing in either image *looked* old enough to be wrong.

    So every stage image is stamped, at build time, with the SHA-256 of the
    Dockerfile that produced it and the identity of the image it was built
    FROM; before a build consumes a cached stage image, the whole FROM chain is
    read back and compared. A mismatch refuses the build and names the stage,
    what moved, and the command to rebuild it. Refuses rather than rebuilds on
    purpose — the cheapest link in this chain is a 37-minute LLVM build, and a
    silent cascade into one is its own hazard.

    ⚠ The identity recorded for a base image is a hash of its *rootfs layer
    digests*, not `docker image inspect --format '{{.Id}}'`. Measured here: two
    fully-cached rebuilds of the same Dockerfile produce three different image
    IDs (the config the ID digests is re-serialised each export) while the
    rootfs layers are byte-identical. Recording `.Id` would mark every
    descendant stale after any no-op rebuild of its parent, which is a gate
    that cries wolf until it is disabled.
#>

# The label names, spelled once. `trinix.dockerfile` is carried alongside the
# hash so that an image can say what it claims to be built from, not just that
# something no longer matches.
#
# `trinix.dockerfile.sha256` is the file's raw bytes and always has been; it
# still names the exact file an image was built from. What the gate *compares*
# is `trinix.dockerfile.content.sha256`, the hash of the build-relevant content
# (see Get-TrinixDockerfileHash). Two labels rather than a redefinition of one,
# so that an image stamped before this existed is recognisable as such instead
# of silently failing a comparison it was never stamped for.
$script:ProvenanceLabel = [pscustomobject]@{
    Stage       = 'trinix.stage'
    Dockerfile  = 'trinix.dockerfile'
    Hash        = 'trinix.dockerfile.sha256'
    Content     = 'trinix.dockerfile.content.sha256'
    BaseImage   = 'trinix.base.image'
    BaseContent = 'trinix.base.content'
}

# The FROM chain, as it actually is rather than as the stage list suggests.
#
#   host-tools   FROM debian:${DEBIAN_TAG}
#   llvm         FROM ${HOST_TOOLS_IMAGE}     (toolchain.Dockerfile)
#   toolchain    FROM ${HOST_TOOLS_IMAGE}     (toolchain.Dockerfile; llvm is an
#                                              internal stage of the same file,
#                                              not the tagged trinix/llvm image)
#   app          FROM ${HOST_TOOLS_IMAGE}
#   base         FROM ${TOOLCHAIN_IMAGE}
#   image        FROM ${BASE_IMAGE}
#
# `Image` is $null for the stages whose build target exports a local directory
# from `scratch` rather than loading a tagged image: there is nothing cached
# under a tag for them to be stale, but they still have ancestors that can be.
$script:StageGraph = [ordered]@{
    'host-tools' = [pscustomobject]@{
        Stage = 'host-tools'; Dockerfile = 'host-tools.Dockerfile'
        Image = 'host-tools'; Parent = $null; ExternalBase = 'debian:${DEBIAN_TAG}'
    }
    'llvm' = [pscustomobject]@{
        Stage = 'llvm'; Dockerfile = 'toolchain.Dockerfile'
        Image = 'llvm'; Parent = 'host-tools'; ExternalBase = $null
    }
    'toolchain' = [pscustomobject]@{
        Stage = 'toolchain'; Dockerfile = 'toolchain.Dockerfile'
        Image = 'toolchain-{arch}'; Parent = 'host-tools'; ExternalBase = $null
    }
    'app' = [pscustomobject]@{
        Stage = 'app'; Dockerfile = 'app.Dockerfile'
        Image = $null; Parent = 'host-tools'; ExternalBase = $null
    }
    'base' = [pscustomobject]@{
        Stage = 'base'; Dockerfile = 'base.Dockerfile'
        Image = 'base-{arch}'; Parent = 'toolchain'; ExternalBase = $null
    }
    'image' = [pscustomobject]@{
        Stage = 'image'; Dockerfile = 'image.Dockerfile'
        Image = $null; Parent = 'base'; ExternalBase = $null
    }
}

<#
    Why the recorded hash is not a hash of the file's bytes.

    A byte hash marks a stage stale when a *comment* is edited, and these
    Dockerfiles are half prose: the gate then demands a 39-minute LLVM rebuild
    to pay for a corrected sentence. That is the failure mode the gate was
    written to avoid — a guard that fails spuriously trains you to work around
    it — and it was stricter than BuildKit, whose cache key comes from the
    parsed instructions and survives a comment edit untouched.

    So what is hashed is the build-relevant content: every line whose first
    non-whitespace character is `#` is dropped before hashing.

    ⚠ The carve-out that makes this more than a one-liner: a parser directive is
    *not* a comment. `# syntax=docker/dockerfile:1.10` chooses the frontend that
    parses the file, `# escape=` changes what a line continuation is, and both
    genuinely change the build; image.Dockerfile opens with the first. Directives
    exist only in the leading block, before any instruction, blank line or
    ordinary comment, and are `# key=value`. They stay in the hash. Do not
    "simplify" this back to Get-FileHash.

    Everything else is left alone on purpose. Trailing whitespace is load-bearing
    (a space after a `\` is no longer a line continuation), blank lines cost
    nothing to keep, and every further normalisation is another chance to call
    two genuinely different files the same.
#>

# A parser directive, matched the way BuildKit matches one: `# key=value`, with
# non-line-breaking whitespace permitted around the key and the `=`, and a
# non-empty value. Deliberately liberal — leading whitespace before the `#`, and
# keys BuildKit does not know, are matched here and kept. Over-matching keeps a
# line in the hash, which can only cost a rebuild that was not needed;
# under-matching would hide a real change, which is the failure that matters.
$script:DockerfileDirective = [regex]::new('^\s*#\s*[a-zA-Z][a-zA-Z0-9]*\s*=\s*\S.*$')

# A heredoc opener. Inside a heredoc body a leading `#` is program text — a shell
# comment the container will run past — and BuildKit keeps it, so stripping it
# would let two different scripts hash the same. Rather than parse heredoc
# bodies, a Dockerfile containing anything resembling an opener is hashed whole;
# `<<` in a string costs that file the comment carve-out, and nothing else. No
# Dockerfile here uses one today.
$script:DockerfileHeredoc = [regex]::new('<<')

function Get-TrinixDockerfileContent {
    <#  .SYNOPSIS  A Dockerfile's build-relevant text: comment lines dropped, parser directives kept. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)

    $kept = [System.Collections.Generic.List[string]]::new()
    $directives = $true

    foreach ($line in $Text.Split("`n")) {
        # `\r` off the end only for the decision; the line itself is kept exactly
        # as it was, so a CRLF file and an LF file remain different files.
        $bare = $line.TrimEnd("`r")

        if ($directives) {
            if ($script:DockerfileDirective.IsMatch($bare)) { $kept.Add($line); continue }
            # A comment, a blank line or an instruction closes the block, and
            # anything directive-shaped after it is an ordinary comment.
            $directives = $false
        }

        if ($bare.TrimStart().StartsWith('#')) { continue }
        if ($script:DockerfileHeredoc.IsMatch($bare)) { return $Text }
        $kept.Add($line)
    }

    return ($kept -join "`n")
}

function Get-TrinixDockerfileHash {
    <#  .SYNOPSIS  SHA-256 of a Dockerfile's build-relevant content, as `sha256:<hex>`. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { throw "No such Dockerfile: $Path" }
    return Get-TrinixDockerfileContentHash -Bytes ([System.IO.File]::ReadAllBytes($Path))
}

function Get-TrinixDockerfileByteHash {
    <#  .SYNOPSIS  SHA-256 of a Dockerfile's raw bytes, as `sha256:<hex>`. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { throw "No such Dockerfile: $Path" }
    return 'sha256:' + (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-TrinixDockerfileContentHash {
    <#  .SYNOPSIS  The build-relevant hash of Dockerfile bytes, wherever they came from. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes)

    # A UTF-8 BOM is not content: BuildKit ignores it, and left in place it would
    # push the first line out of directive position and turn `# syntax=` into a
    # comment this function then dropped.
    $text = [System.Text.Encoding]::UTF8.GetString($Bytes).TrimStart([char]0xFEFF)
    $content = [System.Text.Encoding]::UTF8.GetBytes((Get-TrinixDockerfileContent -Text $text))
    return 'sha256:' + [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($content)).ToLowerInvariant()
}

function Get-TrinixGitBlob {
    <#  .SYNOPSIS  The bytes of a git blob, or $null if it cannot be read. #>
    [CmdletBinding()]
    [OutputType([byte[]])]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Blob
    )

    # Read as a stream rather than through the pipeline: a native command's
    # output reaches PowerShell as decoded, re-terminated lines, and a hash of
    # that is a hash of something other than the file.
    $start = [System.Diagnostics.ProcessStartInfo]::new('git')
    $start.WorkingDirectory = $Root
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('cat-file', 'blob', $Blob)) { $start.ArgumentList.Add($argument) }

    $process = [System.Diagnostics.Process]::Start($start)
    $buffer = [System.IO.MemoryStream]::new()
    $process.StandardOutput.BaseStream.CopyTo($buffer)
    $process.StandardError.ReadToEnd() | Out-Null
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { return $null }

    return $buffer.ToArray()
}

function Resolve-TrinixLegacyContentHash {
    <#
        .SYNOPSIS  The content hash of the committed revision of a Dockerfile whose bytes hash to $ByteHash, or $null.
        .DESCRIPTION
            Only for images stamped before `trinix.dockerfile.content.sha256`
            existed, and only ever reached when their recorded byte hash no
            longer matches the file. Such a stamp names an exact revision of an
            exact file; if git still holds it, its build-relevant content is
            knowable, and comparing that to the working copy answers the only
            question the gate ever asks — did what the build reads change?

            This is what keeps the change from costing what it was written to
            avoid: without it, redefining the hash would mark every existing
            stage stale and demand the rebuild chain it exists to prevent. It
            retires itself — every image built from here on carries the content
            hash and never comes down this path — and it forgives nothing it
            cannot prove: a revision git does not have, a build from an
            uncommitted file, or no git at all all leave the stamp judged on
            bytes, exactly as before.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ByteHash
    )

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { return $null }

    $root = Get-TrinixRoot
    $relative = Get-TrinixRelativePath -Path $Path
    $revisions = & git -C $root log --all --format=%H -- $relative 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $revisions) { return $null }

    $seen = @{}
    foreach ($revision in $revisions) {
        $blob = & git -C $root rev-parse --verify --quiet "${revision}:$relative" 2>$null
        if (-not $blob -or $seen.ContainsKey($blob)) { continue }
        $seen[$blob] = $true

        $bytes = Get-TrinixGitBlob -Root $root -Blob $blob
        if ($null -eq $bytes) { continue }

        $digest = 'sha256:' + [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        if ($digest -eq $ByteHash) { return Get-TrinixDockerfileContentHash -Bytes $bytes }
    }

    return $null
}

function Get-TrinixImageContentId {
    <#
        .SYNOPSIS  A rebuild-stable identity for a local image, or $null if it is not present.
        .DESCRIPTION
            SHA-256 over the image's rootfs layer digests. See the note above on
            why this is not `.Id`: the layer list survives a cached rebuild
            unchanged, and the image ID does not.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Image)

    $layers = & docker image inspect $Image --format '{{json .RootFS.Layers}}' 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($layers)) { return $null }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($layers)
    $digest = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return 'sha256:' + [System.Convert]::ToHexString($digest).ToLowerInvariant()
}

function Get-TrinixImageLabel {
    <#
        .SYNOPSIS  The labels on a local image as a hashtable, or $null if the image is not present.
        .DESCRIPTION
            An image with no labels at all reports no `Labels` key rather than an
            empty one, so the two cases are told apart here — $null means absent,
            an empty hashtable means present and unlabelled.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Image)

    $json = & docker image inspect $Image --format '{{json .Config}}' 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { return $null }

    $labels = @{}
    $config = $json | ConvertFrom-Json
    if ($config.PSObject.Properties.Name -contains 'Labels' -and $config.Labels) {
        foreach ($property in $config.Labels.PSObject.Properties) { $labels[$property.Name] = $property.Value }
    }
    return $labels
}

function New-TrinixProvenanceLabel {
    <#
        .SYNOPSIS  The `--label` arguments that stamp a stage image with what it was built from.
        .PARAMETER Dockerfile  Absolute path to the Dockerfile driving the build.
        .PARAMETER BaseImage   The image ref this stage's FROM resolves to.
        .DESCRIPTION
            Labels are image config, not a layer: adding them neither invalidates
            BuildKit's cache nor changes the rootfs, so stamping is free and does
            not make a stage look stale to its own children.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string]$Dockerfile,
        [Parameter(Mandatory)][string]$BaseImage
    )

    $content = Get-TrinixImageContentId -Image $BaseImage
    if (-not $content) {
        throw "Cannot stamp stage '$Stage': its base image $BaseImage is not present locally, so there is nothing to record it was built from. Build or pull $BaseImage first."
    }

    return @(
        '--label', "$($script:ProvenanceLabel.Stage)=$Stage",
        '--label', "$($script:ProvenanceLabel.Dockerfile)=$(Get-TrinixRelativePath -Path $Dockerfile)",
        '--label', "$($script:ProvenanceLabel.Hash)=$(Get-TrinixDockerfileByteHash -Path $Dockerfile)",
        '--label', "$($script:ProvenanceLabel.Content)=$(Get-TrinixDockerfileHash -Path $Dockerfile)",
        '--label', "$($script:ProvenanceLabel.BaseImage)=$BaseImage",
        '--label', "$($script:ProvenanceLabel.BaseContent)=$content"
    )
}

function Get-TrinixRelativePath {
    <#  .SYNOPSIS  A path spelled relative to the repository root, with forward slashes. #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Path)
    return ([System.IO.Path]::GetRelativePath((Get-TrinixRoot), $Path) -replace '\\', '/')
}

function Test-TrinixStageProvenance {
    <#
        .SYNOPSIS  Why a cached stage image is stale; an empty result means it is current.
        .PARAMETER Dockerfile  Absolute path to the Dockerfile that should have built it.
        .PARAMETER BaseImage   The image ref its FROM resolves to now.
        .OUTPUTS  One human-readable sentence per problem found.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)][string]$Image,
        [Parameter(Mandatory)][string]$Dockerfile,
        [Parameter(Mandatory)][string]$BaseImage
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    $labels = Get-TrinixImageLabel -Image $Image
    $relative = Get-TrinixRelativePath -Path $Dockerfile

    if ($null -eq $labels) {
        $reasons.Add('is not present locally.')
        return $reasons.ToArray()
    }

    # An image built before this gate existed carries no labels. Said plainly:
    # the image is not known to be wrong, it is unverifiable, and an
    # unverifiable image is exactly the thing that cost hours twice.
    if (-not $labels.ContainsKey($script:ProvenanceLabel.Hash)) {
        $reasons.Add("carries no build-provenance labels, so it predates this check and nothing about it can be verified. Stamping it means a real rebuild of it and everything below it, not a relabel.")
        return $reasons.ToArray()
    }

    if ($labels.ContainsKey($script:ProvenanceLabel.Content)) {
        $recordedHash = $labels[$script:ProvenanceLabel.Content]
        $currentHash = Get-TrinixDockerfileHash -Path $Dockerfile
    } else {
        # Stamped before the comparison stopped being over raw bytes. Bytes are
        # all this image recorded, so bytes are what it is judged on — and only
        # if those bytes turn out to be a revision git still holds is the
        # content hash it *would* carry recoverable. Nothing is assumed: an
        # unresolvable legacy stamp stays as strict as it was the day it was
        # made, which for an untouched file is already a match.
        $recordedHash = $labels[$script:ProvenanceLabel.Hash]
        $currentHash = Get-TrinixDockerfileByteHash -Path $Dockerfile

        if ($recordedHash -ne $currentHash) {
            $stamped = Resolve-TrinixLegacyContentHash -Path $Dockerfile -ByteHash $recordedHash
            if ($stamped) {
                $recordedHash = $stamped
                $currentHash = Get-TrinixDockerfileHash -Path $Dockerfile
            }
        }
    }

    if ($recordedHash -ne $currentHash) {
        $reasons.Add("$relative has changed since it was built (recorded $recordedHash, current $currentHash).")
    }

    $recordedBase = if ($labels.ContainsKey($script:ProvenanceLabel.BaseImage)) { $labels[$script:ProvenanceLabel.BaseImage] } else { '(unrecorded)' }
    if ($recordedBase -ne $BaseImage) {
        $reasons.Add("was built FROM $recordedBase, but this build would put it on $BaseImage.")
    } else {
        $recordedContent = if ($labels.ContainsKey($script:ProvenanceLabel.BaseContent)) { $labels[$script:ProvenanceLabel.BaseContent] } else { $null }
        $currentContent = Get-TrinixImageContentId -Image $BaseImage
        if (-not $currentContent) {
            $reasons.Add("was built on $BaseImage, which is no longer present locally, so it cannot be checked.")
        } elseif ($recordedContent -ne $currentContent) {
            $reasons.Add("was built on an older $BaseImage (recorded $recordedContent, current $currentContent).")
        }
    }

    return $reasons.ToArray()
}

function Get-TrinixStage {
    <#
        .SYNOPSIS  Resolve one stage of the FROM chain to concrete paths and image refs.
        .PARAMETER Arch  Required for the per-architecture stages; ignored by the others.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('host-tools', 'llvm', 'toolchain', 'app', 'base', 'image')][string]$Name,
        [string]$ImagePrefix = 'trinix',
        [string]$Tag = 'dev',
        [string]$Arch
    )

    $node = $script:StageGraph[$Name]
    $dockerfile = Join-Path (Get-TrinixRoot) 'docker' $node.Dockerfile

    $image = $null
    if ($node.Image) {
        if ($node.Image -like '*{arch}*' -and -not $Arch) {
            throw "Stage '$Name' is per-architecture; Get-TrinixStage needs -Arch."
        }
        $image = "$ImagePrefix/$($node.Image -replace '\{arch\}', $Arch):$Tag"
    }

    $baseImage = if ($node.Parent) {
        (Get-TrinixStage -Name $node.Parent -ImagePrefix $ImagePrefix -Tag $Tag -Arch $Arch).Image
    } else {
        Resolve-TrinixExternalBase -Dockerfile $dockerfile -Reference $node.ExternalBase
    }

    # The command that fixes this stage, spelled exactly. Non-default prefix and
    # tag are echoed back so the instruction is runnable as printed rather than
    # runnable only on the default configuration.
    $command = "./scripts/build.ps1 -Stage $Name"
    if ($node.Image -like '*{arch}*') { $command += " -Arch $Arch" }
    if ($ImagePrefix -ne 'trinix') { $command += " -ImagePrefix $ImagePrefix" }
    if ($Tag -ne 'dev') { $command += " -Tag $Tag" }

    return [pscustomobject]@{
        Stage          = $Name
        Dockerfile     = $dockerfile
        DockerfilePath = Get-TrinixRelativePath -Path $dockerfile
        Image          = $image
        Parent         = $node.Parent
        BaseImage      = $baseImage
        RebuildCommand = $command
    }
}

function Resolve-TrinixExternalBase {
    <#
        .SYNOPSIS  Expand a `${ARG}` in an upstream FROM using the Dockerfile's own ARG default.
        .DESCRIPTION
            The upstream tag is spelled once, in the Dockerfile, and read back
            from there — so the graph above and `FROM debian:${DEBIAN_TAG}`
            cannot drift into judging a build against a tag it never used.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory)][string]$Dockerfile,
        [Parameter(Mandatory)][string]$Reference
    )

    $text = Get-Content -Raw -LiteralPath $Dockerfile
    $resolved = $Reference

    foreach ($placeholder in ([regex]::Matches($Reference, '\$\{(\w+)\}'))) {
        $arg = $placeholder.Groups[1].Value
        $default = [regex]::Match($text, "(?m)^ARG\s+$arg=(\S+)\s*$")
        if (-not $default.Success) {
            throw "$Dockerfile declares no default for ARG $arg, so the upstream image behind $Reference cannot be resolved."
        }
        $resolved = $resolved.Replace($placeholder.Value, $default.Groups[1].Value)
    }

    return $resolved
}

function Get-TrinixStageChain {
    <#  .SYNOPSIS  A stage and everything it is built on, base first. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('host-tools', 'llvm', 'toolchain', 'app', 'base', 'image')][string]$Name,
        [string]$ImagePrefix = 'trinix',
        [string]$Tag = 'dev',
        [string]$Arch
    )

    $chain = [System.Collections.Generic.List[object]]::new()
    $cursor = $Name
    while ($cursor) {
        $stage = Get-TrinixStage -Name $cursor -ImagePrefix $ImagePrefix -Tag $Tag -Arch $Arch
        $chain.Insert(0, $stage)
        $cursor = $stage.Parent
    }
    return $chain.ToArray()
}

function Get-TrinixStageLabelArgs {
    <#  .SYNOPSIS  The `--label` arguments for a stage, resolved from the FROM chain. #>
    [CmdletBinding(SupportsShouldProcess)]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory)][ValidateSet('host-tools', 'llvm', 'toolchain', 'app', 'base', 'image')][string]$Name,
        [string]$ImagePrefix = 'trinix',
        [string]$Tag = 'dev',
        [string]$Arch
    )

    $stage = Get-TrinixStage -Name $Name -ImagePrefix $ImagePrefix -Tag $Tag -Arch $Arch

    # The upstream base on a fresh machine: the build is about to pull it
    # anyway, and pulling it here means the digest recorded is the digest the
    # build actually uses rather than one guessed after the fact.
    if (-not $stage.Parent -and -not (Get-TrinixImageContentId -Image $stage.BaseImage)) {
        Invoke-TrinixDocker 'image' 'pull' $stage.BaseImage | Out-Host
    }

    return New-TrinixProvenanceLabel -Stage $Name -Dockerfile $stage.Dockerfile -BaseImage $stage.BaseImage
}

function Assert-TrinixStageCurrent {
    <#
        .SYNOPSIS  Refuse to build on cached stage images whose Dockerfile or base image has moved.
        .DESCRIPTION
            Checks every ancestor of the named stage — not just its immediate
            parent, because `-Stage image` rebuilds nothing below `base` and a
            host-tools edit three links down is exactly the failure this exists
            for. The stage itself is not checked: it is about to be rebuilt.

            Reports the whole chain at once. One refusal per stage would mean
            four round trips to learn what a single message can say.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('host-tools', 'llvm', 'toolchain', 'app', 'base', 'image')][string]$Name,
        [string]$ImagePrefix = 'trinix',
        [string]$Tag = 'dev',
        [string]$Arch
    )

    $stale = [System.Collections.Generic.List[object]]::new()
    $checked = 0

    foreach ($stage in (Get-TrinixStageChain -Name $Name -ImagePrefix $ImagePrefix -Tag $Tag -Arch $Arch)) {
        if ($stage.Stage -eq $Name) { continue }
        if (-not $stage.Image) { continue }

        $checked++
        $reasons = @(Test-TrinixStageProvenance -Image $stage.Image -Dockerfile $stage.Dockerfile -BaseImage $stage.BaseImage)
        if ($reasons.Count -gt 0) { $stale.Add([pscustomobject]@{ Stage = $stage; Reasons = $reasons }) }
    }

    if ($stale.Count -eq 0) {
        Write-Host "Stage chain current: $checked cached image(s) below $Name match their Dockerfile and base." -ForegroundColor DarkGray
        return
    }

    # Written out before the throw rather than inside it: PowerShell's error view
    # reflows an exception message into a wrapped paragraph, which turns a list of
    # stages and the commands that fix them into an unreadable run-on. The throw
    # below stays a single self-contained sentence, the way every other Assert-
    # here does; this is the part a person actually reads.
    Write-Host ''
    Write-Host "$($stale.Count) of the $checked cached stage image(s) below '$Name' cannot be trusted:" -ForegroundColor Red
    foreach ($entry in $stale) {
        Write-Host "  $($entry.Stage.Image)" -ForegroundColor Red
        foreach ($reason in $entry.Reasons) { Write-Host "    $reason" }
    }
    Write-Host ''
    Write-Host 'Rebuild, in this order, then run this build again:' -ForegroundColor Yellow
    foreach ($entry in $stale) { Write-Host "  $($entry.Stage.RebuildCommand)" -ForegroundColor Yellow }
    Write-Host ''
    Write-Host 'Not rebuilt for you on purpose: the cheapest link in this chain is a 37-minute LLVM build.' -ForegroundColor DarkGray
    Write-Host ''

    $images = ($stale | ForEach-Object { $_.Stage.Image }) -join ', '
    $commands = ($stale | ForEach-Object { $_.Stage.RebuildCommand }) -join '; '
    throw "Refusing to build '$Name': $images cannot be shown to match the Dockerfile and base image recorded in them (details above). Rebuild first: $commands"
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
    Get-TrinixSourceManifestPath, Get-TrinixSource, Resolve-TrinixSourceUrl, Resolve-TrinixSourceFileName, Set-TrinixSourceChecksum, `
    Assert-TrinixDocker, Assert-TrinixDiskSpace, Invoke-TrinixDocker, `
    Get-TrinixDockerfileContent, Get-TrinixDockerfileHash, Get-TrinixDockerfileByteHash, `
    Get-TrinixImageContentId, Get-TrinixImageLabel, Get-TrinixRelativePath, `
    New-TrinixProvenanceLabel, Test-TrinixStageProvenance, `
    Get-TrinixStage, Resolve-TrinixExternalBase, Get-TrinixStageChain, Get-TrinixStageLabelArgs, Assert-TrinixStageCurrent, `
    Get-TrinixSigningDirectory, Get-TrinixSigningIdentity, Assert-TrinixSigningIdentity, Update-TrinixTrustStore
