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
#
# `trinix.inputs.sha256` covers what the Dockerfile COPYs in (see
# Get-TrinixDockerfileInputsHash), and is a third label for exactly the same
# reason: folded into the content hash it would mismatch every image already
# stamped and demand the full host-tools → toolchain → base rebuild that this
# section exists to avoid. An image without it is judged on the checks it was
# stamped for, and gains this one when it is next rebuilt for a real reason.
#
# `trinix.contexts.sha256` covers the *named* build contexts a stage reads —
# `trust`, and the two that are deliberately not hashed (see
# Get-TrinixNamedContextHash) — and is a fourth label for the third time for the
# same reason. The same rule holds: an image without it is judged on the checks
# it was stamped for.
$script:ProvenanceLabel = [pscustomobject]@{
    Stage       = 'trinix.stage'
    Dockerfile  = 'trinix.dockerfile'
    Hash        = 'trinix.dockerfile.sha256'
    Content     = 'trinix.dockerfile.content.sha256'
    Inputs      = 'trinix.inputs.sha256'
    Contexts    = 'trinix.contexts.sha256'
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

# --- Build-context inputs ---------------------------------------------------

<#
    Why hashing the Dockerfile is not enough.

    A Dockerfile hash catches an edit to the Dockerfile. It catches nothing
    about the files the Dockerfile COPYs in — and those are where the work
    lives. `toolchain/scripts/build-sysroot.sh`, `base/scripts/build-base.sh`,
    `base/recipes/*`, `base/sources.json`, `image/scripts/build-image.sh`: edit
    any of them, run the stage that consumes them, and a cached stage image
    satisfied the gate and the fix never reached the build. That is the same
    fault as the two incidents this whole section was written for — a fix
    sitting in the tree unable to reach the stage that needs it — one level
    down from where the gate was looking.

    So a second digest is recorded: the contents of every repository file the
    Dockerfile's own COPY/ADD instructions bring in.

    ⚠ Derived from the Dockerfile, never hand-declared. A list of inputs kept
    alongside the stage graph would be correct on the day it was written and
    silently wrong afterwards, because nothing makes editing a Dockerfile also
    edit the list — which is precisely the drift this gate exists to catch. The
    Dockerfile is the only statement of what a stage reads that cannot fall out
    of step with what the stage reads.

    What is deliberately *not* an input:

      • `COPY --from=<stage>` — another build stage, or a named build context
        (`sources=`, `trust=`, `apps=`, wired up by build.ps1). Neither is a
        repository path; resolving `*.tdi` or `/` against the repo would look
        for files that do not exist. Internal stages are covered because they
        are in the same Dockerfile, whose hash already changed; the tagged
        images are covered by the base content id; the named contexts are
        covered by the next section, which was written because they were not.
      • Anything .dockerignore excludes. BuildKit does not send it, so it
        cannot change the build — and hashing it would mark `base` stale the
        first time somebody ran `dotnet build` on the host and left an
        `src/**/obj` behind. A gate that fires on build droppings is a gate
        that gets switched off.
#>

# `COPY`/`ADD`, and a line continuation as BuildKit matches one — trailing
# whitespace after the escape character included, which its parser allows.
# Only the default escape character is understood; a `# escape=` directive
# would need this to follow it, and no Dockerfile here uses one.
$script:CopyLeader = [regex]::new('^\s*(?<op>COPY|ADD)\s+(?<rest>.*)$', 'IgnoreCase')
$script:LineContinuation = [regex]::new('\\[ \t]*$')

# Flags that do not change *which* files are read. Anything else — `--from=` is
# handled separately, `--exclude=` and `--parents` would both change the file
# set — is refused rather than guessed at, so a flag added later cannot quietly
# narrow what this hashes.
$script:CopyInertFlag = @('--chown', '--chmod', '--link', '--keep-git-dir')

function Get-TrinixDockerfileCopyInstruction {
    <#  .SYNOPSIS  A Dockerfile's COPY/ADD instructions, line continuations joined into one string each. #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { throw "No such Dockerfile: $Path" }
    $text = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($Path)).TrimStart([char]0xFEFF)

    # Inside a heredoc body a `COPY` is program text, not an instruction, and
    # telling the two apart means parsing heredocs. Refused instead — loudly,
    # because the alternative is a stage whose inputs are silently mis-read.
    # Get-TrinixDockerfileContent bails on the same token for the same reason.
    if ($script:DockerfileHeredoc.IsMatch($text)) {
        throw "$(Get-TrinixRelativePath -Path $Path) contains a heredoc, and the COPY inputs of a Dockerfile with one cannot be read without parsing heredoc bodies. Teach Get-TrinixDockerfileCopyInstruction to skip them before using one here."
    }

    $lines = $text.Split("`n")
    $instructions = [System.Collections.Generic.List[string]]::new()

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $leader = $script:CopyLeader.Match($lines[$i].TrimEnd("`r"))
        if (-not $leader.Success) { continue }

        $joined = $leader.Groups['rest'].Value
        while ($script:LineContinuation.IsMatch($joined)) {
            $joined = $script:LineContinuation.Replace($joined, '')
            # BuildKit drops whole-line comments inside a continued instruction,
            # so a `#` line between two sources does not end the COPY.
            do {
                $i++
                if ($i -ge $lines.Count) { throw "$(Get-TrinixRelativePath -Path $Path) ends inside a continued $($leader.Groups['op'].Value) instruction." }
                $next = $lines[$i].TrimEnd("`r")
            } while ($next.TrimStart().StartsWith('#'))
            $joined += ' ' + $next
        }

        $instructions.Add("$($leader.Groups['op'].Value.ToUpperInvariant()) $joined")
    }

    return $instructions.ToArray()
}

