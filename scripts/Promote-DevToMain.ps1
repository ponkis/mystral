[CmdletBinding()]
param(
    [string]$DevBranch = "dev",
    [string]$MainBranch = "main",
    [string]$Remote = "origin",
    [switch]$Release
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $repoRoot "Mystral.csproj"
$publicDocPaths = @("README.md", "SMOKE_TEST.md")

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') failed."
    }
}

Push-Location $repoRoot
try {
    $status = git status --porcelain
    if ($status) {
        throw "Commit or stash your changes before promoting dev to main."
    }

    Invoke-Git fetch $Remote
    Invoke-Git checkout $MainBranch
    Invoke-Git pull --ff-only $Remote $MainBranch

    $publicDocs = @{}
    foreach ($relativePath in $publicDocPaths) {
        $fullPath = Join-Path $repoRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "Public document $relativePath is missing from $MainBranch."
        }

        $publicDocs[$relativePath] = [System.IO.File]::ReadAllBytes($fullPath)
    }

    & git merge --no-ff --no-commit $DevBranch
    $mergeExitCode = $LASTEXITCODE
    if ($mergeExitCode -ne 0) {
        $unmergedPaths = @(& git diff --name-only --diff-filter=U)
        if ($LASTEXITCODE -ne 0) {
            throw "Could not inspect merge conflicts."
        }

        $unexpectedConflicts = @(
            $unmergedPaths | Where-Object { $_ -notin $publicDocPaths }
        )
        if ($unmergedPaths.Count -eq 0 -or $unexpectedConflicts.Count -gt 0) {
            $details = if ($unexpectedConflicts.Count -gt 0) {
                $unexpectedConflicts -join ", "
            } else {
                "unknown merge failure"
            }
            throw "Merge requires manual resolution: $details."
        }
    }

    foreach ($relativePath in $publicDocPaths) {
        [System.IO.File]::WriteAllBytes(
            (Join-Path $repoRoot $relativePath),
            $publicDocs[$relativePath])
    }
    Invoke-Git add "--" @publicDocPaths

    $remainingConflicts = @(& git diff --name-only --diff-filter=U)
    if ($LASTEXITCODE -ne 0 -or $remainingConflicts.Count -gt 0) {
        throw "Merge still has unresolved conflicts."
    }

    $mergeHead = git rev-parse --verify -q MERGE_HEAD
    if ($LASTEXITCODE -ne 0 -or -not $mergeHead) {
        throw "Nothing new was merged from $DevBranch."
    }

    Invoke-Git commit -m "Merge dev into main"
    Invoke-Git push $Remote $MainBranch

    if ($Release) {
        $version = (
            & dotnet msbuild $projectFile `
                -nologo `
                -getProperty:Version `
                -p:Configuration=Release
        ).Trim()

        if ($LASTEXITCODE -ne 0) {
            throw "dotnet msbuild failed while resolving Version."
        }

        if ([string]::IsNullOrWhiteSpace($version)) {
            throw "Could not resolve the MSBuild Version property."
        }

        $tag = "v$version"
        $existingTag = git tag --list $tag
        if ($existingTag) {
            throw "Tag $tag already exists. Bump VersionPrefix before releasing."
        }

        Invoke-Git tag $tag
        Invoke-Git push $Remote $tag
    }
} finally {
    Pop-Location
}
