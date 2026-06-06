[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RuntimeId = "win-x64",
    [string]$ProjectFile,
    [string]$InnoScript,
    [string]$InnoCompiler
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scriptRoot = if ($PSScriptRoot) {
    $PSScriptRoot
} else {
    Split-Path -Parent $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($ProjectFile)) {
    $ProjectFile = Join-Path $scriptRoot "..\Mystral.csproj"
}

if ([string]::IsNullOrWhiteSpace($InnoScript)) {
    $InnoScript = Join-Path $scriptRoot "Mystral.iss"
}

$projectPath = (Resolve-Path -LiteralPath $ProjectFile).Path
$repoRoot = Split-Path -Parent $projectPath

$version = (
    dotnet msbuild $projectPath `
        -nologo `
        -getProperty:Version `
        -p:Configuration=$Configuration
).Trim()

if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not resolve the MSBuild Version property."
}

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$pathCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue

$candidateCompilers = @(
    $InnoCompiler,
    $(if ($programFilesX86) { Join-Path $programFilesX86 "Inno Setup 6\ISCC.exe" }),
    $(if ($programFiles) { Join-Path $programFiles "Inno Setup 6\ISCC.exe" }),
    $(if ($pathCommand) { $pathCommand.Source })
)

$iscc = $candidateCompilers |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1

if (-not $iscc) {
    throw "Could not find ISCC.exe. Install Inno Setup 6 or pass -InnoCompiler."
}

$publishDir = Join-Path $repoRoot "artifacts\publish\Mystral-$version-$RuntimeId-folder"
if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
    throw "Missing publish folder: $publishDir. Build the folder publish first."
}

& $iscc $InnoScript `
    "/DMyAppVersion=$version" `
    "/DMyPublishDir=$publishDir"

$installerDir = Join-Path $repoRoot "artifacts\installer"
Move-Item `
    -LiteralPath (Join-Path $installerDir "MystralSetup.exe") `
    -Destination (Join-Path $installerDir "Mystral-$version-$RuntimeId-setup.exe") `
    -Force
