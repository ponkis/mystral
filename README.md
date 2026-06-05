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
   dotnet restore .\Mystral\Mystral.csproj
   dotnet build .\Mystral\Mystral.csproj
   ```

2. Run the app from Visual Studio, Rider, or:

   ```powershell
   dotnet run --project .\Mystral\Mystral.csproj
   ```

3. Open `Settings` from the app menu or tray menu.

4. Turn on and configure Last.fm under the `Last.fm` category:

   - API key
   - API secret
   - Username
   - Password
   - `Scrobble playback to Last.fm`, if wanted

The app validates Last.fm credentials when settings are saved. Scrobbling uses Last.fm `auth.getMobileSession`, `track.updateNowPlaying`, and `track.scrobble`.

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
<VersionPrefix>1.0.0</VersionPrefix>
```

To bump the app version, edit `VersionPrefix`. Debug builds automatically use a `-dev` suffix. Release builds use the plain version.

## Development And Production

The build environment is selected through MSBuild:

- Debug defaults to `AppEnvironment=Development`.
- Release defaults to `AppEnvironment=Production`.

```powershell
dotnet build .\Mystral\Mystral.csproj -c Debug /p:AppEnvironment=Development
dotnet build .\Mystral\Mystral.csproj -c Release /p:AppEnvironment=Production
```

## Release Builds

Run these from the repository root.

Clean old release artifacts:

```powershell
Remove-Item .\artifacts -Recurse -Force -ErrorAction SilentlyContinue
```

Debug verification build:

```powershell
dotnet build .\Mystral\Mystral.csproj -c Debug /p:AppEnvironment=Development
```

Self-contained single-file build:

```powershell
dotnet publish .\Mystral\Mystral.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\publish\Mystral-1.0.0-win-x64-single /p:AppEnvironment=Production /p:PublishSingleFile=true /p:IncludeAllContentForSelfExtract=true /p:EnableCompressionInSingleFile=true /p:UseAppHost=true /p:DebugType=None /p:DebugSymbols=false
```

Self-contained folder build:

```powershell
dotnet publish .\Mystral\Mystral.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\publish\Mystral-1.0.0-win-x64-folder /p:AppEnvironment=Production /p:PublishSingleFile=false /p:UseAppHost=true /p:DebugType=None /p:DebugSymbols=false
```

The folder build is what the installer script packages.

## Installer

Install Inno Setup:

```powershell
winget install JRSoftware.InnoSetup
```

Build the folder publish first, then run:

```powershell
iscc .\installer\Mystral.iss
```

The installer is written to:

```text
artifacts\installer\Mystral-1.0.0-win-x64-setup.exe
```

## Runtime Assets

All runtime assets are inside this project under `Mystral\res`.