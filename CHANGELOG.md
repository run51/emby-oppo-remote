# Changelog

## v0.22.2

- **For the Remote 3 Emby driver:** the disc format answer (`/Oppo/Remote/Format`) now says whether "Movie without
  menus" keeps Dolby Vision (`MovieDisc`: reduced disc over DLNA), so a client can offer it on Dolby Vision
  discs without asking the player's state.
- "Movie without menus": when the player takes the reduced disc's link but fetches nothing (its DLNA renderer is
  stuck), the plugin now goes straight to the share instead of trying the full disc over DLNA too, 6 s sooner.
- Emby.Oppo 0.22.2, Emby.IsoBluray 0.5.1 (unchanged). Android apps unchanged.

## v0.22.1

- **"Test the connection" button** on the plugin's settings page, under the OPPO address: it checks the player's
  IP control link and says what is wrong when it does not answer (connection refused: IP control off or another
  device already connected; no answer: wrong address, player off without network standby, isolated Docker
  network; connection closed; IP control hung…). A player in standby is reported as such: it answers the power
  query but not the playback one. Through an oppo-multiplexer, the messages say so.
- When the player stops giving its playback state, Emby's normal log now says why, once (it was only in the
  debug log), and when it answers again.
- Emby.Oppo 0.22.1, Emby.IsoBluray 0.5.1 (unchanged). Android apps unchanged.

## v0.22.0

- **DVD folders over DLNA** (oppo-dv-mkv mod): a DVD folder (`VIDEO_TS`) is served over WebDAV like a Blu-ray
  folder, with a link to `VIDEO_TS/VIDEO_TS.IFO`, and the OPPO plays it as a disc: menu, tracks, seeking and
  resume work, and it starts in about a second. Before, DVD folders were played from the share. DVD ISOs
  already played over DLNA, now checked too (including a two-disc title). Thanks to fandangos for confirming
  VIDEO_TS support in the mod.
- Emby.Oppo 0.22.0, Emby.IsoBluray 0.5.1 (unchanged). Android apps unchanged.

## v0.21.1

- **The remote no longer stays on "Connecting…" when the OPPO is not set up yet.** It now reads "OPPO not
  set up · Set its address in Emby: Dashboard → Plugins → OPPO". Before, the state request failed and the
  remote showed "Emby is not responding", which was misleading. A player that does not answer within 15 s now
  shows "OPPO unreachable" in the same way.
- README: on a Synology with Emby from Package Center, the plugins are now installed with DSM's Task Scheduler
  (a script to paste, no SSH); plugin settings are to be opened in a web browser.
- Emby.Oppo 0.21.1, Emby.IsoBluray 0.5.1 (unchanged). Android apps unchanged.

## v0.21.0

- **"Movie without menus" keeps Dolby Vision** when DLNA playback is on (oppo-dv-mkv mod): the plugin serves the
  Blu-ray reduced to its main movie, a virtual disc whose navigation plays the main playlist at once, and the
  OPPO plays it as a disc. Dolby Vision, the disc's chapters, its named tracks and resume all work; the movie
  starts in about a second (checked on 1917, Dune and Dune: Part Two, UHD Dolby Vision). The button is
  therefore offered on Dolby Vision discs too when DLNA is on. Without the mod, the previous mode is kept (the
  main movie as one .m2ts file, HDR10 only, button hidden on Dolby Vision discs).
- **Dolby Atmos and DTS:X in every Emby app**, not only in the web remote: once a title is analysed, the
  plugin writes them into Emby's own track information. DTS:X tracks read "English DTS:X 7.1" (track profile);
  Atmos tracks get the title "Dolby Atmos" (for TrueHD, Emby shows the codec name, not the profile; an existing
  track title is kept). A library rescan resets this information: the plugin writes it back a few seconds
  after each item update, and the nightly task checks the whole library.
- The DTS-HD MA audio filter and the "best track" badge now count DTS:X tracks as DTS-HD MA (they are).
- The web remote no longer asks the player for its HDR / audio output in the first 90 s of a movie: on a disc
  started without menus, that query once went unanswered and the OPPO then refused IP control until it was
  switched off.
