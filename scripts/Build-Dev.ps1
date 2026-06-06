[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeId = "win-x64",
    [switch]$Clean,
    [switch]$SingleFile,
    [switch]$Run
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $repoRoot "Mystral.csproj"
$configuration = "Release"
$appEnvironment = "Development"
$versionSuffix = "dev"

function Assert-NativeCommandSucceeded {
    param([string]$CommandName)

    if ($LASTEXITCODE -ne 0) {
        throw "$CommandName failed."
    }
}

$version = (
    & dotnet msbuild $projectFile `
        -nologo `
        -getProperty:Version `
        -p:AppEnvironment=$appEnvironment `
        -p:Configuration=$configuration `
        -p:VersionSuffix=$versionSuffix
).Trim()

if ($LASTEXITCODE -ne 0) {
    throw "dotnet msbuild failed while resolving Version."
}

if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not resolve the MSBuild Version property."
}

$publishKind = if ($SingleFile) { "single" } else { "folder" }
$publishDir = Join-Path $repoRoot "artifacts\dev\Mystral-$version-$RuntimeId-$publishKind"

$currentBranch = (& git -C $repoRoot branch --show-current).Trim()
if ($LASTEXITCODE -eq 0 -and $currentBranch -and $currentBranch -ne "dev") {
    Write-Warning "You are building from '$currentBranch', not 'dev'."
}

if ($Clean -and (Test-Path -LiteralPath $publishDir)) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

$publishSingleFile = if ($SingleFile) { "true" } else { "false" }

& dotnet restore $projectFile -r $RuntimeId
Assert-NativeCommandSucceeded "dotnet restore"

& dotnet publish $projectFile `
    -c $configuration `
    -r $RuntimeId `
    --self-contained true `
    --no-restore `
    -o $publishDir `
    -p:AppEnvironment=$appEnvironment `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:PublishSingleFile=$publishSingleFile `
    -p:UseAppHost=true `
    -p:VersionSuffix=$versionSuffix
Assert-NativeCommandSucceeded "dotnet publish"

$exePath = Join-Path $publishDir "Mystral.exe"

Write-Host ""
Write-Host "Development build created:"
Write-Host $exePath
Write-Host ""
Write-Host "Environment: $appEnvironment"
Write-Host "Version:     $version"

if ($Run) {
    Start-Process -FilePath $exePath -WorkingDirectory $publishDir
}