function Get-TrinixDockerfileCopySource {
    <#
        .SYNOPSIS  Absolute repository paths a Dockerfile COPYs in; `--from=` instructions contribute none.
        .DESCRIPTION
            The last argument of a COPY is its destination and is dropped.
            Wildcards are expanded against the repository; a source that
            resolves to nothing is an error, because `docker build` would fail
            on it too and hashing an empty set would pass instead.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory)][string]$Path)

    $root = Get-TrinixRoot
    $relative = Get-TrinixRelativePath -Path $Path
    $sources = [System.Collections.Generic.List[string]]::new()

    foreach ($instruction in (Get-TrinixDockerfileCopyInstruction -Path $Path)) {
        # @() on purpose: a one-token result would otherwise be a bare string,
        # and indexing a string hands back a character.
        $tokens = @($instruction -split '\s+' | Where-Object { $_ })
        $operation = $tokens[0]
        $arguments = [System.Collections.Generic.List[string]]::new()
        $external = $false

        foreach ($token in @($tokens | Select-Object -Skip 1)) {
            if (-not $token.StartsWith('--')) { $arguments.Add($token); continue }

            $flag = ($token -split '=', 2)[0]
            # From another build stage or a named build context. Not a
            # repository path, and covered elsewhere — see the note above.
            if ($flag -eq '--from') { $external = $true; break }
            if ($script:CopyInertFlag -notcontains $flag) {
                throw "$relative uses $flag on a $operation, and whether that changes which files are copied is not known here. Teach Get-TrinixDockerfileCopySource about it rather than letting a stage's inputs go unhashed."
            }
        }
        if ($external) { continue }

        if ($arguments.Count -lt 2) {
            throw "$relative has a $operation with fewer than two arguments: $instruction"
        }
        if ($arguments[0].StartsWith('[')) {
            throw "$relative uses the JSON form of $operation, which is not parsed here: $instruction"
        }

        # Everything but the destination.
        foreach ($source in $arguments[0..($arguments.Count - 2)]) {
            if ($source.Contains('$')) {
                throw "$relative COPYs from `"$source`", whose ARG cannot be resolved here, so the files behind it cannot be hashed."
            }

            $candidate = Join-Path $root ($source -replace '/', [System.IO.Path]::DirectorySeparatorChar)
            if ($source.Contains('*') -or $source.Contains('?')) {
                $matched = @(Get-ChildItem -Path $candidate -Force -ErrorAction SilentlyContinue)
                if ($matched.Count -eq 0) { throw "$relative COPYs `"$source`", which matches nothing in the repository." }
                foreach ($item in $matched) { $sources.Add($item.FullName) }
                continue
            }

            if (-not (Test-Path -LiteralPath $candidate)) {
                throw "$relative COPYs `"$source`", which does not exist in the repository."
            }
            $sources.Add((Resolve-Path -LiteralPath $candidate).Path)
        }
    }

    return $sources.ToArray()
}

<#
    .dockerignore, matched the way BuildKit matches it.

    Patterns are relative to the context root, `*` and `?` stop at a separator,
    `**` spans them, a `!` line takes a path back out, and the last pattern to
    match wins. A pattern matching a directory excludes everything beneath it,
    so every ancestor of a path is offered to every pattern.

    Two deliberate narrowings, both of which can only ever *include* a file
    that BuildKit would have dropped — costing at worst a rebuild that was not
    needed, never hiding a change:
      • character classes (`[Dd]ebug`) are literal here, not classes.
      • an excluded directory is not descended into unless some `!` pattern
        could plausibly reach inside it.
#>
$script:ContextIgnore = $null

function Get-TrinixContextIgnore {
    <#  .SYNOPSIS  The repository's .dockerignore as ordered {Text, Regex, Negate} patterns. #>
    [CmdletBinding()]
    param()

    if ($null -ne $script:ContextIgnore) { return $script:ContextIgnore }

    $patterns = [System.Collections.Generic.List[object]]::new()
    $file = Join-Path (Get-TrinixRoot) '.dockerignore'

    if (Test-Path -LiteralPath $file) {
        foreach ($line in ([System.IO.File]::ReadAllText($file).TrimStart([char]0xFEFF) -split "`n")) {
            $entry = $line.TrimEnd("`r").Trim()
            if (-not $entry -or $entry.StartsWith('#')) { continue }

            $negate = $entry.StartsWith('!')
            if ($negate) { $entry = $entry.Substring(1).Trim() }

            $entry = ($entry -replace '\\', '/').Trim('/')
            if (-not $entry -or $entry -eq '.') { continue }

            $patterns.Add([pscustomobject]@{
                Text   = $entry
                Regex  = ConvertTo-TrinixIgnoreRegex -Pattern $entry
                Negate = $negate
            })
        }
    }

    $script:ContextIgnore = $patterns.ToArray()
    return $script:ContextIgnore
}

