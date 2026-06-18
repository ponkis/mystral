# Mystral

Mystral is a Windows desktop music companion built with WPF. It reads the active Windows media session, shows playback controls, lyrics, Last.fm links, optional Last.fm scrobbling, and a tray menu for quick actions.

## Requirements

- Windows 10 1809 or newer.
- .NET SDK 8.0 or newer for development.
- Inno Setup 6 for creating the Windows installer.
- A Last.fm API account for Last.fm links and scrobbling (optional)

## First Setup

1. Restore and build the project:

   ```powershell
   dotnet restore .\Mystral.csproj
   dotnet build .\Mystral.csproj
   ```

2. Run the app from Visual Studio, Rider, or:

   ```powershell
   dotnet run --project .\Mystral.csproj
   ```

3. Open `Settings` from the app menu or tray menu.

4. Turn on and configure Last.fm under the `Last.fm` category:

   - API key
   - API secret
   - Username
   - Password
   - `Scrobble playback to Last.fm`, if wanted

The app validates Last.fm credentials when settings are saved. Scrobbling uses Last.fm `auth.getMobileSession`, `track.updateNowPlaying`, and `track.scrobble`.

## Testing

The automated tests live in `tests\Mystral.Tests`. They use a small console runner instead of a test framework, so there are no extra test packages to restore.

Run the core test suite:

```powershell
dotnet restore .\tests\Mystral.Tests\Mystral.Tests.csproj
dotnet run --project .\tests\Mystral.Tests\Mystral.Tests.csproj --no-restore
```

If NuGet is unavailable but the SDK packs are already cached locally, restore from the local package cache:

```powershell
dotnet restore .\tests\Mystral.Tests\Mystral.Tests.csproj --source "$env:USERPROFILE\.nuget\packages"
```

The test suite covers the vital headless app logic:

- LRC parsing and lyric result handling
- Last.fm metadata cleanup, filtering, API requests, signatures, caching, and scrobbling paths
- LRCLIB search ranking, parsing, and caching
- settings persistence and corrupt JSON fallback
- local scrobble history add, remove, clear, corrupt file, and 10,000 item cap
- model defaults and artwork tint edge cases

Before a release, also smoke-test the Windows-only shell manually: WPF window states, tray behavior, media session controls, system volume controls, notifications, and installer output.

## Settings Storage

Debug builds store settings in:

```text
%LOCALAPPDATA%\Mystral Development\settings.json
```

Release builds store settings in:

```text
%LOCALAPPDATA%\Mystral\settings.json
```

## Versioning

The project version is centralized in `Directory.Build.props`:

```xml
<VersionPrefix>1.1.1</VersionPrefix>
```

To bump the app version, edit `VersionPrefix`. Debug builds automatically use a `-dev` suffix. Release builds use the plain version.

## Development And Production

The build environment is selected through MSBuild:

- Debug defaults to `AppEnvironment=Development`.
- Release defaults to `AppEnvironment=Production`.

```powershell
dotnet build .\Mystral.csproj -c Debug /p:AppEnvironment=Development
dotnet build .\Mystral.csproj -c Release /p:AppEnvironment=Production
```

## Release Builds

Development builds are created locally from the `dev` branch. They use Release optimizations with `AppEnvironment=Development`, so you can test packaged builds without touching production settings.

Build and run a local development build:

```powershell
.\scripts\Build-Dev.ps1 -Clean -Run
```

The dev build is written to:

```text
artifacts\dev\Mystral-<version>-dev-win-x64-folder\Mystral.exe
```

Production releases are built by GitHub Actions from version tags on `main`:

```powershell
$version = dotnet msbuild .\Mystral.csproj -nologo -getProperty:Version -p:Configuration=Release
git tag "v$version"
git push origin "v$version"
```

The production release workflow validates that the tag matches the MSBuild Release version, publishes self-contained `win-x64` builds, creates the Inno Setup installer, generates SHA-256 checksums, and attaches the assets to a GitHub Release.

The workflow can also be started manually from GitHub Actions if you need to rebuild a release from the current commit.

Recommended branch flow:

```text
work on dev -> build locally -> commit and push dev -> merge dev into main -> vX.Y.Z tag -> GitHub Release
```

After testing locally, you can merge `dev` into `main` and push with:

```powershell
.\scripts\Promote-DevToMain.ps1
```

To merge, push `main`, and create the production release tag in one step:

```powershell
.\scripts\Promote-DevToMain.ps1 -Release
```

For local release builds, run these commands from the repository root.

Load the project version:

```powershell
$version = dotnet msbuild .\Mystral.csproj -nologo -getProperty:Version -p:Configuration=Release
```

Clean old release artifacts:

```powershell
Remove-Item .\artifacts -Recurse -Force -ErrorAction SilentlyContinue
```

Debug verification build:

```powershell
dotnet build .\Mystral.csproj -c Debug /p:AppEnvironment=Development
```

Self-contained single-file build:

```powershell
dotnet publish .\Mystral.csproj -c Release -r win-x64 --self-contained true -o ".\artifacts\publish\Mystral-$version-win-x64-single" /p:AppEnvironment=Production /p:PublishSingleFile=true /p:IncludeAllContentForSelfExtract=true /p:EnableCompressionInSingleFile=true /p:UseAppHost=true /p:DebugType=None /p:DebugSymbols=false
```

Self-contained folder build:

```powershell
dotnet publish .\Mystral.csproj -c Release -r win-x64 --self-contained true -o ".\artifacts\publish\Mystral-$version-win-x64-folder" /p:AppEnvironment=Production /p:PublishSingleFile=false /p:UseAppHost=true /p:DebugType=None /p:DebugSymbols=false
```

The folder build is what the installer script packages.

## Installer

Install Inno Setup:

```powershell
winget install JRSoftware.InnoSetup
```

Build the folder publish first, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\Build-Installer.ps1
```

The build script resolves the version from MSBuild and uses `C:\Program Files (x86)\Inno Setup 6\ISCC.exe` when `ISCC.exe` is not on `PATH`.

The installer is written to:

```text
artifacts\installer\Mystral-<version>-win-x64-setup.exe
```

## Runtime Assets

All runtime assets are inside this project under `Resources`.
