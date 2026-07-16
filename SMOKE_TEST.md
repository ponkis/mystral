# Mystral Smoke Test

Use this checklist before a release or after changes that touch WPF, media
controls, settings, Last.fm, installer packaging, burn behavior, or startup
behavior.

## Build

```powershell
dotnet build .\Mystral.csproj -c Debug /p:AppEnvironment=Development
dotnet run --project .\tests\Mystral.Tests\Mystral.Tests.csproj -c Debug --no-restore
dotnet build .\Mystral.csproj -c Release /p:AppEnvironment=Production --no-restore
dotnet run --project .\tests\Mystral.Tests\Mystral.Tests.csproj -c Release --no-restore
```

For packaged development builds:

```powershell
.\scripts\Build-Dev.ps1 -Clean
```

## App Launch

- Launch Mystral from Visual Studio, `dotnet run`, or the packaged `Mystral.exe`.
- Confirm the main window appears without startup errors.
- Confirm the app icon appears in the taskbar or tray as expected.
- Close and relaunch once to catch settings or placement load errors.

## Window And Tray

- Move the window, close it, relaunch, and confirm placement is restored safely on-screen.
- Toggle always-on-top and confirm the setting sticks after restart.
- Minimize the app and confirm restore works.
- Close with `Close to tray` enabled and confirm the tray icon remains.
- Restore from the tray icon.
- Exit from the tray menu and confirm the process closes.

## Media Controls

- Start playback in Spotify, a browser, or another Windows media-session app.
- Confirm title, artist/description, progress, duration, play state, and artwork update.
- Test play/pause, next, previous, and seek controls.
- Pause playback and confirm the UI state updates.
- Stop playback or close the media app and confirm Mystral returns to idle without crashing.

## Lyrics

- Play a well-tagged song with the correct album and duration and confirm synced or plain lyrics load.
- Play a track with incomplete or imperfect metadata and confirm lyrics still load through the fallback search when available.
- During a long gap, confirm the three dots fill alongside the preceding highlighted lyric without recentering or advancing lyric synchronization early.
- Switch tracks and confirm old lyrics do not remain stuck.
- Test lyrics mode, back navigation, scrolling, and fullscreen lyrics if artwork is present.
- Play a track with no lyrics and confirm the empty/not-found state is readable.

## Volume

- Adjust volume using Mystral and confirm Windows output volume changes.
- Toggle mute and confirm the icon/state updates.
- Drag the volume slider and confirm tooltip values are reasonable.

## Notifications

- Enable notifications in settings.
- Change tracks and confirm one track notification appears.
- Burn a "CD" and confirm the successful burn notification appears.
- Disable notifications and confirm no new notifications appear.

## Settings

- Open settings from the app and tray menu.
- Toggle behavior settings, save, restart, and confirm persistence.
- Enter invalid Last.fm credentials and confirm validation fails gracefully.
- If available, enter valid Last.fm credentials and confirm validation succeeds.

## Last.fm

- With Last.fm enabled, play a likely song longer than 30 seconds.
- Confirm the Last.fm link/action appears for valid track metadata.
- If scrobbling is enabled, confirm now-playing/scrobble status does not report repeated failures.
- Play an ad, podcast, very short clip, or idle state and confirm it is not treated as a scrobbleable song.

## Social Sharing

Social sharing depends on a proprietary backend and is validated separately as an
internal step; it is not part of the contributor smoke test.

## Installer

- Build the release folder output.
- Run `installer\Build-Installer.ps1`.
- Install the generated setup executable.
- Launch Mystral from the installed shortcut or install location.
- Confirm resources load: icon, artwork placeholder, buttons, busy animation, and sounds.
- Uninstall and confirm the app is removed cleanly.

## Pass Criteria

- No crashes.
- No stuck tray process after exit.
- Settings persist across restart.
- Media controls match the active Windows media session.
- Lyrics and Last.fm failures degrade to readable UI states.
- Installer creates a launchable app with expected resources.