function ConvertTo-TrinixIgnoreRegex {
    <#  .SYNOPSIS  One .dockerignore pattern as an anchored regex over a forward-slashed relative path. #>
    [CmdletBinding()]
    [OutputType([regex])]
    param([Parameter(Mandatory)][string]$Pattern)

    $expression = [System.Text.StringBuilder]::new('^')
    $i = 0

    while ($i -lt $Pattern.Length) {
        $character = $Pattern[$i]

        if ($character -eq '*') {
            if ($i + 1 -lt $Pattern.Length -and $Pattern[$i + 1] -eq '*') {
                $i += 2
                if ($i -lt $Pattern.Length -and $Pattern[$i] -eq '/') { $i++ }
                # A trailing `**` takes the rest of the path; otherwise it spans
                # any number of leading segments, including none.
                [void]$expression.Append($(if ($i -ge $Pattern.Length) { '.*' } else { '((.*/)|([^/]*))' }))
                continue
            }
            [void]$expression.Append('[^/]*'); $i++; continue
        }

        if ($character -eq '?') { [void]$expression.Append('[^/]'); $i++; continue }

        [void]$expression.Append([regex]::Escape([string]$character)); $i++
    }

    return [regex]::new($expression.Append('$').ToString())
}

function Test-TrinixContextIgnore {
    <#  .SYNOPSIS  Whether .dockerignore keeps a repository-relative path out of the build context. #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)][string]$RelativePath)

    $patterns = Get-TrinixContextIgnore
    if ($patterns.Count -eq 0) { return $false }

    $candidates = [System.Collections.Generic.List[string]]::new()
    $candidates.Add($RelativePath)
    $cut = $RelativePath.LastIndexOf('/')
    while ($cut -gt 0) {
        $candidates.Add($RelativePath.Substring(0, $cut))
        $cut = $RelativePath.LastIndexOf('/', $cut - 1)
    }

    $ignored = $false
    foreach ($pattern in $patterns) {
        foreach ($candidate in $candidates) {
            if ($pattern.Regex.IsMatch($candidate)) { $ignored = -not $pattern.Negate; break }
        }
    }
    return $ignored
}

