define(['loading', 'emby-input', 'emby-button', 'emby-select', 'emby-scroller'], function (loading) {
    'use strict';

    var pluginId = '8b1f4e62-6c3d-4f0a-a7e2-91d5c03b7f48';

    // Page texts: English by default, French when the Emby UI / browser language is French.
    var messages = {
        en: {
            player: 'Player',
            oppoAddress: 'OPPO player IP address:',
            oppoAddressHelp: 'UDP-203 / UDP-205 or a compatible clone, on the same network as this server. Enable network standby on the player so that it can be turned on remotely.',
            ipControlAddress: 'IP control through (optional):',
            ipControlAddressHelp: 'The player accepts a single IP control connection (port 23). If another device controls it too (an Unfolded Circle remote, for example), run an oppo-multiplexer and enter its address, e.g. 192.168.1.20:23. Empty: the player itself.',
            deviceName: 'Name in the "Play on" menu:',
            startTimeout: 'Start timeout (seconds):',
            dlnaDiscImages: 'Play over DLNA: ISO files, Blu-ray and DVD folders, MKV (oppo-dv-mkv mod)',
            updates: 'Version and updates', installUpdate: 'Update', restartEmby: 'Restart Emby', releaseNotes: "What's new",
            updChecking: 'Looking for updates…', updInstalled: 'Installed: OPPO {0}, ISO Blu-ray {1}.',
            updAvailable: 'Version {0} is available.', updUpToDate: 'Up to date (latest release: {0}).', updInstalling: 'Downloading and installing…',
            updRestarting: 'Emby is restarting. Reload this page in a minute.', updConfirm: 'Install version {0} of both plugins? Emby must be restarted afterwards.',
            resumeWithoutMenus: 'Resume Blu-rays without their menus',
            resumeWithoutMenusHelp: 'When a Blu-ray is resumed, its main movie starts directly at the resume point instead of the disc\'s menu.',
            dlnaDiscImagesHelp: 'Requires the oppo-dv-mkv mod on the player with "DLNA Disc Images" on. The plugin serves the movie itself to the player: an ISO, a Blu-ray folder or a DVD folder (VIDEO_TS) plays as a disc (menus and Dolby Vision included), an MKV, MP4 or M2TS file as a file. No share is mounted; the share above is only used if the movie does not start.',
            discServerPort: 'Disc image server port:',
            discServerPortHelp: 'A free port on this server, reachable by the player. Restart Emby after changing it.',
            remote: 'Remote control',
            remoteHelp: 'Full remote control page (playback, chapters, directional pad, menus, audio and subtitles) to open on a phone and add to its home screen. Sign in with an Emby account.',
            openRemote: 'Open the remote control',
            address: 'address:',
            access: 'Access to movies',
            accessHelp: 'Each folder of your Emby libraries must match a share that the OPPO can mount. "Search" turns the OPPO on and lists the shares it sees; a match is suggested automatically. Check it with "Test".',
            discover: 'Search the OPPO\'s shares',
            testOppo: 'Test the connection',
            sshRestart: 'Jailbroken player: allow restarting it over SSH',
            sshRestartHelp: 'Adds a "Restart the OPPO" button to the remote: the same as unplugging the player, without touching the cable (stuck DLNA, IP control not answering…). Needs the jailbreak\'s jb_overlay with SSH.',
            sshPassword: 'Root password (SSH):',
            sshPasswordHelp: 'The jb_overlay\'s ROOT_PWD, "oppo" by default.',
            testSsh: 'Test SSH',
            windowsNotice: 'This Emby server runs on Windows. The OPPO cannot use NFS with Windows (no NFS server), and it only speaks SMB 1, which Windows 10/11 disables. If the player has the oppo-dv-mkv mod, tick "Play over DLNA" below instead: the plugin then sends the movies itself, no share needed (allow Emby through the Windows firewall, port 8099). Otherwise, turn on SMB 1 in Windows (Turn Windows features on or off > "SMB 1.0/CIFS File Sharing Support", all its boxes; an old protocol, keep it to your home network) and enter the real share name with a Windows username and password.',
            homeAutomation: 'Home automation',
            webhookUrl: 'Webhook URL (optional):',
            webhookHelp: 'Called with POST at the start (<code>{"event":"detected","title":…}</code>) and at the end (<code>"stopped"</code>) of each movie, and by the remote control\'s Showtime / All off buttons (<code>"seance_on"</code> / <code>"seance_off"</code>). Home Assistant example: <code>http://homeassistant:8123/api/webhook/my_id</code>',
            ucAddress: 'Unfolded Circle remote — address (optional):',
            ucAddressHelp: 'If set, the Remote 2/3 turns the TV on over the network when a movie starts and with the Showtime button.',
            ucApiKey: 'Remote — API key:',
            ucTvEntity: 'Remote — TV entity (e.g. lgwebos_driver.main.media_player.192.168.1.20):',
            homeAutomationHelp: 'Optional. The Showtime, All off and amplifier volume buttons are shown in the "OPPO Home" Android app and in the browser remote.',
            haUrl: 'Home Assistant — address (optional, e.g. http://homeassistant:8123):',
            haUrlHelp: 'Used for the amplifier volume and mute buttons of the remote.',
            haToken: 'Home Assistant — long-lived access token:',
            haTokenHelp: 'Create it in Home Assistant: your profile → Security → Long-lived access tokens.',
            tmdbKey: 'TheMovieDb — API key (optional):',
            tmdbKeyHelp: 'Your own key (themoviedb.org → Settings → API): the remote then also shows the French trailers of each film.',
            ampEntity: 'Home Assistant — amplifier entity (e.g. media_player.denon):',
            ampFormatEntity: 'Home Assistant — amplifier audio format sensor (optional, e.g. sensor.denon_audio_format):',
            ampFormatHelp: 'What the amplifier decodes (Dolby TrueHD, DTS-HD MA…), shown while a film plays. Empty: sensor.<amplifier>_audio_format if it exists, else the amplifier\'s sound mode.',
            save: 'Save',
            playMode: 'How the OPPO plays your films',
            modeDlna: 'Over DLNA',
            recommended: 'recommended',
            modeDlnaHelp: 'For a player with the oppo-dv-mkv mod ("DLNA Disc Images" on). The plugin sends the film to the OPPO itself: ISO files, Blu-ray and DVD folders play as a disc (menus, Dolby Vision), MKV and MP4 files as files. No share to set up.',
            modeShare: 'From a network share (NFS or SMB)',
            modeShareHelp: 'For a player without the mod: the OPPO mounts your film folders itself. NFS is recommended; with Windows, the OPPO only speaks SMB 1.',
            testDlna: 'Test DLNA',
            fallbackTitle: 'Fallback if a film does not start',
            fallbackSub: 'Optional: shares the OPPO can mount',
            fallbackHelp: 'These shares are only used when the OPPO does not start a film over DLNA. A failed "Test" here does not prevent playback over DLNA (for example on Windows without SMB 1).',
            optional: 'Optional',
            homeAutomationSub: 'Webhook, Unfolded Circle remote, amplifier volume through Home Assistant',
            trailers: 'Trailers',
            trailersSub: 'TheMovieDb key for French trailers',
            advanced: 'Advanced',
            advancedSub: 'oppo-multiplexer, start timeout, film server port, jailbroken player (SSH)',
            choose: '— choose —',
            manualEntry: 'Manual entry…',
            shareSeen: 'Share seen by the OPPO:',
            protocol: 'Protocol:',
            server: 'Server:',
            share: 'NFS export or SMB share:',
            smbUser: 'SMB username (empty = guest):',
            smbPassword: 'SMB password:',
            test: 'Test',
            notInLibrary: 'not in a library',
            discovering: 'Turning the OPPO on and searching for shares… (up to a minute)',
            smbServer: ' SMB server "{0}": enter its shares manually.',
            discoverFailed: 'The search failed. Check the OPPO address.',
            pickShare: 'Choose a share first.',
            testing: 'Testing…',
            testError: '✘ Error during the test.'
        },
        fr: {
            player: 'Lecteur',
            oppoAddress: 'Adresse IP du lecteur OPPO :',
            oppoAddressHelp: 'UDP-203 / UDP-205 ou clone compatible, sur le même réseau que ce serveur. Activez sur le lecteur la mise en veille réseau pour qu\'il puisse être allumé à distance.',
            ipControlAddress: 'Contrôle IP via (facultatif) :',
            ipControlAddressHelp: 'Le lecteur n\'accepte qu\'une connexion de contrôle IP (port 23). Si un autre appareil le pilote aussi (une télécommande Unfolded Circle, par exemple), lancez un oppo-multiplexer et indiquez son adresse, par ex. 192.168.1.20:23. Vide : le lecteur lui-même.',
            deviceName: 'Nom dans le menu « Lire sur… » :',
            startTimeout: 'Délai de démarrage (secondes) :',
            dlnaDiscImages: 'Lire par DLNA : ISO, dossiers Blu-ray et DVD, MKV (mod oppo-dv-mkv)',
            updates: 'Version et mises à jour', installUpdate: 'Mettre à jour', restartEmby: 'Redémarrer Emby', releaseNotes: 'Nouveautés',
            updChecking: 'Recherche de mises à jour…', updInstalled: 'Installé : OPPO {0}, ISO Blu-ray {1}.',
            updAvailable: 'La version {0} est disponible.', updUpToDate: 'À jour (dernière version publiée : {0}).', updInstalling: 'Téléchargement et installation…',
            updRestarting: 'Emby redémarre. Rechargez cette page dans une minute.', updConfirm: 'Installer la version {0} des deux plugins ? Emby devra ensuite être redémarré.',
            resumeWithoutMenus: 'Reprendre les Blu-ray sans leurs menus',
            resumeWithoutMenusHelp: 'À la reprise d\'un Blu-ray, son film principal démarre directement au point de reprise au lieu du menu du disque.',
            dlnaDiscImagesHelp: 'Nécessite le mod oppo-dv-mkv sur le lecteur, avec « DLNA Disc Images » activé. Le plugin sert lui-même le film au lecteur : un ISO, un dossier Blu-ray ou un dossier DVD (VIDEO_TS) est lu comme un disque (menus et Dolby Vision compris), un fichier MKV, MP4 ou M2TS comme un fichier. Aucun partage n\'est monté ; le partage ci-dessus ne sert que si le film ne démarre pas.',
            discServerPort: 'Port du serveur d\'images disque :',
            discServerPortHelp: 'Un port libre sur ce serveur, joignable par le lecteur. Redémarrez Emby après l\'avoir changé.',
            remote: 'Télécommande',
            remoteHelp: 'Page de télécommande complète (lecture, chapitres, pavé directionnel, menus, audio et sous-titres) à ouvrir sur un téléphone et à ajouter à son écran d\'accueil. Connexion avec un compte Emby.',
            openRemote: 'Ouvrir la télécommande',
            address: 'adresse :',
            access: 'Accès aux films',
            accessHelp: 'Chaque dossier de vos bibliothèques Emby doit correspondre à un partage que l\'OPPO sait monter. « Rechercher » allume l\'OPPO et liste les partages qu\'il voit ; une correspondance est proposée automatiquement. Vérifiez-la avec « Tester ».',
            discover: 'Rechercher les partages de l\'OPPO',
            testOppo: 'Tester la connexion',
            sshRestart: 'Lecteur jailbreaké : autoriser son redémarrage par SSH',
            sshRestartHelp: 'Ajoute un bouton « Redémarrer l\'OPPO » à la télécommande : comme débrancher le lecteur, sans toucher au câble (DLNA bloqué, contrôle IP muet…). Nécessite le jb_overlay du jailbreak avec SSH.',
            sshPassword: 'Mot de passe root (SSH) :',
            sshPasswordHelp: 'Le ROOT_PWD du jb_overlay, « oppo » par défaut.',
            testSsh: 'Tester SSH',
            windowsNotice: 'Ce serveur Emby tourne sous Windows. L\'OPPO ne peut pas utiliser le NFS avec Windows (pas de serveur NFS), et il ne parle que SMB 1, que Windows 10/11 désactive. Si le lecteur a le mod oppo-dv-mkv, cochez plutôt « Lire par DLNA » ci-dessous : le plugin envoie alors les films lui-même, sans partage (autorisez Emby dans le pare-feu Windows, port 8099). Sinon, activez SMB 1 dans Windows (Activer ou désactiver des fonctionnalités Windows > « Support de partage de fichiers SMB 1.0/CIFS », toutes ses cases ; un protocole ancien, à réserver au réseau de la maison) et indiquez le vrai nom du partage avec un utilisateur et un mot de passe Windows.',
            homeAutomation: 'Domotique',
            webhookUrl: 'URL de webhook (facultatif) :',
            webhookHelp: 'Appelée en POST au début (<code>{"event":"detected","title":…}</code>) et à la fin (<code>"stopped"</code>) de chaque film, et par les boutons Séance (<code>"seance_on"</code> / <code>"seance_off"</code>) de la télécommande. Exemple Home Assistant : <code>http://homeassistant:8123/api/webhook/mon_id</code>',
            ucAddress: 'Télécommande Unfolded Circle — adresse (facultatif) :',
            ucAddressHelp: 'Si renseignée, la Remote 2/3 allume la TV par le réseau au lancement d\'un film et au bouton Séance.',
            ucApiKey: 'Télécommande — clé d\'API :',
            ucTvEntity: 'Télécommande — entité de la TV (ex. lgwebos_driver.main.media_player.192.168.1.20) :',
            homeAutomationHelp: 'Facultatif. Les boutons Séance, Tout éteindre et le volume de l\'ampli s\'affichent dans l\'application Android « OPPO Home » et dans la télécommande du navigateur.',
            haUrl: 'Home Assistant — adresse (facultatif, ex. http://homeassistant:8123) :',
            haUrlHelp: 'Sert aux boutons de volume et de sourdine de l\'ampli dans la télécommande.',
            haToken: 'Home Assistant — jeton d\'accès longue durée :',
            haTokenHelp: 'À créer dans Home Assistant : votre profil → Sécurité → Jetons d\'accès longue durée.',
            tmdbKey: 'TheMovieDb — clé d\'API (facultatif) :',
            tmdbKeyHelp: 'Votre propre clé (themoviedb.org → Paramètres → API) : la télécommande affiche alors aussi les bandes-annonces françaises de chaque film.',
            ampEntity: 'Home Assistant — entité de l\'ampli (ex. media_player.denon) :',
            ampFormatEntity: 'Home Assistant — capteur du format audio de l\'ampli (facultatif, ex. sensor.denon_audio_format) :',
            ampFormatHelp: 'Ce que l\'ampli décode (Dolby TrueHD, DTS-HD MA…), affiché pendant la lecture d\'un film. Vide : sensor.<ampli>_audio_format s\'il existe, sinon le mode sonore de l\'ampli.',
            save: 'Enregistrer',
            playMode: 'Comment l\'OPPO lit vos films',
            modeDlna: 'Par DLNA',
            recommended: 'recommandé',
            modeDlnaHelp: 'Pour un lecteur avec le mod oppo-dv-mkv (« DLNA Disc Images » activé). Le plugin envoie lui-même le film à l\'OPPO : ISO, dossiers Blu-ray et DVD lus comme un disque (menus, Dolby Vision), fichiers MKV et MP4. Aucun partage à configurer.',
            modeShare: 'Depuis un partage réseau (NFS ou SMB)',
            modeShareHelp: 'Pour un lecteur sans le mod : l\'OPPO monte vos dossiers de films lui-même. NFS est conseillé ; avec Windows, l\'OPPO ne parle que SMB 1.',
            testDlna: 'Tester le DLNA',
            fallbackTitle: 'Secours si un film ne démarre pas',
            fallbackSub: 'Facultatif : partages que l\'OPPO peut monter',
            fallbackHelp: 'Ces partages ne servent que si l\'OPPO ne démarre pas un film par DLNA. Un « Test » en échec ici n\'empêche pas la lecture par DLNA (par exemple sous Windows sans SMB 1).',
            optional: 'Facultatif',
            homeAutomationSub: 'Webhook, télécommande Unfolded Circle, volume de l\'ampli par Home Assistant',
            trailers: 'Bandes-annonces',
            trailersSub: 'Clé TheMovieDb pour les bandes-annonces françaises',
            advanced: 'Avancé',
            advancedSub: 'oppo-multiplexer, délai de démarrage, port du serveur de films, OPPO déverrouillé (SSH)',
            choose: '— choisir —',
            manualEntry: 'Saisie manuelle…',
            shareSeen: 'Partage vu par l\'OPPO :',
            protocol: 'Protocole :',
            server: 'Serveur :',
            share: 'Export NFS ou partage SMB :',
            smbUser: 'Identifiant SMB (vide = invité) :',
            smbPassword: 'Mot de passe SMB :',
            test: 'Tester',
            notInLibrary: 'hors bibliothèque',
            discovering: 'Allumage de l\'OPPO et recherche des partages… (jusqu\'à une minute)',
            smbServer: ' Serveur SMB « {0} » : partages en saisie manuelle.',
            discoverFailed: 'La recherche a échoué. Vérifiez l\'adresse de l\'OPPO.',
            pickShare: 'Choisissez d\'abord un partage.',
            testing: 'Test en cours…',
            testError: '✘ Erreur pendant le test.'
        }
    };

    var lang = /^fr/i.test(document.documentElement.lang || navigator.language || '') ? 'fr' : 'en';

    function t(key) {
        var text = messages[lang][key];
        return text == null ? messages.en[key] : text;
    }

    // Applies the texts to the static page: data-i18n (text), data-i18n-html (trusted markup from
    // the dictionary above) and data-i18n-label (emby-input labels, already rendered or not).
    function translatePage(view) {
        Array.prototype.forEach.call(view.querySelectorAll('[data-i18n]'), function (el) {
            el.textContent = t(el.getAttribute('data-i18n'));
        });
        Array.prototype.forEach.call(view.querySelectorAll('[data-i18n-html]'), function (el) {
            el.innerHTML = t(el.getAttribute('data-i18n-html'));
        });
        Array.prototype.forEach.call(view.querySelectorAll('[data-i18n-label]'), function (el) {
            var text = t(el.getAttribute('data-i18n-label'));
            el.setAttribute('label', text);
            var label = (el.id && view.querySelector('label[for="' + el.id + '"]')) || (el.parentNode && el.parentNode.querySelector('.inputLabel'));
            if (label) label.textContent = text;
        });
    }

    function esc(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function trimSlashes(s) { return String(s || '').replace(/^\/+|\/+$/g, ''); }

    // Suggests a share for an Emby folder: export identical to the path, otherwise the same path ending.
    function suggest(embyPath, shares) {
        var p = trimSlashes(embyPath).toLowerCase();
        var best = null, bestScore = 0;
        shares.forEach(function (s) {
            var share = trimSlashes(s.Share).toLowerCase();
            if (!share) return;
            var score = 0;
            if (share === p) score = 1000;
            else if (p.endsWith('/' + share) || p === share) score = 500 + share.length;
            else {
                var a = p.split('/'), b = share.split('/'), n = 0;
                while (n < a.length && n < b.length && a[a.length - 1 - n] === b[b.length - 1 - n]) n++;
                score = n * 10;
            }
            if (score > bestScore) { bestScore = score; best = s; }
        });
        return bestScore >= 10 ? best : null;
    }

    function shareKey(s) { return s.Protocol + '|' + s.Server + '|' + trimSlashes(s.Share); }

    function rowHtml(row, shares) {
        var m = row.mapping || {};
        var options = '<option value="">' + esc(t('choose')) + '</option>' + shares.map(function (s) {
            var key = shareKey(s);
            var selected = m.Server && shareKey(m) === key ? ' selected' : '';
            return '<option value="' + esc(key) + '"' + selected + '>' + esc((s.Protocol === 'smb' ? 'SMB ' : 'NFS ') + s.Server + ' : /' + trimSlashes(s.Share)) + '</option>';
        }).join('') + '<option value="manual"' + (m.Server && !shares.some(function (s) { return shareKey(s) === shareKey(m); }) ? ' selected' : '') + '>' + esc(t('manualEntry')) + '</option>';
        var manual = m.Server && !shares.some(function (s) { return shareKey(s) === shareKey(m); });
        var smb = (m.Protocol || 'nfs') === 'smb';

        return '<div class="oppoMapping paperList" data-embypath="' + esc(row.embyPath) + '" style="padding:1em;margin-bottom:1em;">' +
            '<h3 style="margin:0 0 .5em 0;">' + esc(row.embyPath) + (row.library ? ' <span class="fieldDescription">(' + esc(row.library) + ')</span>' : '') + '</h3>' +
            '<div class="selectContainer"><select is="emby-select" class="mapChoice" label="' + esc(t('shareSeen')) + '">' + options + '</select></div>' +
            '<div class="manualFields"' + (manual ? '' : ' style="display:none;"') + '>' +
            '<div class="selectContainer"><select is="emby-select" class="mapProtocol" label="' + esc(t('protocol')) + '"><option value="nfs"' + (smb ? '' : ' selected') + '>NFS</option><option value="smb"' + (smb ? ' selected' : '') + '>SMB / CIFS</option></select></div>' +
            '<div class="inputContainer"><input is="emby-input" type="text" class="mapServer" label="' + esc(t('server')) + '" value="' + esc(m.Server) + '" /></div>' +
            '<div class="inputContainer"><input is="emby-input" type="text" class="mapShare" label="' + esc(t('share')) + '" value="' + esc(m.Share) + '" /></div>' +
            '</div>' +
            '<div class="smbFields"' + (smb ? '' : ' style="display:none;"') + '>' +
            '<div class="inputContainer"><input is="emby-input" type="text" class="mapUser" label="' + esc(t('smbUser')) + '" value="' + esc(m.Username) + '" /></div>' +
            '<div class="inputContainer"><input is="emby-input" type="password" class="mapPassword" label="' + esc(t('smbPassword')) + '" value="' + esc(m.Password) + '" /></div>' +
            '</div>' +
            '<button is="emby-button" type="button" class="raised btnTest"><span>' + esc(t('test')) + '</span></button>' +
            '<span class="testResult fieldDescription" style="margin-left:1em;"></span>' +
            '</div>';
    }

    return function (view) {
        var state = { libraries: [], mappings: [], shares: [] };

        translatePage(view);

        function readRow(el) {
            var choice = el.querySelector('.mapChoice').value;
            var m = { EmbyPath: el.getAttribute('data-embypath'), Username: el.querySelector('.mapUser').value.trim(), Password: el.querySelector('.mapPassword').value };
            if (choice && choice !== 'manual') {
                var parts = choice.split('|');
                m.Protocol = parts[0]; m.Server = parts[1]; m.Share = parts.slice(2).join('|');
            } else {
                m.Protocol = el.querySelector('.mapProtocol').value;
                m.Server = el.querySelector('.mapServer').value.trim();
                m.Share = trimSlashes(el.querySelector('.mapShare').value);
            }
            return m;
        }

        function readAll() {
            return Array.prototype.map.call(view.querySelectorAll('.oppoMapping'), readRow)
                .filter(function (m) { return m.EmbyPath && m.Server && m.Share; });
        }

        function render() {
            var known = {};
            var rows = [];
            state.libraries.forEach(function (lib) {
                (lib.Locations || []).forEach(function (loc) {
                    if (known[loc]) return;
                    known[loc] = true;
                    var existing = state.mappings.filter(function (m) { return m.EmbyPath === loc; })[0];
                    rows.push({ embyPath: loc, library: lib.Name, mapping: existing || suggest(loc, state.shares) && Object.assign({ EmbyPath: loc }, suggest(loc, state.shares)) });
                });
            });
            // Saved mappings that are no longer in a library: keep them.
            state.mappings.forEach(function (m) {
                if (!known[m.EmbyPath]) rows.push({ embyPath: m.EmbyPath, library: t('notInLibrary'), mapping: m });
            });
            view.querySelector('.oppoMappings').innerHTML = rows.map(function (r) { return rowHtml(r, state.shares); }).join('');
            // Emby on Windows ("E:\\Movies", "\\\\nas\\Movies"): NFS is impossible and SMB needs SMB 1, so point to DLNA.
            var windows = rows.some(function (r) { return /^[A-Za-z]:[\\\/]|^\\\\/.test(r.embyPath || ''); });
            var notice = view.querySelector('.windowsNotice');
            notice.textContent = t('windowsNotice');
            notice.hidden = !windows;
        }

        // How the OPPO plays the films: over DLNA (the shares are then only a fallback, folded away) or from a share.
        // Nothing chosen on a first setup: both boxes stay hidden until one is picked.
        function applyMode() {
            var dlna = view.querySelector('#radioDlna').checked, share = view.querySelector('#radioShare').checked;
            view.querySelector('.modeDlna').classList.toggle('selected', dlna);
            view.querySelector('.modeShare').classList.toggle('selected', share);
            view.querySelector('.dlnaModeBox').hidden = !dlna;
            view.querySelector('.shareModeBox').hidden = !share;
            var shares = view.querySelector('.oppoShares');
            shares.hidden = !dlna && !share;
            view.querySelector(dlna ? '.dlnaSharesSlot' : '.shareSharesSlot').appendChild(shares);
        }
        view.querySelector('#radioDlna').addEventListener('change', applyMode);
        view.querySelector('#radioShare').addEventListener('change', applyMode);

        view.querySelector('.btnTestDlna').addEventListener('click', function () {
            var status = view.querySelector('.testDlnaStatus');
            status.textContent = t('testing');
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('Oppo/TestDlna'),
                data: JSON.stringify({
                    Address: view.querySelector('#txtOppoAddress').value.trim(),
                    Port: parseInt(view.querySelector('#txtDiscServerPort').value, 10) || 0,
                    Ssh: view.querySelector('#chkSshRestart').checked,
                    Password: view.querySelector('#txtSshPassword').value
                }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (result) {
                status.textContent = result.Message;
            }, function () {
                status.textContent = t('testError');
            });
        });

        view.querySelector('.btnTestOppo').addEventListener('click', function () {
            var status = view.querySelector('.testOppoStatus');
            status.textContent = t('testing');
            ApiClient.getJSON(ApiClient.getUrl('Oppo/TestConnection', {
                Address: view.querySelector('#txtOppoAddress').value.trim(),
                IpControl: view.querySelector('#txtIpControlAddress').value.trim()
            })).then(function (result) {
                status.textContent = result.Message;
            }, function () {
                status.textContent = t('testError');
            });
        });

        view.querySelector('.btnTestSsh').addEventListener('click', function () {
            var status = view.querySelector('.testSshStatus');
            status.textContent = t('testing');
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('Oppo/TestSsh'),
                data: JSON.stringify({ Address: view.querySelector('#txtOppoAddress').value.trim(), Password: view.querySelector('#txtSshPassword').value }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (result) {
                status.textContent = result.Message;
            }, function () {
                status.textContent = t('testError');
            });
        });

        view.querySelector('.btnDiscover').addEventListener('click', function () {
            var status = view.querySelector('.discoverStatus');
            state.mappings = readAll();
            status.textContent = t('discovering');
            loading.show();
            ApiClient.getJSON(ApiClient.getUrl('Oppo/Discover', { Address: view.querySelector('#txtOppoAddress').value.trim() })).then(function (result) {
                loading.hide();
                status.textContent = result.Message || '';
                var shares = [];
                (result.Servers || []).forEach(function (srv) {
                    if (srv.Protocol === 'nfs') {
                        (srv.Shares || []).forEach(function (share) { shares.push({ Protocol: 'nfs', Server: srv.Address, Share: trimSlashes(share) }); });
                    } else {
                        status.textContent += t('smbServer').replace('{0}', srv.Name);
                    }
                });
                state.shares = shares;
                render();
            }, function () {
                loading.hide();
                status.textContent = t('discoverFailed');
            });
        });

        view.querySelector('.oppoMappings').addEventListener('change', function (e) {
            var row = e.target.closest('.oppoMapping');
            if (!row) return;
            if (e.target.classList.contains('mapChoice')) {
                var manual = e.target.value === 'manual' || !e.target.value;
                row.querySelector('.manualFields').style.display = manual ? '' : 'none';
                var proto = manual ? row.querySelector('.mapProtocol').value : e.target.value.split('|')[0];
                row.querySelector('.smbFields').style.display = proto === 'smb' ? '' : 'none';
            }
            if (e.target.classList.contains('mapProtocol')) {
                row.querySelector('.smbFields').style.display = e.target.value === 'smb' ? '' : 'none';
            }
        });

        view.querySelector('.oppoMappings').addEventListener('click', function (e) {
            var btn = e.target.closest('.btnTest');
            if (!btn) return;
            var row = btn.closest('.oppoMapping');
            var out = row.querySelector('.testResult');
            var m = readRow(row);
            if (!m.Server || !m.Share) { out.textContent = t('pickShare'); return; }
            out.textContent = t('testing');
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('Oppo/TestMapping'),
                data: JSON.stringify({ Address: view.querySelector('#txtOppoAddress').value.trim(), Protocol: m.Protocol, Server: m.Server, Share: m.Share, Username: m.Username, Password: m.Password }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (r) {
                out.textContent = (r.Success ? '✔ ' : '✘ ') + (r.Message || '') + (r.Entries && r.Entries.length ? ' : ' + r.Entries.slice(0, 5).join(', ') : '');
            }, function () { out.textContent = t('testError'); });
        });

        view.querySelector('.oppoConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            loading.show();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                config.OppoAddress = view.querySelector('#txtOppoAddress').value.trim();
                config.IpControlAddress = view.querySelector('#txtIpControlAddress').value.trim();
                config.DeviceName = view.querySelector('#txtDeviceName').value.trim() || 'OPPO';
                config.StartTimeoutSeconds = parseInt(view.querySelector('#txtStartTimeout').value, 10) || 60;
                config.DlnaDiscImages = view.querySelector('#radioDlna').checked;
                config.ResumeWithoutMenus = view.querySelector('#chkResumeWithoutMenus').checked;
                config.SshRestart = view.querySelector('#chkSshRestart').checked;
                config.SshPassword = view.querySelector('#txtSshPassword').value;
                config.DiscServerPort = parseInt(view.querySelector('#txtDiscServerPort').value, 10) || 8099;
                config.WebhookUrl = view.querySelector('#txtWebhookUrl').value.trim();
                config.RemoteAddress = view.querySelector('#txtRemoteAddress').value.trim();
                config.RemoteApiKey = view.querySelector('#txtRemoteApiKey').value.trim();
                config.RemoteTvEntity = view.querySelector('#txtRemoteTvEntity').value.trim();
                config.HomeAssistantUrl = view.querySelector('#txtHaUrl').value.trim();
                config.HomeAssistantToken = view.querySelector('#txtHaToken').value.trim();
                config.TmdbApiKey = view.querySelector('#txtTmdbApiKey').value.trim();
                config.AmplifierEntity = view.querySelector('#txtAmpEntity').value.trim();
                config.AmplifierFormatEntity = view.querySelector('#txtAmpFormatEntity').value.trim();
                config.Mappings = readAll();
                ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                    loading.hide();
                    Dashboard.processPluginConfigurationUpdateResult(result);
                });
            });
            return false;
        });

        // Version and updates (GitHub releases)
        function fmt(text) { var args = arguments; return text.replace(/\{(\d)\}/g, function (m, i) { return args[+i + 1] != null ? args[+i + 1] : ''; }); }
        function showUpdate(info, message) {
            var box = view.querySelector('.txtUpdate');
            var lines = [fmt(t('updInstalled'), info.Current || '?', info.IsoBlurayCurrent || '?')];
            if (message) lines.push(message);
            else if (info.Available) lines.push(fmt(t('updAvailable'), info.Latest));
            else if (info.Latest) lines.push(fmt(t('updUpToDate'), info.Latest));
            else if (info.Message) lines.push(info.Message);
            box.textContent = lines.join(' ');
            view.querySelector('.btnInstallUpdate').hidden = !info.Available || !!info.Installed;
            view.querySelector('.btnRestartEmby').hidden = !info.Installed;
            var link = view.querySelector('.lnkRelease');
            link.hidden = !info.ReleaseUrl;
            if (info.ReleaseUrl) link.href = info.ReleaseUrl;
        }
        function checkUpdate() {
            view.querySelector('.txtUpdate').textContent = t('updChecking');
            ApiClient.getJSON(ApiClient.getUrl('Oppo/Update')).then(function (info) { showUpdate(info); }, function () { view.querySelector('.txtUpdate').textContent = ''; });
        }
        view.querySelector('.btnInstallUpdate').addEventListener('click', function () {
            var latest = (view.querySelector('.txtUpdate').textContent.match(/\d+\.\d+\.\d+(?=[^\d]*$)/) || [''])[0];
            if (!window.confirm(fmt(t('updConfirm'), latest))) return;
            view.querySelector('.txtUpdate').textContent = t('updInstalling');
            this.hidden = true;
            ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('Oppo/Update'), dataType: 'json' })
                .then(function (info) { showUpdate(info, info.Message); }, function () { checkUpdate(); });
        });
        view.querySelector('.btnRestartEmby').addEventListener('click', function () {
            this.hidden = true;
            view.querySelector('.txtUpdate').textContent = t('updRestarting');
            ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('System/Restart') });
        });

        view.addEventListener('viewshow', function () {
            translatePage(view);
            checkUpdate();
            var remoteUrl = ApiClient.getUrl('Oppo/Remote');
            view.querySelector('.lnkOppoRemote').href = remoteUrl;
            view.querySelector('.txtOppoRemoteUrl').textContent = remoteUrl;
            loading.show();
            Promise.all([ApiClient.getPluginConfiguration(pluginId), ApiClient.getVirtualFolders()]).then(function (res) {
                var config = res[0];
                view.querySelector('#txtOppoAddress').value = config.OppoAddress || '';
                view.querySelector('#txtIpControlAddress').value = config.IpControlAddress || '';
                view.querySelector('#txtDeviceName').value = config.DeviceName || 'OPPO';
                view.querySelector('#txtStartTimeout').value = config.StartTimeoutSeconds || 60;
                var firstSetup = !config.OppoAddress && !(config.Mappings || []).length && !config.DlnaDiscImages;
                view.querySelector('#radioDlna').checked = !!config.DlnaDiscImages;
                view.querySelector('#radioShare').checked = !config.DlnaDiscImages && !firstSetup;
                applyMode();
                view.querySelector('#chkResumeWithoutMenus').checked = config.ResumeWithoutMenus !== false;
                view.querySelector('#chkSshRestart').checked = !!config.SshRestart;
                view.querySelector('#txtSshPassword').value = config.SshPassword == null ? 'oppo' : config.SshPassword;
                view.querySelector('#txtDiscServerPort').value = config.DiscServerPort || 8099;
                view.querySelector('#txtWebhookUrl').value = config.WebhookUrl || '';
                view.querySelector('#txtRemoteAddress').value = config.RemoteAddress || '';
                view.querySelector('#txtRemoteApiKey').value = config.RemoteApiKey || '';
                view.querySelector('#txtRemoteTvEntity').value = config.RemoteTvEntity || '';
                view.querySelector('#txtHaUrl').value = config.HomeAssistantUrl || '';
                view.querySelector('#txtHaToken').value = config.HomeAssistantToken || '';
                view.querySelector('#txtTmdbApiKey').value = config.TmdbApiKey || '';
                view.querySelector('#txtAmpEntity').value = config.AmplifierEntity || '';
                view.querySelector('#txtAmpFormatEntity').value = config.AmplifierFormatEntity || '';
                state.mappings = config.Mappings || [];
                var folders = res[1];
                state.libraries = (folders && folders.Items) ? folders.Items : (folders || []);
                state.libraries = state.libraries.filter(function (l) { return !l.CollectionType || ['movies', 'tvshows', 'homevideos', 'mixed', 'musicvideos'].indexOf(l.CollectionType) >= 0; });
                render();
                loading.hide();
            });
        });
    };
});
