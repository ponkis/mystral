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
- With always-on-top enabled, expand music information, then open the burn editor and settings window; confirm the player stays above other apps the whole time (the child windows themselves are not topmost) and remains topmost after closing them.
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
- Confirm the seek and volume tooltips appear centered above the slider handle (pill) and follow it while dragging rather than sitting over the middle of the bar.
- While dragging a progress bar, switch away from Mystral before releasing; return and confirm the timeline is not stuck in seek-preview mode.
- Seek forward and backward in Spotify and Chrome and confirm the chosen timestamp does not snap back while Windows delivers the old position.
- Let Spotify and Chrome play across several polling intervals and confirm the elapsed time remains monotonic instead of jumping backward or resetting to zero.
- Play a track from an album with Apple Music animated cover art (for example The Weeknd — Dawn FM) and confirm the cover fades into a looping animation in the compact, expanded, lyrics-header, and fullscreen art views.
- Let the animated cover reach its end and confirm it loops without freezing; switch tracks and confirm the next track returns to its own static or animated art without leftovers.
- Switch between two albums whose animated covers are both already cached and confirm no outgoing static, blank, or black frame flashes during the swap.
- Switch from animated art to an album without animation and confirm the new track's real static cover replaces the held frame immediately when available — the old frame must not linger beside the new title and must not fade out seconds later. Repeat with quick Next/Previous presses while metadata is still changing and confirm no provisional cover breaks through or remains behind. Use a 60 fps recording and frame-by-frame inspection for the one-frame case.
- Pause playback and confirm the UI state updates.
- Stop playback or close the media app and confirm Mystral returns to idle without crashing.

## Music Information

- Play a known track, open `More`, and confirm `Track information`, `Artist information`, and `Album information` unfold the player onto the matching tab instead of opening another taskbar window.
- Confirm the existing playback-controls pill stays in its upper-right position while the elapsed time, seek slider, and remaining time sit beneath the information content. The timeline must appear only once, with matching 12-DIP spacing from the information box above and rounded window edge below, and no enclosing fill, separate divider, or large empty footer. The compact cover, title, and artist must not remain duplicated.
- On each of the Track, Artist, and Album tabs, click Lyrics and confirm a narrow glass pane slides out from the information sheet's right side. The selected tab and its content must remain unchanged, with no switch to the regular lyrics player or compact-player flash.
- Toggle the attached lyrics pane repeatedly and confirm its slide, fade, scale, and any monitor-edge repositioning reverse smoothly without a resize snap, clipped shadow, stale frame, doubled border, or jump in the main information sheet. Press Escape once to retract the pane and again to close information.
- In the attached pane, verify synchronized highlighting and automatic following, manual wheel scrolling, seekable line clicks, wait dots during long gaps, and readable plain lyrics. Switch tracks while it is open and confirm the loading state replaces the old lines; if the new track has no lyrics, the pane retracts cleanly.
- Confirm working window controls remain visible even when the pointer leaves or the window loses focus. They stay in a squared-bottom tab whose bottom edge sits exactly on the sheet's top border instead of overlapping inside it. The tab shows the attached sheet's same animated blur, gloss, tint, and border rather than appearing as a separate solid block, and button hover fills stay square at shared and bottom edges with rounding only at the strip's outer top corners.
- Confirm the cover animates into the larger artwork tile without a blank flash, then retraces the same path without clipping, jumping, or exposing a hard window edge when information mode closes. As it lands, the compact title, glass, media details, and controls must dissolve back around it without a final resize snap, blank flash, doubled/darkened glass, or stale cover. Its image, border, and highlight must follow the same rounded corners with no square gaps throughout both transitions, including when the compact player began near any monitor edge.
- Confirm the Track, Artist, and Album tabs begin at the content's left edge below the lifted artwork, and that the selected tab opens into the content while its top outline remains a continuous single-width line everywhere else, with no stepped joins or doubled separators; tab headers use the Hand cursor while the information body uses the normal cursor.
- Confirm the Track tab presents the matched title, full artist credit, and album prominently in aligned rows, followed only by first release date, track number, and genres when available.
- Confirm the Artist tab promotes the selected artist's name into the main header and its first available alias into the subtitle, then shows only the name, full track credit, country, aliases, genres, and available biography without clipping long text. When a portrait changes the Artist page tint, confirm the playback pill follows that tint instead of retaining the track-cover tint; switch tabs and close information mode to confirm the normal track tint returns.
- Play a multi-artist recording and confirm the Artist tab lets you switch between credited artists without changing the track or album match.
- Open an artist with a linked Wikimedia Commons image and confirm its actual portrait replaces the initials tile without a text band over the image and never displays the album cover as an artist portrait. Confirm source details are available on hover and an artist without an image link keeps the initials fallback.
- Confirm the Album tab shows only title, artist, first release date, genres, and its track list while keeping the current lifted cover unchanged; opening the tab must not trigger a replacement-artwork fetch or flash.
- Confirm album tracks use compact number/title/artist/duration rows. Only the row whose MusicBrainz recording ID matches the playing recording uses `cd_thing.png`: one copy stretched vertically as a soft background plus crisp, original-height copies along that row's top and bottom, all beginning after the number gutter and continuing through the title/duration area. Every other row must remain plain, with no image, separator, or gradient. Album and Track list headings remain white.
- Open a multi-disc release and confirm disc headings keep repeated track numbers unambiguous.
- Confirm the content ends above the bare playback-timeline row while the playback-controls pill remains above, with no source label or external-page button.
- Try known metadata variants such as a featured artist in the title, an `Explicit` album suffix, missing accents, punctuation differences, or a renamed artist such as Kanye West / Ye; confirm the known recording still resolves without selecting a different song.
- Open information for similarly named recordings or releases and confirm the current artist, album, and duration guide the match.
- Play a track with missing optional fields and confirm the available details remain readable with no blank-field clutter or layout clipping.
- Try incomplete metadata or a track with no confident MusicBrainz match and confirm a clear empty state appears.
- Disconnect the network, retry a lookup, and confirm the information surface reports a readable availability or timeout error and offers `Try again`; reconnect and confirm the same track recovers after retrying instead of retaining the failure.
- If the information lookup receives a temporary busy or server response, confirm the message does not blame the network and the lookup tries again after a short pause.
- Change tracks rapidly while a lookup is running, then collapse the information surface during another lookup; confirm stale details never replace the current track and the app does not crash.
- Open information from compact, expanded, lyrics, and fullscreen modes; with and without its attached lyrics pane open, collapse it, minimize, and restore, confirming the player returns to a valid mode with working controls and no stale sidecar.
- Move the compact player near each monitor edge before opening information, then toggle its lyrics pane and confirm the combined surface stays usable and collapses back to the visible compact-player position.
- At 100% and 150% display scaling, confirm the desktop remains visible through the attached glass surfaces and that every tab selection, lyrics-pane animation and scrolling, the lifted artwork, window controls, and playback pill remain aligned.