function Test-TrinixContextIgnoreReentrant {
    <#  .SYNOPSIS  Whether any `!` pattern could re-include something beneath an excluded directory. #>
    [CmdletBinding()]
    [OutputType([bool])]
    param([Parameter(Mandatory)][string]$RelativePath)

    foreach ($pattern in (Get-TrinixContextIgnore)) {
        if (-not $pattern.Negate) { continue }
        if ($pattern.Text.StartsWith('**') -or $pattern.Text.StartsWith("$RelativePath/")) { return $true }
    }
    return $false
}

function Get-TrinixContextFile {
    <#
        .SYNOPSIS  Every repository file a COPY of $Path would send, relative to the root, ordinal-sorted.
        .DESCRIPTION
            A directory contributes its whole recursive contents — `base/recipes`
            is 72 files across 58 recipes, and a gate that hashed only the
            directory's name would be no gate at all. Symlinks are hashed by the
            content they resolve
            to rather than as links, and file modes are not hashed: every script
            here is `chmod +x`-ed inside the container, so a host mode change
            cannot alter the build.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory)][string]$Path)

    $root = Get-TrinixRoot
    $prefix = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar).Length + 1
    $relativeOf = { param($p) $p.Substring($prefix) -replace '\\', '/' }

    $files = [System.Collections.Generic.List[string]]::new()

    if ([System.IO.File]::Exists($Path)) {
        $relative = & $relativeOf $Path
        if (-not (Test-TrinixContextIgnore -RelativePath $relative)) { $files.Add($relative) }
        return $files.ToArray()
    }

    if (-not [System.IO.Directory]::Exists($Path)) { throw "Not a build-context path: $Path" }

    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)

    while ($pending.Count -gt 0) {
        foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($pending.Pop())) {
            $relative = & $relativeOf $entry
            $isDirectory = [System.IO.Directory]::Exists($entry)

            if (Test-TrinixContextIgnore -RelativePath $relative) {
                if (-not $isDirectory) { continue }
                if (-not (Test-TrinixContextIgnoreReentrant -RelativePath $relative)) { continue }
            }

            if ($isDirectory) { $pending.Push($entry) } else { $files.Add($relative) }
        }
    }

    $files.Sort([System.StringComparer]::Ordinal)
    return $files.ToArray()
}

function Get-TrinixDockerfileInput {
    <#  .SYNOPSIS  Every repository file a Dockerfile COPYs in, relative to the root, ordinal-sorted and deduplicated. #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory)][string]$Path)

    $files = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($source in (Get-TrinixDockerfileCopySource -Path $Path)) {
        foreach ($file in (Get-TrinixContextFile -Path $source)) { [void]$files.Add($file) }
    }
    return @($files)
}

function Get-TrinixDockerfileInputsHash {
    <#
        .SYNOPSIS  SHA-256 over the contents of every repository file a Dockerfile COPYs in, as `sha256:<hex>`.
        .DESCRIPTION
            Hashed as a manifest of `<digest>  <path>` lines so that a rename,
            an addition and a deletion all move the result, not only an edit.
            Line endings are spelled `\n` explicitly: an Environment.NewLine
            here would make the same tree hash differently on Windows.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Path)

    $root = Get-TrinixRoot
    $manifest = [System.Text.StringBuilder]::new()

    foreach ($file in (Get-TrinixDockerfileInput -Path $Path)) {
        $bytes = [System.IO.File]::ReadAllBytes((Join-Path $root ($file -replace '/', [System.IO.Path]::DirectorySeparatorChar)))
        $digest = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        [void]$manifest.Append("$digest  $file`n")
    }

    $content = [System.Text.Encoding]::UTF8.GetBytes($manifest.ToString())
    return 'sha256:' + [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($content)).ToLowerInvariant()
}

# --- Named build contexts ---------------------------------------------------

