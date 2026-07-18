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

- Confirm each custom title bar places its available Close and Minimize controls on the left, while main-window actions such as Always on top and Fullscreen remain on the right.
- Confirm the Mystral icon and name are visually centered in custom title bars where they are shown, including the burn editor and track notification.
- Move the window, close it, relaunch, and confirm placement is restored safely on-screen.
- Toggle always-on-top and confirm the setting sticks after restart.
- Minimize the app and confirm restore works.
- Enter fullscreen and confirm Close is hidden, then exit with Escape or the fullscreen control and confirm Close returns.
- Close with `Close to tray` enabled and confirm the tray icon remains.
- Restore from the tray icon.
- Exit from the tray menu and confirm the process closes.

## Media Controls

- Start playback in Spotify, a browser, or another Windows media-session app.
- Confirm title, artist/description, progress, duration, play state, and artwork update.
- Test play/pause, next, previous, and seek controls.
- In compact, expanded, and fullscreen playback controls, confirm Lyrics appears before More.
- In compact, expanded, lyrics, and fullscreen modes, click an empty point on the progress bar and drag from the track (not only the thumb); confirm every progress bar follows the same target.
- Confirm the hand cursor covers the full progress and volume bar surfaces, not only their draggable thumbs.
- Hover a progress bar and confirm its tooltip says `Seek`; click or drag and confirm it shows the target time, stays visible briefly after release, and then returns to `Seek`.
- While dragging a progress bar, switch away from Mystral before releasing; return and confirm the timeline is not stuck in seek-preview mode.
- Seek forward and backward in Spotify and Chrome and confirm the chosen timestamp does not snap back while Windows delivers the old position.
- Let Spotify and Chrome play across several polling intervals and confirm the elapsed time remains monotonic instead of jumping backward or resetting to zero.
- Play a track from an album with Apple Music animated cover art (for example The Weeknd — Dawn FM) and confirm the cover fades into a looping animation in the compact, expanded, lyrics-header, and fullscreen art views.
- Let the animated cover reach its end and confirm it loops without freezing; switch tracks and confirm the next track returns to its own static or animated art without leftovers.
- Pause playback and confirm the UI state updates.
- Stop playback or close the media app and confirm Mystral returns to idle without crashing.

## Lyrics

- Play a well-tagged song with the correct album and duration and confirm synced or plain lyrics load.
- In Apple Music for Windows, play a known track and confirm lyrics load when its media session combines `Artist — Album` instead of publishing a separate album.
- Play a track with incomplete or imperfect metadata and confirm lyrics still load through the fallback search when available.
- During a long gap, confirm the three dots fill alongside the preceding highlighted lyric without recentering or advancing lyric synchronization early.
- With a seekable source and synchronized lyrics, click a line in both regular and fullscreen lyrics; confirm playback seeks to that line and recenters it. Confirm plain lyrics and synchronized lyrics from a non-seekable source keep the normal cursor and do nothing when clicked.
- Scroll regular and fullscreen lyrics away from the active line, then let the same track loop from near its end to the beginning; confirm both views clear browsing state and return to the top. Confirm a one-off zero/near-zero provider reading or small backward jitter does not reset the lyric view.
- With the automatic artwork theme, confirm lyrics mode shows one blurred cover backdrop plus the header thumbnail without a doubled backdrop. With a fixed custom theme, confirm the backdrop stays hidden while the header thumbnail remains.
- Switch tracks and confirm old lyrics do not remain stuck.
- Test lyrics mode, back navigation, scrolling, and fullscreen lyrics if artwork is present.
- Play a track with no lyrics and confirm the empty/not-found state is readable.

## Volume

- Adjust volume using Mystral and confirm Windows output volume changes.
- Toggle mute and confirm the icon/state updates.
- Click an empty point on each volume bar, then drag from both the track and thumb; confirm volume follows continuously in every player mode.
- Confirm the volume tooltip appears immediately, stays visible briefly after release, and a lost mouse capture does not leave the slider in its dragging state.

## Notifications

- Enable notifications in settings.
- Change tracks and confirm one track notification appears.
- Burn a "CD" and confirm the successful burn notification appears.
- Disable notifications and confirm no new notifications appear.
- With a custom player theme selected, confirm track notifications still derive their tint from the track artwork rather than the custom player color.

## Settings

- Open settings from the app and tray menu.
- Toggle behavior settings, save, restart, and confirm persistence.
- Export playback history and confirm the successful `Export complete` dialog uses the CSV icon.
- Under Behavior, switch the Burn lyrics provider between `MusicBrainz-assisted (default)` and `LRCLIB (direct)`, save, restart, and confirm the selected provider persists.
- Open Appearance and confirm the category/header uses the appearance icon and the Theme control opens its color picker.
- While a track is visible, choose a custom theme color and save; confirm the main player changes to that color immediately without switching tracks or restarting.
- Check the compact, expanded-artwork, lyrics, and fullscreen player modes; confirm cover-derived background artwork is hidden in each mode while foreground cover artwork remains visible.
- Return Theme to automatic and save; confirm artwork-derived tinting and all player background artwork return immediately.
- Open the burn editor with artwork and confirm its tint remains artwork-derived rather than using the custom main-player theme.
- Enter invalid Last.fm credentials and confirm validation fails gracefully.
- If available, enter valid Last.fm credentials and confirm validation succeeds.

## Burn Editor

- Open an audio file that already contains plain and synchronized lyrics; confirm both fields appear in the Lyrics tab without changing the source file.
- Add or edit plain lyrics and timestamped LRC lyrics such as `[00:12.34]First line`, save the burned copy, reopen that copy, and confirm both lyric forms round-trip.
- Clear both lyric fields, save another copy, reopen it, and confirm the lyrics were removed while unrelated metadata remains intact.
- Enter synchronized text without a valid LRC timestamp and confirm Save reports a readable validation error instead of creating the burned copy.
- With `MusicBrainz-assisted (default)` selected, click `Fetch song data` for a known track and confirm MusicBrainz metadata/artwork and LRCLIB plain or synchronized lyrics populate the editor.
- With `LRCLIB (direct)` selected, edit the lookup fields, click `Fetch song data`, and confirm lyrics are searched using the values currently in the editor.
- Fetch a song with lyrics, then fetch a song that has no lyrics; confirm both lyric editors clear instead of retaining the previous song's text.
- While a fetch is running, confirm the progress window uses the search icon and concise song-search wording; when cover or disc artwork is not found, confirm the notice uses the artwork icon.
- Fetch tracks that LRCLIB marks as instrumental both with and without a confident MusicBrainz match; confirm the completion or warning popup uses the instrumental icon.
- If both plain and synchronized lyrics are available from LRCLIB, confirm both remain editable and are saved to the burned copy.
- Confirm the original audio bytes and tags are unchanged after every burn.

## Updates

- From the About dialog, check for an update while running a version older than the latest release.
- Start the installer download, click Cancel, and confirm Mystral reports that the download was canceled and the installer was not launched.
- Start the download again, disconnect the network, and confirm the failure dialog includes a cause plus Retry and Cancel buttons.
- Restore the connection, click Retry, and confirm a new progress dialog completes the download and launches the installer.
- After installing a newer release, confirm the `Update installed` popup includes a `What's new?` link and opens the GitHub comparison from the previous release tag to the newly installed tag.

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
