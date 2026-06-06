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
    Invoke-Git merge --no-ff $DevBranch -m "Merge dev into main"
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
