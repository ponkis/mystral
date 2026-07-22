# Mystral Smoke Test

Use this checklist before a release or after changes to the Windows UI, media
controls, lyrics, settings, Last.fm, burning, updates, startup, or packaging.

## Build

~~~powershell
dotnet build .\Mystral.csproj -c Debug /p:AppEnvironment=Development
dotnet run --project .\tests\Mystral.Tests\Mystral.Tests.csproj -c Debug
dotnet build .\Mystral.csproj -c Release /p:AppEnvironment=Production
~~~

For a packaged development build:

~~~powershell
.\scripts\Build-Dev.ps1 -Clean
~~~

## Launch

- Launch from Visual Studio, dotnet run, or a packaged Mystral.exe.
- Confirm the player opens without an error and its icon appears correctly.
- Launch Mystral again and confirm the existing instance is activated instead
  of opening a duplicate process.
- Close and reopen once to verify saved settings and window placement.

## Window and tray

- Move the player, restart, and confirm it returns to a visible screen position.
- Toggle always-on-top, open Settings and the burn editor, and confirm the player
  retains its setting.
- Minimize and restore the app.
- Enter and leave fullscreen with both the control and Escape.
- With close-to-tray enabled, close the window, restore it from the tray, and
  then use the tray Exit command to end the process.
- Confirm custom title-bar controls remain visible, aligned, and clickable in
  every player mode.

## Media controls

- Start playback in Spotify, Apple Music, a browser, or another Windows media
  session source.
- Confirm title, artist, artwork, progress, duration, play state, and supported
  actions update.
- Test play/pause, next, previous, mute, volume, and seeking.
- Click and drag progress and volume bars from both the track and thumb.
- Confirm each tooltip remains centered over its slider handle before, during,
  and after dragging, including after switching player layouts.
- Seek forward and backward and confirm stale Windows timeline updates do not
  snap the selected time back.
- Switch tracks quickly and confirm metadata and artwork never settle on values
  from the previous track.
- Pause playback, then stop playback or close the source and confirm Mystral
  returns to a valid idle state.

## Artwork and themes

- In automatic theme mode, switch between covers with different colors and
  confirm the player tint follows the artwork.
- Choose and save a custom theme. Confirm it applies immediately while foreground
  cover art remains visible and cover-derived backgrounds are hidden.
- Return to automatic mode and confirm artwork tint and backgrounds return.
- Play an album with supported animated Apple Music artwork and confirm it loops
  in compact, expanded, lyrics-header, and fullscreen artwork views.
- Switch rapidly between animated and static covers and confirm no blank, black,
  or stale frame remains.

## Music information

- While a track is playing, open More and select Track information, Artist
  information, and Album information.
- Confirm the player unfolds in the same window, opens the requested tab, and
  keeps playback controls and the timeline usable.
- Switch tabs and confirm the cover, tab outline, content, and player tint remain
  aligned without duplicated compact metadata.
- Confirm Track shows the matched recording details without blank-field clutter.
- Confirm Artist supports credited-artist selection and uses a validated artist
  image when available, otherwise an initials tile.
- Confirm Album keeps the active player cover, presents multi-disc tracks
  clearly, and highlights only the currently playing recording.
- Open a track with punctuation, accent, featured-credit, edition, or renamed
  artist differences and confirm a confident known match still resolves.
- Try incomplete metadata and a song with no confident match; confirm a readable
  empty state appears.
- Disconnect the network, retry, reconnect, and confirm the information lookup
  reports a readable failure and recovers.
- Change tracks or close information during a lookup and confirm stale results
  do not replace the current track.
- Open and close information near each monitor edge at 100% and 150% display
  scaling. Confirm the compact player returns to its original visible position.
- Confirm the opening and closing artwork animation has no clipping, stale frame,
  resize snap, or blank handoff.

## Attached information lyrics

- From each information tab, open Lyrics and confirm a narrow drawer slides from
  beneath the information sheet without replacing the selected tab.
- Confirm the drawer meets the sheet without a wallpaper gap, doubled border,
  shadow halo, or rounded inner edge.
