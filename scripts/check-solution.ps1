#!/usr/bin/env pwsh
#requires -Version 7.4
<#
    .SYNOPSIS
        Fail if the set of .csproj under src/ is not exactly the set listed in
        src/Trinix.slnx.

    .DESCRIPTION
        The omission this exists to stop had already happened once. Trinix.Platform and
        Trinix.Apps.HelloUi were in the tree and not in the solution, so they compiled
        only during the image stage — which means a compile error in the Vixen platform
        backend was caught by a two-hour image build rather than by the ninety-second
        `dotnet` job that gates every pull request. Nothing was wrong with either
        project; nothing told anyone they were missing either.

        ⚠ This is a script rather than a unit test, and that is the whole design.
        A test can only run if its own project is in the solution, so a test asserting
        "every project is in the solution" is the one assertion that can be defeated by
        being left out of the thing it checks. The workflow runs this before it restores
        anything, so the check does not depend on the state it is checking.

        It is also runnable by a person, which docs/plan/16-build-ci-and-testing.md
        asks of every gate: CI should run no logic a developer cannot.

    .PARAMETER SolutionPath
        The solution to check. Default: src/Trinix.slnx.

    .EXAMPLE
        ./scripts/check-solution.ps1
        Compare src/ against src/Trinix.slnx and exit non-zero if they disagree.
#>
[CmdletBinding()]
param(
    [string]$SolutionPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'lib' 'Trinix.Build.psm1') -Force

$root = Get-TrinixRoot
$sourceDirectory = Join-Path $root 'src'
$solution = if ($SolutionPath) { $SolutionPath } else { Join-Path $sourceDirectory 'Trinix.slnx' }

if (-not (Test-Path -LiteralPath $solution)) {
    throw "No solution at $solution."
}

# Paths are compared as solution-relative with forward slashes, which is how .slnx
# spells them and how they read in a diff. Get-ChildItem hands back the host's
# separator, so the normalisation happens on the filesystem side rather than by
# rewriting what the solution says.
$onDisk = @(Get-ChildItem -LiteralPath $sourceDirectory -Recurse -Filter '*.csproj' -File |
    ForEach-Object { [System.IO.Path]::GetRelativePath($sourceDirectory, $_.FullName).Replace('\', '/') } |
    Sort-Object -CaseSensitive)

# The <Project Path="..."/> attribute, and nothing else in the file. A regex rather
# than an XML parse because .slnx has no schema worth binding to and because a
# malformed solution should fail here as "no projects found" rather than as an
# XmlException from somewhere three frames down.
$solutionText = Get-Content -Raw -LiteralPath $solution
$inSolution = @([regex]::Matches($solutionText, '<Project\s+Path\s*=\s*"([^"]+)"') |
    ForEach-Object { $_.Groups[1].Value.Replace('\', '/') } |
    Sort-Object -CaseSensitive)

if ($inSolution.Count -eq 0) {
    throw "$solution lists no projects at all — is it still a solution file?"
}

$missing = @($onDisk | Where-Object { $_ -notin $inSolution })
$phantom = @($inSolution | Where-Object { $_ -notin $onDisk })

if ($missing.Count -eq 0 -and $phantom.Count -eq 0) {
    Write-Host "OK: all $($onDisk.Count) projects under src/ are in $(Split-Path -Leaf $solution)."
    exit 0
}

# Both directions are reported at once, and the fix is printed rather than described:
# whoever hits this is adding a project and wants the line to paste, not a lecture.
if ($missing.Count -gt 0) {
    Write-Host "Missing from $(Split-Path -Leaf $solution):" -ForegroundColor Red
    foreach ($project in $missing) {
        Write-Host "    <Project Path=`"$project`"/>"
    }
}

if ($phantom.Count -gt 0) {
    Write-Host "Listed in $(Split-Path -Leaf $solution) but not on disk:" -ForegroundColor Red
    foreach ($project in $phantom) {
        Write-Host "    $project"
    }
}

exit 1
