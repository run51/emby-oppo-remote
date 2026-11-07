# Emby × OPPO UDP-203/205

Two Emby Server plugins and an Android app to play your library on an **OPPO UDP-203 / UDP-205** (or a compatible clone) straight from Emby. Supported sources are Blu-ray / UHD ISOs, BDMV folders, MKV and other files. The OPPO plays the original file, so you keep full-quality audio and video, menus and Dolby Vision. Emby keeps track of progress.

Written from scratch using the OPPO's public IP control protocol and Emby's public plugin API. No third-party code is included.

The interface is available in **English and French**. The language follows the device or browser, and you can change it in the remote's settings.

## What's inside

| Component | What it does |
|---|---|
| **Emby.Oppo** (plugin) | **Playback:** adds the OPPO to Emby's "Play on…" menu. The player mounts your share over NFS or SMB and opens the original file. Position is reported back to Emby, so resume and watched status work.<br>**Playback options:** audio and subtitle track selection, multi-disc titles, optional webhook for home automation.<br>**Web remote:** browse your libraries, open a title page, then control playback: chapter thumbnails, live OPPO output info (resolution, HDR, audio format), transport keys, a D-pad and a "Showtime" power scene. |
| **Emby.IsoBluray** (plugin) | Reads inside Blu-ray ISOs and BDMV folders (UDF 2.50, MPLS playlists). Emby then shows the real audio and subtitle tracks, the correct runtime and the format (UHD, HDR…). Blu-ray 3D discs are marked as 3D. |
| **Android app** | Fullscreen app for the web remote, with voice search and lock-screen media controls. The interface is served by the plugin, so it updates without reinstalling the app. |

## Installing the plugins

Emby has no catalog for third-party plugins: they are installed by copying the DLL files.

1. Download `Emby.Oppo.dll` and `Emby.IsoBluray.dll` from the [Releases](../../releases) page.
2. Copy them into the `plugins` folder of your Emby configuration:
   - **Docker:** `<config volume>/plugins/`
   - **Linux:** `/var/lib/emby/plugins/`
   - **Windows:** `%AppData%\Emby-Server\programdata\plugins\`
3. Restart Emby. Both plugins appear under **Dashboard → Plugins**.

Developed and tested with Emby Server 4.10. The plugins are built against the 4.9 API, so 4.9 should work too.

## Configuration (Dashboard → Plugins → OPPO)

- **OPPO address**
  - Give the player a fixed IP address.
  - In the OPPO network settings, enable **IP control**.
- **Path mappings**: for each library folder, tell the plugin how the OPPO can reach it.

  | Path as seen by Emby | Protocol | Server | Share |
  |---|---|---|---|
  | `/volume1/Movies` | NFS | `192.168.1.10` | `volume1/Movies` |
  | `/mnt/movies` | SMB | `nas.local` | `Movies` (+ username and password) |

  NFS is recommended, especially for BDMV folders.
- **Webhook** (optional)
  - Called with `POST {"event":"detected"|"stopped"|"seance_on"|"seance_off","title":…}`.
  - For example, use it to have Home Assistant turn on the TV and AV receiver (`http://homeassistant:8123/api/webhook/<id>`).
- **Unfolded Circle remote** (optional)
  - Enter its address, API key and the TV's `media_player` entity.
  - The TV is then switched on over the network when a movie starts or when you press "Showtime". This helps when the Emby server cannot reach the TV with Wake-on-LAN.

Then, from any Emby app: **Play on… → OPPO**.

### Scheduled task

Run *Dashboard → Scheduled tasks → "Blu-ray: tracks and runtimes"* once after installing. It analyses the Blu-ray ISOs and folders already in your library.

## Web remote and Android app

- **Browser**
  - Open `http://<emby-server>:8096/emby/Oppo/Remote` and sign in with your Emby account.
  - In Chrome, the page can be installed as an app (PWA).
- **Android app** (Android 7+)
  - Download `OPPO.apk` from [Releases](../../releases), or use the "Install the app" button on the web remote.
  - Allow installs from unknown sources.
  - On first launch, enter your Emby server address (e.g. `http://192.168.1.10:8096`).

## Building from source

```bash
# Plugins (.NET SDK 8). The Emby API comes from the MediaBrowser.Server.Core NuGet package.
dotnet build src/Emby.IsoBluray -c Release
dotnet build src/Emby.Oppo -c Release

# Android app (no Gradle). Needs Android SDK build-tools 35 and platform 34 in ~/android/sdk.
bash android/build.sh                               # creates a signing key on first run
cp android/build/oppo.apk src/Emby.Oppo/Remote/     # embed it in the plugin, then rebuild Emby.Oppo

# Icons (Android launcher + web remote) from tools/icon.svg. Needs Node.js and Playwright.
node tools/make_icons.js
```

## Known limitations

- On discs with BD-J menus, the OPPO cannot jump straight to the main movie, so the disc menu is shown.
- The file must be reachable by the OPPO over NFS or SMB. The player does not play Emby's transcoded streams.

## Credits

- This project is inspired by [Xnoppo](https://github.com/siberian-git/Xnoppo) by siberian-git, the original bridge that turned Emby into a library front end for the OPPO 203/205. Thanks also to jandrop for its [Docker image](https://hub.docker.com/r/jandrop/xnoppo).
- Many thanks to **Trevor Doyle**, who wrote the scripts I used to launch movies from my Synology NAS with Kodi. They were the starting point of this setup.
- OPPO is a trademark of its owner. This is an unofficial project, not affiliated with or endorsed by OPPO.

## Licence

MIT. See [LICENSE](LICENSE).

## Releasing (maintainers)

1. Bump `<Version>` in the csproj files and add a `## vX.Y.Z` section to `CHANGELOG.md`.
2. If the Android app changed: `bash android/build.sh && cp android/build/oppo.apk src/Emby.Oppo/Remote/` (signed with the private key) and commit the APK.
3. `git tag vX.Y.Z && git push origin vX.Y.Z`. GitHub Actions builds the plugins and publishes the release.