- Toggle it repeatedly and confirm entrance and exit remain smooth while the
  main information sheet stays still.
- Confirm synchronized lines follow playback, can be clicked to seek, and can be
  browsed with the wheel.
- Switch tracks while the drawer is open. Confirm loading replaces the old
  lyrics, valid new lyrics appear, and a definitive no-lyrics result retracts
  the drawer cleanly.

## Lyrics

- Play songs with synchronized lyrics, plain lyrics, imperfect metadata, and no
  available lyrics; confirm each state is readable.
- Enter regular lyrics and confirm the surface appears without a blank frame.
- Confirm the active synchronized line uses the disc-style highlight in regular,
  attached information, and fullscreen lyrics.
- Use an active lyric that wraps to three or more rows and confirm its container
  remains fully below the regular lyrics header.
- Let several lines advance automatically. Completed regular lyrics should
  disappear instead of peeking above the active row; scrolling upward should
  reveal them again for browsing.
- During a long instrumental gap, confirm the wait dots animate without advancing
  synchronization early.
- With a seekable source, click synchronized lines in all three lyric views and
  confirm playback seeks and recenters. Plain or non-seekable lyrics must not
  act as links.
- Browse away from the active line and let the track loop. Confirm all lyric
  views reset to the beginning, while one-off near-zero timeline noise does not.
- Leave lyrics for compact and expanded modes and confirm no artwork or layout
  jumps during the transition.

## Settings and Last.fm

- Open Settings from both the player and tray.
- Change behavior and appearance settings, save, restart, and confirm persistence.
- Export playback history and confirm the CSV is created.
- Switch the burn lyric lookup between MusicBrainz and LRCLIB, save, restart,
  and confirm the selection persists.
- With only a Last.fm API key and username, confirm track and profile links work.
- Enable scrobbling without its additional credentials and confirm saving is
  blocked with a readable explanation.
- Enter invalid full credentials and confirm validation fails gracefully; test
  valid credentials when available.
- Toggle scrobbling from the tray while Settings is open and confirm both views
  remain synchronized.

## Notifications

- Enable notifications, change tracks, and confirm one track notification appears.
- Disable notifications and confirm no additional notification appears.
- With a fixed player theme, confirm notifications still derive their tint from
  the track artwork.

## Burn editor

- Open a supported audio file and confirm the editor does not modify it in place.
- Edit metadata, cover art, disc art, plain lyrics, and timestamped LRC lyrics;
  save, reopen the copy, and confirm the values round-trip.
- Clear lyrics and save another copy. Confirm unrelated metadata remains intact.
- Enter malformed synchronized lyrics and confirm saving reports a readable
  validation error without creating a copy.
- Test Fetch song data in both MusicBrainz and LRCLIB modes. Confirm returned
  metadata, artwork, and lyrics remain editable.
- Fetch a song with lyrics followed by one without lyrics and confirm old lyric
  text is cleared.
- Disconnect the network during a fetch and confirm it ends with a readable
  connection or timeout message.
- Save the artwork guide from the disc-art menu and confirm the exported image
  retains its transparent disc mask.
- Confirm the source file’s bytes and tags remain unchanged after every burn.

## Updates

- Check for an update from About while running an older version.
- Start a download, cancel it, and confirm no installer launches.
- Retry after an interrupted connection and confirm the download can complete.
- After installing a newer release, confirm the update dialog can open the
  GitHub comparison between versions.

## Social sharing

Social sharing depends on the hosted service and is validated separately by the
maintainer. It is not required for contributor smoke testing.

## Installer

- Build the Release folder output and run installer\Build-Installer.ps1.
- Install the generated setup, launch from the installed shortcut, and confirm
  icons, artwork, animations, and sounds load.
- Open the production Mystral URL protocol and confirm it activates the installed
  app rather than starting duplicate processes.
- Uninstall and confirm the application is removed.

## Pass criteria

- No crashes or stuck process after Exit.
- Window, tray, settings, and media controls remain usable.
- Lyrics, MusicBrainz, Last.fm, and network failures degrade to readable states.
- The original audio source is never modified.
- The installer produces a launchable application with expected resources.
