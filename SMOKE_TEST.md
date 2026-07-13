# Mystral Smoke Test

Use this checklist before a release or after changes that touch WPF, media controls, settings, Last.fm, installer packaging, tray behavior, or startup behavior.

## Build

```powershell
dotnet build .\Mystral.csproj -c Debug /p:AppEnvironment=Development
dotnet run --project .\tests\Mystral.Tests\Mystral.Tests.csproj --no-restore
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

- Play a normal song with lyrics and confirm synced or plain lyrics load.
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
- Disable notifications and confirm no new notification appears.

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

## Globe

- Run Globe locally on port 3000, open Settings → Social, and link through the browser approval page.
- Confirm the linked name, username, avatar, and CD count update without restarting Mystral.
- Burn once with automatic sharing off: the completion popup must offer `Share to globe`, and its status modal must move from progress to success.
- Burn once with automatic sharing on: the completion popup must say `Successfully shared to your globe profile.` only after the server accepted it.
- Stop Globe or force the burn endpoint to fail, burn with automatic sharing on, and confirm the popup reports the failure and offers Retry.
- Unlink in Mystral and confirm Globe rejects the old bearer token, local link state clears, auto-share turns off, and a confirmation appears.
- Link again, unlink from Globe's web settings, and confirm Mystral warns once on the next status check and disables Globe controls.
- Open `mystral-dev://settings/social` and confirm an existing process is activated rather than starting a second instance.
- Confirm Globe shows the burned-CD wall post and jewel-case collection entry, and that collection reordering survives a reload.

## Installer

- Build the release folder output.
- Run `installer\Build-Installer.ps1`.
- Install the generated setup executable.
- Launch Mystral from the installed shortcut or install location.
- Confirm resources load: icon, artwork placeholder, buttons, busy animation, and sounds.
- Open `mystral://settings/social` and confirm the installed production app opens Settings → Social.
- Uninstall and confirm the app is removed cleanly.

## Pass Criteria

- No crashes.
- No stuck tray process after exit.
- Settings persist across restart.
- Media controls match the active Windows media session.
- Lyrics and Last.fm failures degrade to readable UI states.
- Installer creates a launchable app with expected resources.
