# Changelog

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