<#
    The inputs that are not in the repository.

    Three directories reach a build as BuildKit *named contexts*, wired up in
    scripts/build.ps1 and referenced from a Dockerfile as `--from=<name>` (or
    `--mount=...,from=<name>`). They are not repository paths, so the COPY-inputs
    walk above skips them by construction — and for a while nothing else looked
    at them either. The symptom: adding a trust anchor left `trinix/base-arm64`
    judged current, so `-Stage image`, which rebuilds nothing below itself, was
    told the chain was fine and shipped an image without the new root.

    Whether a context needs hashing is one question asked once per context: can
    a change to it alter the built image without something else already moving?
    The answers are not the same, so they are not hashed the same.

      • `trust=` — YES, hashed. Get-TrinixTrustAnchor is the sole statement of
        what goes into the store, and both the staging copy and the hash read
        it, so the two cannot drift. Hashed from `signing/` rather than from
        the staged output on purpose: Update-TrinixTrustStore *stages* as a side
        effect, and a read-only staleness check that rewrites out/pki/roots is
        a check that is unsafe to run.

        (Note it cannot be hashed via the COPY-inputs walk even in principle:
        .dockerignore excludes `signing/**/*.pem` — no key material in a build
        context, ever — so that walk sees an empty directory. The anchors reach
        an image only through this context.)

      • `sources=` — NO, and not for cost alone, though ~600 MB of tarballs is
        reason enough to look for another answer. Nothing in the cache can
        change what is built: every consumer goes through `trinix-fetch`
        (`trinix-extract` and the recipes included), which re-verifies the
        sha256 from base/sources.json on *every* use, cache hit or not, and
        dies on a mismatch. No script reads $TRINIX_SOURCES by path. So a
        swapped or corrupted tarball fails the build loudly rather than
        altering the image quietly, and the only thing that can legitimately
        change what a stage downloads is base/sources.json — which is COPYed
        in, and therefore already in the inputs hash. Verified against the
        scripts, not taken from the comment that claimed it.

      • `apps=` — NO, and hashing it would make the gate worse. The .tdi files
        are outputs of the app stage, whose entire repository input (global.json,
        vendor, src) base.Dockerfile COPYs in as well — so their source is
        already in base's inputs hash, and hashing the artefacts adds no
        coverage of it. What it would add is churn: a .tdi is deliberately not
        reproducible (an ECDSA signature is randomised and the manifest records
        when it was signed — see scripts/check-determinism.ps1, which gates the
        bundle *contents* for exactly this reason), so any real app rebuild
        moves the bytes with no source change behind it, and `base` would be
        stale the moment it finished building.

    ⚠ Which contexts a stage reads is derived from its Dockerfile, for the same
    reason its COPY inputs are: a hand-kept list is correct on the day it is
    written and silently wrong afterwards. A context named in a Dockerfile but
    missing from $script:NamedContext is refused, loudly — so wiring up a fourth
    context cannot quietly go unhashed the way these three did.
#>

# `FROM <image> AS <name>`. A `--from=` naming one of these is another stage of
# the same file, already covered by that file's own content hash.
$script:DockerfileStageName = [regex]::new('(?im)^\s*FROM\s+\S+\s+AS\s+(?<name>\S+)')

# `--from=<ref>` on a COPY, and `,from=<ref>` inside a `--mount=`. Anchored on
# the `--` or `,` that BuildKit requires, so `from=` occurring in shell text
# inside a RUN is not mistaken for one. Over-matching costs a named throw below
# rather than a silent miss, which is the trade this file makes everywhere.
$script:DockerfileContextRef = [regex]::new('(?i)(?:--|,)from=(?<name>[^,\s]+)')

# Stands in for a context whose digest is deliberately not taken. Recorded
# rather than omitted so the label still says which contexts the stage read:
# adding or removing one moves the hash even when nothing is hashed.
$script:NamedContextUnhashed = 'not-hashed'

# Every named context build.ps1 can hand to a build, and how the gate judges it.
# `Hash` is a scriptblock returning `sha256:<hex>`, or $null for the contexts
# the block comment above argues need none.
$script:NamedContext = [ordered]@{
    'sources' = [pscustomobject]@{ Hash = $null }
    'trust'   = [pscustomobject]@{ Hash = { Get-TrinixTrustAnchorHash } }
    'apps'    = [pscustomobject]@{ Hash = $null }
}

