# Mystral

Mystral is a Windows desktop music companion built with WPF. It reads the active Windows media session, shows playback controls, lyrics, MusicBrainz track, artist, and album information, Last.fm links, optional Last.fm scrobbling, customizable player themes, and a tray menu for quick actions.

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

   - API key and Username — enough for track links and the tray profile link
   - `Scrobble playback to Last.fm`, if wanted
   - API secret and Password — required only when scrobbling is enabled

The app validates Last.fm credentials when settings are saved: the API key and
username alone unlock the viewer features, while enabling scrobbling also
requires the API secret and account password (the tray toggle enforces the same
rule and stays in sync with an open Settings window). Scrobbling uses Last.fm
`auth.getMobileSession`, `track.updateNowPlaying`, and `track.scrobble`.

Under `Behavior`, choose how the burn editor looks up lyrics:

- `MusicBrainz (default)` first matches the song on MusicBrainz and uses that
  metadata to refine the LRCLIB lyric search.
- `LRCLIB` searches LRCLIB directly with the title, artist, album, and duration
  currently shown in the burn editor.

Lyrics always come from LRCLIB (MusicBrainz does not host lyric text), and
neither burn-lyrics mode requires an API key.

Under `Appearance`, the player theme can follow each track's artwork automatically
or use a color chosen from the theme color picker (a color wheel plus a hex
field). The live player previews the color while picking, and an accepted color
stays applied to the player until the settings are saved (making it permanent)
or the settings window closes without saving (reverting it). A custom color
becomes the fixed tint for the main player and removes its cover-derived blurred
backgrounds while the expanded view keeps showing the cover itself; the burn
editor and track notifications continue using their own automatic artwork tint.
Returning the theme to automatic restores the artwork tint and backgrounds.

Mystral's custom title bars place the available Close and Minimize controls on
the left, keep window-specific actions such as Always on top and Fullscreen on
the right, and center the app icon and name where that identity is shown. In
fullscreen, Close is hidden and the Fullscreen action becomes the exit control.

The playback timeline uses the media session's own update timestamp so Spotify
and browser sessions do not rewind when Windows repeats a stale position.
Progress and volume bars use their full hand-cursor surface for click-to-jump or
dragging, with an immediate tooltip that lingers after release. Volume applies
continuously; progress previews the target and commits the seek on release. The
tooltip remains anchored to the slider handle as playback advances and when a
bar moves between player layouts.

For Apple Music's Windows media session, lyric lookup separates a combined
`Artist — Album` value when the session omits its album field, and uses the
session's album artist when its primary artist field is empty.

When the playing album has an Apple Music animated cover, Mystral downloads the
animation once and fades it in over the static cover in the compact, expanded,
lyrics-header, and fullscreen art views, looping it while the album keeps
playing. When the album changes, Mystral keeps the outgoing frame only while
Windows is still returning the previous thumbnail. As soon as the new track's
regular cover is ready it replaces that frame directly; a new animation can
then take over once loaded. This keeps intermediate media-session thumbnails
from flashing without leaving the old cover behind. Animated covers are
resolved through the `artwork.m8tec.top` lookup service and cached in the user's
temp directory, where Windows disk cleanup can reclaim them.

Synchronized lyric lines become seek targets when the active media session
allows seeking; plain lyrics remain read-only text. In the regular,
information-side, and fullscreen views, the active synchronized line uses the
same three-layer `cd_thing.png` glow as the playing row in an album track list;
inactive and plain lines remain unframed. Wrapped active lines stay below the
regular lyrics header instead of clipping against the top of the viewport.
During automatic following, completed lines leave the regular lyrics viewport;
scrolling restores them for browsing.
When a looping track restarts after
reaching its end, lyric browsing resets in all three views. Lyrics mode sits on the player's translucent glass surface and
uses one cover-derived backdrop plus its header artwork, while a fixed custom
theme continues to hide the backdrop.

## Music Information

