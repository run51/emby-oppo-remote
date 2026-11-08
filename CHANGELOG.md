# Changelog

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