## Lyrics

- Play a well-tagged song with the correct album and duration and confirm synced or plain lyrics load.
- In Apple Music for Windows, play a known track and confirm lyrics load when its media session combines `Artist — Album` instead of publishing a separate album.
- Play a track with incomplete or imperfect metadata and confirm lyrics still load through the fallback search when available.
- During a long gap, confirm the three dots fill alongside the preceding highlighted lyric without recentering or advancing lyric synchronization early.
- With a seekable source and synchronized lyrics, click a line in regular lyrics, the information-side pane, and fullscreen lyrics; confirm playback seeks to that line and recenters it. Confirm plain lyrics and synchronized lyrics from a non-seekable source keep the normal cursor and do nothing when clicked.
- Scroll regular lyrics, the information-side pane, and fullscreen lyrics away from the active line, then let the same track loop from near its end to the beginning; confirm all views clear browsing state and return to the top. Confirm a one-off zero/near-zero provider reading or small backward jitter does not reset the lyric view.
- With the automatic artwork theme, confirm lyrics mode shows one blurred cover backdrop plus the header thumbnail without a doubled backdrop or a ghost of the compact player art. With a fixed custom theme, confirm the backdrop stays hidden while the header thumbnail remains.
- Confirm lyrics mode keeps the player's translucent glass look (the desktop shows through slightly) instead of a fully solid panel.
- Leave lyrics mode back to the regular and expanded views and confirm the background artwork appears in its final position without shifting sideways during the transition.
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
- Disable notifications and confirm no new notification appears.
- With a custom player theme selected, confirm track notifications still derive their tint from the track artwork rather than the custom player color.

## Settings