While a track is playing, open `More` and choose `Track information`, `Artist
information`, or `Album information`. The player unfolds into an attached Aero
information surface: the current cover lifts out and grows, then retraces that
motion to its exact compact position while the compact glass dissolves smoothly
back around it. The existing playback pill stays in its
upper-right position, while the playback timeline moves into
the open space directly beneath the details without a surrounding panel,
divider, or a large empty footer. Equal 12-DIP spacing separates the timeline
from both the information box and the rounded lower edge. The pill follows the selected information page's glass
tint, including an available artist portrait. While information remains open,
the Lyrics control slides a narrow glass pane out from beneath its right side without
changing the selected Track, Artist, or Album page. Its left edge stays tucked
beneath the information sheet, so the pane meets it cleanly while retaining its
own cover-tinted glass. The attached pane follows
synchronized lines, supports scrolling and seekable line clicks, and retracts
under the sheet with the reverse slide. Its lines begin at the same compact top
inset as regular lyrics, shares their active-line glow, and the
last line moves up so its song details remain visible below it. The window
controls remain visible and move into a squared-bottom tab whose bottom edge sits on the top border rather than
overlapping the sheet. The tab samples the sheet's rendered, cover-derived glass
material and reuses its border instead of drawing a separate solid tint, and its button
highlights remain joined to the strip with rounding only at the two outer top
corners. The more transparent glass sheet places the Track,
Artist, and Album tabs along the content's left edge below the lifted artwork,
with the selected tab opening into the otherwise outlined content area. Track identity and matched
details use a larger, aligned presentation. The views focus on essential credits,
dates, genres, and a compact, divided album track list; multi-artist recordings
also let you choose which credited artist to view. The Artist page promotes the
artist name and first available alias into the header. The Album page promotes
the album name into the header and the current track into its subtitle. In an album track list,
only the currently playing recording receives the vertically stretched
`cd_thing.png` highlight, framed by original-height copies of the same fade at
its top and bottom; the remaining rows stay plain. There is no source label or
external-link footer.

The details come from MusicBrainz and do not require an account or API key.
The Album view keeps the cover already supplied by the active player instead of
downloading a nearly identical replacement. Searches consider both an artist's
current name and the name credited on the recording, and tolerate common
featured-credit, punctuation, accent, and store-edition differences while still
requiring a confident match. Temporary lookup failures can be retried
immediately and are tried again automatically after a short pause while the
information surface remains open. When an artist
has a MusicBrainz image link, Mystral shows its validated Wikimedia Commons
thumbnail without overlaying a caption; source details remain available on
hover. Otherwise it keeps a simple initials tile.

## Burn Editor

The burn editor always writes a separate copy of the selected audio file. It can
edit track details, cover and disc artwork, plain lyrics, and synchronized lyrics
in LRC format; the source audio is never modified. Synchronized lyrics use a
native synchronized tag when the file's tagging format supports one and fall
back to portable timestamped LRC text otherwise.

`Fetch song data` retrieves metadata and artwork through MusicBrainz and the
Cover Art Archive, and retrieves plain or synchronized lyrics through LRCLIB.
Fetched lyrics remain editable before the copy is saved. The whole fetch runs
under one connection deadline, so a dropped connection surfaces a timeout
message instead of stalling. The CD artwork tile's right-click menu can also
save the transparent disc guide image for aligning artwork in an editor.

## Updates

Mystral can check GitHub releases at startup or from the About dialog. Update
downloads show progress and can be canceled; Mystral confirms that a canceled
download did not launch the installer. If a download is interrupted or fails,
the error dialog reports the underlying cause and offers Retry. After an update,
the confirmation popup links to the GitHub comparison for the previous and new
release.

## Testing

The automated tests live in `tests\Mystral.Tests`. They use a small console runner instead of a test framework, so there are no extra test packages to restore.

Run the core test suite:

```powershell
dotnet restore .\tests\Mystral.Tests\Mystral.Tests.csproj
dotnet run --project .\tests\Mystral.Tests\Mystral.Tests.csproj --no-restore
```