function Get-TrinixDockerfileContextName {
    <#
        .SYNOPSIS  The named build contexts a Dockerfile reads, ordinal-sorted.
        .DESCRIPTION
            Internal stages of the same file and `${ARG}`-resolved image refs
            are not contexts and are dropped; anything left that the gate does
            not recognise is an error rather than a guess.
    #>
    [CmdletBinding()]
    [OutputType([string[]])]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) { throw "No such Dockerfile: $Path" }
    $relative = Get-TrinixRelativePath -Path $Path
    $text = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($Path)).TrimStart([char]0xFEFF)

    # Comments dropped first: these Dockerfiles are half prose, and a `--from=`
    # in a sentence about a `--from=` is not a build input.
    $body = Get-TrinixDockerfileContent -Text $text

    $internal = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($match in $script:DockerfileStageName.Matches($body)) { [void]$internal.Add($match.Groups['name'].Value) }

    $contexts = [System.Collections.Generic.SortedSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($match in $script:DockerfileContextRef.Matches($body)) {
        $name = $match.Groups['name'].Value
        # `--from=${TOOLCHAIN_IMAGE}` names an image, and an image's identity is
        # already the base content id.
        if ($name.Contains('$')) { continue }
        if ($internal.Contains($name)) { continue }
        [void]$contexts.Add($name)
    }

    foreach ($name in $contexts) {
        if (-not $script:NamedContext.Contains($name)) {
            throw "$relative reads the named build context '$name', which this gate knows nothing about. Add it to `$script:NamedContext — with a hash, or with the argument for why it needs none — rather than letting a stage's inputs go unhashed."
        }
    }

    return @($contexts)
}

function Get-TrinixNamedContextHash {
    <#
        .SYNOPSIS  SHA-256 over the named build contexts a Dockerfile reads, as `sha256:<hex>`.
        .DESCRIPTION
            A manifest of `<digest>  <name>` lines, ordinal-sorted by name, in
            the same shape as Get-TrinixDockerfileInputsHash. A Dockerfile that
            reads no named context hashes an empty manifest, which is a fixed
            value that then moves the first time one is added.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][string]$Path)

    $manifest = [System.Text.StringBuilder]::new()

    foreach ($name in (Get-TrinixDockerfileContextName -Path $Path)) {
        $context = $script:NamedContext[$name]
        $digest = if ($context.Hash) { & $context.Hash } else { $script:NamedContextUnhashed }
        [void]$manifest.Append("$digest  $name`n")
    }

    $content = [System.Text.Encoding]::UTF8.GetBytes($manifest.ToString())
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
        '--label', "$($script:ProvenanceLabel.Inputs)=$(Get-TrinixDockerfileInputsHash -Path $Dockerfile)",
        '--label', "$($script:ProvenanceLabel.Contexts)=$(Get-TrinixNamedContextHash -Path $Dockerfile)",
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

    # What the Dockerfile COPYs in. Only checked when the image says it recorded
    # it: an image stamped before this label existed has nothing to compare, and
    # inventing a comparison for it would fail every one of them at once and
    # demand the rebuild chain this gate exists to make unnecessary. It is not
    # forgiveness — the image is still judged on everything it *was* stamped
    # for, and carries this the next time it is genuinely rebuilt.
    if ($labels.ContainsKey($script:ProvenanceLabel.Inputs)) {
        $recordedInputs = $labels[$script:ProvenanceLabel.Inputs]
        $currentInputs = Get-TrinixDockerfileInputsHash -Path $Dockerfile
        if ($recordedInputs -ne $currentInputs) {
            $reasons.Add("was built from older copies of the files $relative COPYs in (recorded $recordedInputs, current $currentInputs).")
        }
    }

    # The named build contexts, on the same terms and for the same reason: only
    # checked when the image says it recorded it, so adding this label does not
    # invalidate every image already standing. This is the check that was
    # missing when a new trust anchor left base-<arch> looking current.
    if ($labels.ContainsKey($script:ProvenanceLabel.Contexts)) {
        $recordedContexts = $labels[$script:ProvenanceLabel.Contexts]
        $currentContexts = Get-TrinixNamedContextHash -Path $Dockerfile
        if ($recordedContexts -ne $currentContexts) {
            $names = (Get-TrinixDockerfileContextName -Path $Dockerfile) -join ', '
            $reasons.Add("was built against an older named build context — $relative reads $names (recorded $recordedContexts, current $currentContexts).")
        }
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

function Get-TrinixTrustAnchor {
    <#
        .SYNOPSIS  The roots that go into the system image, as staged-name → source path.
        .DESCRIPTION
            Two sources, deliberately separate. signing/trusted/ holds anchors
            that are committed — a CI root, eventually a release root — and
            signing/local/ holds the one this machine generated.

            The one statement of what the `trust` build context contains.
            Update-TrinixTrustStore stages exactly this and
            Get-TrinixTrustAnchorHash hashes exactly this, so the gate cannot
            drift into judging a set of roots other than the one a build ships —
            which is the only thing that makes hashing the source directories
            rather than the staged output defensible.

            Reads only. The staleness gate calls it on every invocation, and a
            read-only check that staged files as a side effect would be a check
            nobody could afford to run.

            Ordinal-sorted by staged name, later source winning a name
            collision — which is the overwrite copying both into one directory
            performs.
        .OUTPUTS  A sorted name → absolute path dictionary; empty is a legal answer.
    #>
    [CmdletBinding()]
    param()

    $sources = @(
        (Join-Path (Get-TrinixRoot) 'signing' 'trusted'),
        (Get-TrinixSigningDirectory)
    )

    $anchors = [System.Collections.Generic.SortedDictionary[string, string]]::new([System.StringComparer]::Ordinal)
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

            $anchors[$certificate.Name] = $certificate.FullName
        }
    }

    return $anchors
}