- Emby.Oppo 0.21.0, Emby.IsoBluray 0.5.1 (unchanged). Android apps unchanged.

## v0.20.0

- **The title page's audio badges show the disc's best track**, whatever the language selected: a disc with
  a French Dolby Digital track and an English TrueHD Atmos one now reads "TrueHD 7.1 · Atmos" (Backdraft:
  "DTS:X 7.1" instead of the French "DTS"). Order: Atmos, DTS:X, TrueHD, DTS-HD MA, PCM, DTS-HD HRA,
  Dolby Digital Plus, DTS, Dolby Digital, then the number of channels. The track list still selects your
  preferred language.
- **Library filters:** the "Picture" filter is now called "Video"; "4K Dolby Vision" is gone (Dolby Vision
  stays); "HDR" now lists HDR10 / HDR10+ / HLG titles without the Dolby Vision ones. New **Audio** filter:
  Dolby Atmos, DTS:X, Atmos or DTS:X, TrueHD, DTS-HD MA, 7.1 (titles with at least one such track, in any
  language).
- **Updates from the plugin page:** Dashboard → Plugins → OPPO shows the installed versions and the latest
  release on GitHub; **Update** downloads both plugins, checks them (assembly name and version) and replaces the
  installed files (the previous ones are kept in Emby's cache folder), then **Restart Emby** applies them.
  Administrators only. From this version on, no more manual copying for updates.
- New scheduled task **"OPPO: Dolby Atmos / DTS:X"** (daily, 3 am): analyses the titles that have a TrueHD or
  DTS track, so that the audio filter knows them all. First run on a 600-title library: about 7 minutes.
- Emby.Oppo 0.20.0, Emby.IsoBluray 0.5.1 (unchanged). Android apps unchanged.

## v0.19.0

- **Dolby Atmos and DTS:X are shown on the title page** (audio badges and track list: "TrueHD Atmos 7.1",
  "DTS:X 7.1"). Emby's ffprobe (FFmpeg 5.1) does not report them, so the plugin looks at the audio itself:
  the Atmos bit of the TrueHD major sync, the DTS:X extension in DTS-HD MA. Blu-ray discs (ISO or folder):
  48 MB of the movie are read once; MKV files: a few seconds of each TrueHD / DTS track. Results are cached.
  Dolby Digital Plus Atmos (streaming files) is not detected.
- **Audio track choice on some discs:** the OPPO's audio menu follows the disc playlist, and Emby's track
  order can differ from it (a disc carrying an extra track that the playlist does not list, such as Dune).
  The plugin now matches Emby's track to the playlist by language and codec before selecting it on a disc.
- **Windows paths:** Emby libraries on a Windows server (`D:\Movies`, `\\nas\Movies`) now match the path
  mappings (backslashes, any letter case), and Blu-ray folders can be served over DLNA from Windows paths.
  The plugins were developed and tested on Linux (Docker on a Synology NAS); Windows is still untested.
- Includes v0.18.1 (DLNA fixes).
- Emby.Oppo 0.19.0, Emby.IsoBluray 0.5.1 (folder names with a trailing backslash). Android apps unchanged.

## v0.18.1

- **DLNA: the player could freeze when switching movies.** The plugin sent the DLNA renderer a Stop before
  each new link and when falling back to the share. Sent while the mod plays a disc image, that Stop left
  the disc playing, deaf to IP control, and the renderer stopped taking links until the player was
  unplugged. The plugin no longer sends it: the player is stopped through IP control only, as before.
- **DLNA was skipped for no reason after a disc image.** The renderer reports "TRANSITIONING" during and
  after every disc image played by the mod, and still takes a new link then. The plugin took that state for
  a stuck renderer and used the share. It now only skips DLNA when the renderer does not answer; a renderer
  that really is stuck is still detected when it does not request the movie within 6 s.
- Emby.Oppo 0.18.1, Emby.IsoBluray 0.5.0 (unchanged). Android apps unchanged.

## v0.18.0

- **Movies can be played over DLNA, with no network share** (new option "Play over DLNA", off by
  default). It needs the [oppo-dv-mkv](https://github.com/fandangos/oppo-dv-mkv) mod on the player, with
  its "DLNA Disc Images" setting on. The plugin serves the movie itself, on a port of its own (8099 by
  default), and sends its address to the player's DLNA renderer:
  - **ISO files and Blu-ray folders play as a disc**, with menus, Dolby Vision and lossless audio. An ISO
    is served byte for byte with HTTP ranges. A Blu-ray folder is served over WebDAV, and the mod builds
    the disc image from that listing. Most discs start in 1 to 3 s; a UHD folder with many files can
    take 10 to 20 s, while the player builds its image.
  - **MKV, MP4 and M2TS files play as files.** Dolby Vision MKV files keep Dolby Vision.
  - Track selection, the resume point, chapters and the web remote work as before, through IP control.
  - This skips the share mount and the player's HTTP interface (port 436), the steps that failed after a
    player restart or when a share was not released. The share is still used as a fallback: if the
    player does not request the movie within 6 s, if the movie does not start, or if its DLNA renderer is
    stuck ("TRANSITIONING" even after Stop; only unplugging the player cleared it).
  - Emby's own stream URL cannot be used for this: for an ISO, the ISO Blu-ray plugin answers with the
    main title as an M2TS stream, not with the image.
- **"Movie without menus"** button on the title page of Blu-ray and UHD discs (ISO or folder), like the
  "play main movie" of Zidoo players. The plugin serves the clips of the main playlist end to end and
  sends them to the DLNA renderer as an .m2ts file: the movie starts at once, with no menu or trailer.
  This mode does not need the mod.
  - The player has no chapters then: next / previous chapter jump by time to the disc's chapter marks.
  - The player lists the tracks without languages: the web remote shows Emby's track names instead.
  - Not offered on Dolby Vision discs: played as a file, the player outputs the HDR10 base layer only.
  - If the movie does not start, the disc is played normally.
- **Tracks are checked after they are applied.** The player could accept the chosen audio and subtitle
  tracks and then switch back to its own defaults while a file was still starting. The plugin now
  re-reads them a moment later and selects them again if needed.
- **The web remote and the apps no longer say the OPPO is off while it is on.** The player answers IP
  control in a short form ("@OK MEDIA CENTER") when another client, such as the Unfolded Circle Oppo
  driver, switches it out of verbose mode. The plugin only understood the verbose form
  ("@QPL OK MEDIA CENTER"), so every query looked unanswered. Both forms are now accepted.
- Emby.Oppo 0.18.0, Emby.IsoBluray 0.5.0 (unchanged). Android apps unchanged.

## v0.17.2

- **A film starts after the player was restarted** (power button held, power cut): the player then
  answers IP control ("HOME MENU") but keeps its HTTP interface closed until a control app announces
  itself, and the film failed at once with "Connection refused (…:436)". Before a film the plugin now
  also waits for the HTTP interface when the player is already on, sending the announcement while it
  is closed (20 s at most).
- **The web remote no longer upsets the player at the start of a film.** With its player screen open,
  it asked the OPPO for the HDR status (#QHS), the audio and the subtitle type even on a disc menu
  (a UHD ISO starts with a title of a few seconds). The OPPO does not answer #QHS there, each unanswered
  query held the oppo-multiplexer 10 s, and the player then ignored commands for a while (the web
  remote's STOP was lost). These three queries are now only sent during the film itself (a title of
  more than 5 minutes, playback started), like the Oppo driver of the Unfolded Circle remote does. The
  audio and subtitle track lists are still returned at any time.
- Emby.Oppo 0.17.2, Emby.IsoBluray 0.5.0 (unchanged).

## v0.17.1

- **Works again after a power cut of the player:** the OPPO then keeps its HTTP interface (port 436)
  closed, even when it is on, until a control app announces itself. While waiting for the player, the
  plugin now sends that announcement (the UDP datagram the official app sends on port 7624), so a film
  can start without opening another app first.
- Emby.Oppo 0.17.1, Emby.IsoBluray 0.5.0 (unchanged).

## v0.17.0

- **IP control through an oppo-multiplexer** (new optional setting "IP control through"): the player
  accepts a single IP control connection (port 23), so the plugin and another controller (an Unfolded
  Circle remote, for example) kept disconnecting each other. Enter the address of an
  [oppo-multiplexer](https://github.com/henrikwidlund/oppo-multiplexer), e.g. `192.168.1.20:23`; the
  HTTP interface (port 436) still goes to the player.
- Emby.Oppo 0.17.0, Emby.IsoBluray 0.5.0 (unchanged).

## v0.16.1

- **Light mode** for slow devices (remote settings, next to the language): turns off the live blur, shadows and animations, hides the blurred backdrop on the playback screen and loads smaller images. The choice is saved on the device.
- The app bar no longer uses a live blur (it was almost opaque already), which makes scrolling lighter everywhere.
- Emby.Oppo 0.16.1, Emby.IsoBluray 0.5.0 (unchanged).

## v0.16.0

- **Two Android apps (1.3.1):**
  - **OPPO**: the remote without home-automation buttons, for setups without home automation.
  - **OPPO Home**: adds Showtime, All off and the amplifier volume. It replaces the previous app (same package), so an update keeps your settings.
  - Both can be installed side by side.
- **Amplifier volume and mute** through Home Assistant: new settings for the Home Assistant address, a long-lived access token and the amplifier's `media_player` entity. The remote shows the volume (in dB when the receiver reports it), with buttons for volume down, volume up and mute.
- In **OPPO Home**, the phone's volume keys control the amplifier:
  - while the app is open and the amplifier is on;
  - while a title is playing, even with the app in the background or the screen locked. Android shows its volume slider for the amplifier, like with a Chromecast.
  - Holding a key down groups the steps, so the volume follows quickly.
- The browser remote offers both apps to download.
- Emby.Oppo 0.16.0 (embeds both apps), Emby.IsoBluray 0.5.0 (unchanged).

## v0.15.2

- **New icon** for the Android app and the web remote: an "OPPO" wordmark on black.
- **README:** credits to Xnoppo (the project that inspired this one) and to Trevor Doyle.
- Emby.Oppo 0.15.2 (embeds Android app 1.2.1), Emby.IsoBluray 0.5.0 (unchanged).

## v0.15.1

- **Android app 1.2:** the lock-screen / notification controls now refresh on their own, so the chapter and the progress update after "next chapter" or "previous chapter" even when the app is in the background. Before, they only refreshed when the app was reopened.
- Emby.Oppo 0.15.1 (embeds app 1.2), Emby.IsoBluray 0.5.0 (unchanged).

## v0.15.0

- **Android app 1.1:** lock-screen and notification media controls while a movie plays on the OPPO (previous chapter, play/pause, next chapter, stop), with the title, chapter and poster. Allow notifications when Android asks. Update the app from the "Install the app" button or from this release.
- **Library filters:** 3D, 4K Dolby Vision, and duration (under 1 h 45, under 2 h, over 2 h 30).
- **Sagas:** a new row on the home screen shows your Emby collections. A collection without its own poster shows the poster of its first movie.
- Emby.Oppo 0.15.0, Emby.IsoBluray 0.5.0 (unchanged).

## v0.14.1

- **3D:** Blu-ray 3D discs (ISO or BDMV folder) are now detected from the disc itself and marked as 3D in Emby. Emby on its own only detects 3D from tags in the file name. Detection uses the `BDMV/STREAM/SSIF` folder or the 3D playlists. Run the "Blu-ray: tracks and runtimes" task once to mark existing movies.
- **Web remote:** a 3D label is shown on library posters, next to 4K / DV / HDR.
- Emby.Oppo 0.14.1, Emby.IsoBluray 0.5.0, Android app 1.0 (unchanged).

## v0.14.0

First public release.

- Emby.Oppo 0.14.0, Emby.IsoBluray 0.4.0, Android app 1.0.
- English UI by default, with a French translation.