GitHub Actions runs the same core checks on pushes and pull requests in `.github\workflows\ci.yml`.

If NuGet is unavailable but the SDK packs are already cached locally, restore from the local package cache:

```powershell
dotnet restore .\tests\Mystral.Tests\Mystral.Tests.csproj --source "$env:USERPROFILE\.nuget\packages"
```

The test suite covers the vital headless app logic:

- LRC parsing and lyric result handling
- Last.fm metadata cleanup, filtering, API requests, signatures, caching, and scrobbling paths
- LRCLIB exact lookup, Apple Music metadata normalization, fallback search ranking, parsing, and caching
- settings persistence, player-theme and burn-lyrics defaults, and corrupt JSON fallback
- local scrobble history add, remove, clear, corrupt file, and 10,000 item cap
- media-session timestamp projection, stale-position filtering, seek reconciliation, and loop-restart detection
- model defaults and artwork tint edge cases
- MusicBrainz recording, artist/image-relation, and multi-disc release mapping, confidence selection, artist/release artwork security bounds, outcomes, and failure handling
- burn metadata validation, lyric-tag round trips, and preservation of the source audio
- interrupted update downloads, partial-file cleanup, and failure-message handling

Before a release, also run the Windows-only checklist in `SMOKE_TEST.md`: WPF window states, tray behavior, media session controls, system volume controls, notifications, and installer output.

## Settings Storage

Debug builds store settings in:

```text
%LOCALAPPDATA%\Mystral Development\settings.json
```

Release builds store settings in:

```text
%LOCALAPPDATA%\Mystral\settings.json
```

Last.fm credentials and the globe bearer token are never written to
`settings.json`. They are stored in the environment-specific `credentials`
directory and encrypted for the current Windows user with DPAPI. Existing
plaintext Last.fm fields are migrated to that protected store the next time
settings are loaded successfully.

## Versioning

The project version is centralized in `Directory.Build.props`:

```xml
<VersionPrefix>2.3.0</VersionPrefix>
```

To bump the app version, edit `VersionPrefix`. Debug builds automatically use a `-dev` suffix. Release builds use the plain version.

## Development And Production

The build environment is selected through MSBuild:

- Debug defaults to `AppEnvironment=Development`.
- Release defaults to `AppEnvironment=Production`.

Build outputs are isolated by both values under
`bin\<Configuration>\<AppEnvironment>` so a Development executable cannot be
silently replaced by a Production-flavored build in the same folder.

Development builds connect to `http://localhost:3000/` and register
`mystral-dev://settings/social`. Set `MYSTRAL_GLOBE_BASE_URL` when the local
globe server uses another origin. Production builds always use
`https://chat.ponkis.xyz/` and `mystral://settings/social`.

Avatar downloads are restricted to the globe origin and globe's current
first-party R2 origin, both embedded as exact allowlist entries. If the image
CDN changes, set the GitHub Actions repository variable
`GLOBE_AVATAR_CDN_URL` to the new exact public base URL before creating a
release; the release workflow embeds that override with `GlobeAvatarCdnUrl`.
Development can override it with `MYSTRAL_GLOBE_AVATAR_CDN_URL` when testing a
different CDN.

The development packaging script registers the published executable without
opening a window. A regular Debug build also registers itself when launched,
or it can be registered explicitly without starting the UI. Run this from the
same normal Windows account as the browser (not an elevated/admin shell),
because the handler is stored in that user's `HKCU` registry hive:

```powershell
.\scripts\Register-DevProtocol.cmd
```

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

`main` keeps contributor-facing versions of `README.md` and `SMOKE_TEST.md`.
The promotion script preserves those two files from `main`, including when they
are the only merge conflicts, so development notes cannot replace the public
documentation. Update the public copies on `main` before promoting a release.

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
Licenses for the Appearance color-picker dependencies are listed in
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