- Open settings from the app and tray menu.
- Toggle behavior settings, save, restart, and confirm persistence.
- Export playback history and confirm the successful `Export complete` dialog uses the CSV icon.
- Under Behavior, switch the Burn lyrics provider between `MusicBrainz (default)` and `LRCLIB`, save, restart, and confirm the selected provider persists.
- Open Appearance and confirm the category/header uses the appearance icon and the Theme control opens its color picker showing only the color wheel and a hex field.
- Move the picker's wheel and confirm the main player (and the Appearance swatch/hex text) previews the color live; cancel and confirm the player returns to the last accepted or saved appearance.
- Accept a color with `Use color` and confirm the player keeps showing it; save and confirm it becomes permanent, or close settings without saving and confirm the player reverts.
- Choose a custom theme color and save; confirm the main player changes to that color immediately without switching tracks or restarting.
- Check the compact, expanded-artwork, lyrics, and fullscreen player modes; confirm cover-derived blurred background artwork is hidden in each mode while foreground cover artwork (including the expanded view's cover) remains visible.
- In the History list, right-click a track and confirm the `Remove` entry shows the delete icon.
- Return Theme to automatic and save; confirm artwork-derived tinting and all player background artwork return immediately.
- Open the burn editor with artwork and confirm its tint remains artwork-derived rather than using the custom main-player theme.
- Enter invalid Last.fm credentials and confirm validation fails gracefully.
- If available, enter valid Last.fm credentials and confirm validation succeeds.
- With only an API key and username saved, confirm Last.fm track links and the tray profile link work; confirm the API secret and password fields stay disabled until `Scrobble playback to Last.fm` is checked.
- With scrobbling checked but no API secret or password, confirm Save is blocked with a readable status and the tray's `Enable Scrobbling` toggle refuses to turn on and points to Settings.
- Fill every Last.fm field but use a wrong password or secret, then enable scrobbling from the tray; confirm Mystral checks the account first and reports the failure instead of turning scrobbling on.
- With settings open, toggle scrobbling from the tray menu and confirm the settings checkbox updates immediately without reopening the window.

## Burn Editor

- Open an audio file that already contains plain and synchronized lyrics; confirm both fields appear in the Lyrics tab without changing the source file.
- Add or edit plain lyrics and timestamped LRC lyrics such as `[00:12.34]First line`, save the burned copy, reopen that copy, and confirm both lyric forms round-trip.
- Clear both lyric fields, save another copy, reopen it, and confirm the lyrics were removed while unrelated metadata remains intact.
- Enter synchronized text without a valid LRC timestamp and confirm Save reports a readable validation error instead of creating the burned copy.
- With `MusicBrainz (default)` selected, click `Fetch song data` for a known track and confirm MusicBrainz metadata/artwork and LRCLIB plain or synchronized lyrics populate the editor.
- With `LRCLIB` selected, edit the lookup fields, click `Fetch song data`, and confirm lyrics are searched using the values currently in the editor.
- Disconnect the network and click `Fetch song data`; confirm the fetch ends with a readable connection/timeout message instead of hanging.
- Right-click the CD artwork tile, choose `Save artwork guide...`, and confirm the saved `cd-artwork-guide.png` matches the transparent disc mask.
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

## globe

- Run globe locally on port 3000, open Settings → Social, and link through the browser approval page.
- Confirm the linked name, username, avatar, and CD count update without restarting Mystral.
- Burn once with automatic sharing off: the completion popup must offer `Share to globe`, and its status modal must move from progress to success.
- Confirm the post-share confirmation uses the mail icon and its `View your post on globe` link (on its own line) opens the burned-CD post via `/users/<name>?post=<id>`.
- Burn once with automatic sharing on: the same share progress modal must appear, and the completion popup must say `Successfully burned and shared to your globe profile!` only after the server accepted it.
- On globe, watch the profile's `my CD collection` sidebar while sharing and deleting a burned-CD post; confirm it updates without a page refresh.
- Stop globe or force the burn endpoint to fail, burn with automatic sharing on, and confirm the popup reports the failure and offers Retry.
- Stop globe while linked and confirm the Social profile frame transitions to its offline presence; restart globe and confirm it animates back to online on the next status change.
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
- Lyrics, MusicBrainz, and Last.fm failures degrade to readable UI states.
- Installer creates a launchable app with expected resources.
