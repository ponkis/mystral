[CmdletBinding()]
param(
    [string]$ExecutablePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:OS -ne "Windows_NT") {
    throw "The Mystral URL protocol can only be registered on Windows."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $repoRoot "bin\Debug\Development\net8.0-windows10.0.19041.0\Mystral.exe"
}

$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf) -or
    [System.IO.Path]::GetExtension($resolvedExecutable) -ne ".exe") {
    throw "The protocol target must be a Mystral .exe file."
}

$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedExecutable)
if ($versionInfo.ProductName -cne "Mystral" -or
    [string]::IsNullOrWhiteSpace($versionInfo.ProductVersion) -or
    -not $versionInfo.ProductVersion.EndsWith("-dev", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to register mystral-dev:// to a non-development Mystral executable."
}

$scheme = "mystral-dev"
$classesRoot = "HKCU:\Software\Classes"
$schemeKey = Join-Path $classesRoot $scheme
$iconKey = Join-Path $schemeKey "DefaultIcon"
$commandKey = Join-Path $schemeKey "shell\open\command"
$expectedCommand = '"' + $resolvedExecutable + '" "%1"'

[void](New-Item -Path $schemeKey -Force)
Set-Item -LiteralPath $schemeKey -Value "URL:Mystral Development Protocol"
[void](New-ItemProperty -LiteralPath $schemeKey -Name "URL Protocol" -Value "" -PropertyType String -Force)
[void](New-ItemProperty -LiteralPath $schemeKey -Name "MystralRegistrationOwner" -Value "ponkis.mystral.development" -PropertyType String -Force)
[void](New-ItemProperty -LiteralPath $schemeKey -Name "MystralRegistrationTarget" -Value $resolvedExecutable -PropertyType String -Force)

[void](New-Item -Path $iconKey -Force)
Set-Item -LiteralPath $iconKey -Value ('"' + $resolvedExecutable + '",0')

[void](New-Item -Path $commandKey -Force)
Set-Item -LiteralPath $commandKey -Value $expectedCommand

$registeredCommand = (Get-Item -LiteralPath $commandKey).GetValue("")
if ($registeredCommand -cne $expectedCommand) {
    throw "The per-user protocol command could not be verified."
}

$mergedCommandKey = [Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey(
    "$scheme\shell\open\command",
    $false)
$mergedCommand = $null
try {
    if ($null -ne $mergedCommandKey) {
        $mergedCommand = $mergedCommandKey.GetValue($null)
    }
} finally {
    if ($null -ne $mergedCommandKey) {
        $mergedCommandKey.Dispose()
    }
}

if ($mergedCommand -cne $expectedCommand) {
    throw "Windows did not expose the new protocol command through HKEY_CLASSES_ROOT."
}

if (-not ("MystralProtocolRegistration.NativeMethods" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace MystralProtocolRegistration
{
    public static class NativeMethods
    {
        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(
            uint eventId,
            uint flags,
            IntPtr item1,
            IntPtr item2);
    }
}
"@
}

[MystralProtocolRegistration.NativeMethods]::SHChangeNotify(
    0x08000000,
    0,
    [IntPtr]::Zero,
    [IntPtr]::Zero)

Write-Host ""
Write-Host "Registered mystral-dev:// for Windows user $([Environment]::UserName)."
Write-Host "Handler: $registeredCommand"
Write-Host ""
Write-Host "If the browser was already open when the handler was missing, close and reopen it once."