function Get-TrinixTrustAnchorHash {
    <#
        .SYNOPSIS  SHA-256 over the trust anchors a build would stage, as `sha256:<hex>`.
        .DESCRIPTION
            A manifest of `<digest>  <staged name>` lines, so a new anchor, a
            removed one, a renamed one and a reissued one all move the result.

            An empty set hashes rather than throws, unlike the staging path: a
            fresh clone has no PKI until the first build makes one, and the gate
            runs before that. The empty hash simply will not match what a
            stamped image recorded, which reports the trust store as changed —
            a reason, which is the gate working, rather than a crash.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param()

    $manifest = [System.Text.StringBuilder]::new()

    foreach ($anchor in (Get-TrinixTrustAnchor).GetEnumerator()) {
        $bytes = [System.IO.File]::ReadAllBytes($anchor.Value)
        $digest = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        [void]$manifest.Append("$digest  $($anchor.Key)`n")
    }

    $content = [System.Text.Encoding]::UTF8.GetBytes($manifest.ToString())
    return 'sha256:' + [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($content)).ToLowerInvariant()
}

function Update-TrinixTrustStore {
    <#
        .SYNOPSIS  Stage the roots that go into the system image into one directory.
        .DESCRIPTION
            Staged into a single directory because a build context is a
            directory, and because "the set of roots this image trusts" should
            be one list that can be read at a glance rather than two rules in a
            Dockerfile. What goes in it is Get-TrinixTrustAnchor's answer and
            nothing else.
        .OUTPUTS  The staging directory, for use as a named build context.
    #>
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$OutputDirectory)

    $anchors = Get-TrinixTrustAnchor
    if ($anchors.Count -eq 0) {
        throw "No trust anchors found. Expected at least signing/local/dev-root.pub.pem."
    }

    $staging = Join-Path $OutputDirectory 'pki' 'roots'
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    foreach ($anchor in $anchors.GetEnumerator()) {
        Copy-Item -LiteralPath $anchor.Value -Destination (Join-Path $staging $anchor.Key)
    }

    return (Resolve-Path $staging).Path
}

Export-ModuleMember -Function `
    Get-TrinixRoot, Get-TrinixSourceCache, Get-TrinixArch, `
    Get-TrinixSourceManifestPath, Get-TrinixSource, Resolve-TrinixSourceUrl, Resolve-TrinixSourceFileName, Set-TrinixSourceChecksum, `
    Assert-TrinixDocker, Assert-TrinixDiskSpace, Invoke-TrinixDocker, `
    Get-TrinixDockerfileContent, Get-TrinixDockerfileHash, Get-TrinixDockerfileByteHash, `
    Get-TrinixDockerfileCopyInstruction, Get-TrinixDockerfileCopySource, `
    Get-TrinixDockerfileInput, Get-TrinixDockerfileInputsHash, Test-TrinixContextIgnore, `
    Get-TrinixDockerfileContextName, Get-TrinixNamedContextHash, `
    Get-TrinixImageContentId, Get-TrinixImageLabel, Get-TrinixRelativePath, `
    New-TrinixProvenanceLabel, Test-TrinixStageProvenance, `
    Get-TrinixStage, Resolve-TrinixExternalBase, Get-TrinixStageChain, Get-TrinixStageLabelArgs, Assert-TrinixStageCurrent, `
    Get-TrinixSigningDirectory, Get-TrinixSigningIdentity, Assert-TrinixSigningIdentity, `
    Get-TrinixTrustAnchor, Get-TrinixTrustAnchorHash, Update-TrinixTrustStore
