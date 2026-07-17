# Mystral Smoke Test

Use this checklist before a release or after changes that touch WPF, media controls, settings, Last.fm, installer packaging, tray behavior, or startup behavior.

## Build

```powershell
dotnet build .\mystral.csproj -c Debug /p:AppEnvironment=Development
dotnet run --project .\tests\Mystral.Tests\Mystral.Tests.csproj -c Debug --no-restore
dotnet build .\mystral.csproj -c Release /p:AppEnvironment=Production --no-restore
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

- Confirm each custom title bar places its available Close and Minimize controls on the left, while main-window actions such as Always on top and Fullscreen remain on the right.
- Confirm the Mystral icon and name are visually centered in custom title bars where they are shown, including the burn editor and track notification.
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
- Disable notifications and confirm no new notification appears.

## Settings

- Open settings from the app and tray menu.
- Toggle behavior settings, save, restart, and confirm persistence.
- Under Behavior, switch the Burn lyrics provider between `MusicBrainz-assisted (default)` and `LRCLIB (direct)`, save, restart, and confirm the selected provider persists.
- Enter invalid Last.fm credentials and confirm validation fails gracefully.
- If available, enter valid Last.fm credentials and confirm validation succeeds.

## Burn Editor

- Open an audio file that already contains plain and synchronized lyrics; confirm both fields appear in the Lyrics tab without changing the source file.
- Add or edit plain lyrics and timestamped LRC lyrics such as `[00:12.34]First line`, save the burned copy, reopen that copy, and confirm both lyric forms round-trip.
- Clear both lyric fields, save another copy, reopen it, and confirm the lyrics were removed while unrelated metadata remains intact.
- Enter synchronized text without a valid LRC timestamp and confirm Save reports a readable validation error instead of creating the burned copy.
- With `MusicBrainz-assisted (default)` selected, click `Fetch song data` for a known track and confirm MusicBrainz metadata/artwork and LRCLIB plain or synchronized lyrics populate the editor.
- With `LRCLIB (direct)` selected, edit the lookup fields, click `Fetch song data`, and confirm lyrics are searched using the values currently in the editor.
- While a fetch is running, confirm the progress window uses the search icon and concise song-search wording; when cover or disc artwork is not found, confirm the notice uses the artwork icon.
- If both plain and synchronized lyrics are available from LRCLIB, confirm both remain editable and are saved to the burned copy.
- Confirm the original audio bytes and tags are unchanged after every burn.

## Updates

- From the About dialog, check for an update while running a version older than the latest release.
- Start the installer download, click Cancel, and confirm Mystral reports that the download was canceled and the installer was not launched.
- Start the download again, disconnect the network, and confirm the failure dialog includes a cause plus Retry and Cancel buttons.
- Restore the connection, click Retry, and confirm a new progress dialog completes the download and launches the installer.

## Last.fm

- With Last.fm enabled, play a likely song longer than 30 seconds.
- Confirm the Last.fm link/action appears for valid track metadata.
- If scrobbling is enabled, confirm now-playing/scrobble status does not report repeated failures.
- Play an ad, podcast, very short clip, or idle state and confirm it is not treated as a scrobbleable song.

## globe

- Run globe locally on port 3000, open Settings → Social, and link through the browser approval page.
- Confirm the linked name, username, avatar, and CD count update without restarting Mystral.
- Burn once with automatic sharing off: the completion popup must offer `Share to globe`, and its status modal must move from progress to success.
- Burn once with automatic sharing on: the completion popup must say `Successfully burned and shared to your globe profile!` only after the server accepted it.
- Stop globe or force the burn endpoint to fail, burn with automatic sharing on, and confirm the popup reports the failure and offers Retry.
- Restore globe, click Retry, and confirm Mystral validates immediately and resends the same burn instead of waiting for the periodic check.
- Unlink in Mystral and confirm globe rejects the old bearer token, local link state clears, auto-share turns off, and a confirmation appears.
- Link again, unlink from globe's web settings, and confirm Mystral warns once on the next status check and disables globe controls.
- Cancel a browser approval and confirm Mystral exits the waiting state and reports the cancellation.
- Open globe's Mystral connection page while unlinked, click `open Mystral to link`, and confirm Settings → Social opens and linking starts automatically.
- Open `mystral-dev://settings/social` and confirm an existing process is activated rather than starting a second instance.
- Confirm globe shows the burned-CD wall post and jewel-case collection entry with the same metadata/card styling as Last.fm music posts.

## Installer

- Build the release folder output.
- Run `installer\Build-Installer.ps1`.
- Install the generated setup executable.
- Launch Mystral from the installed shortcut or install location.
- Confirm resources load: icon, artwork placeholder, buttons, busy animation, and sounds.
- Open `mystral://settings/social` and confirm the installed production app opens Settings → Social and begins linking when no account is linked.
- Uninstall and confirm the app is removed cleanly.

## Pass Criteria

- No crashes.
- No stuck tray process after exit.
- Settings persist across restart.
- Media controls match the active Windows media session.
- Lyrics and Last.fm failures degrade to readable UI states.
- Installer creates a launchable app with expected resources.
