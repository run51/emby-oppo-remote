define(['loading', 'emby-input', 'emby-button', 'emby-select', 'emby-scroller'], function (loading) {
    'use strict';

    var pluginId = '8b1f4e62-6c3d-4f0a-a7e2-91d5c03b7f48';

    // Page texts: English by default, French when the Emby UI / browser language is French.
    var messages = {
        en: {
            player: 'Player',
            oppoAddress: 'OPPO player IP address:',
            oppoAddressHelp: 'UDP-203 / UDP-205 or a compatible clone, on the same network as this server. Enable network standby on the player so that it can be turned on remotely.',
            deviceName: 'Name in the "Play on" menu:',
            startTimeout: 'Start timeout (seconds):',
            remote: 'Remote control',
            remoteHelp: 'Full remote control page (playback, chapters, directional pad, menus, audio and subtitles) to open on a phone and add to its home screen. Sign in with an Emby account.',
            openRemote: 'Open the remote control',
            address: 'address:',
            access: 'Access to movies',
            accessHelp: 'Each folder of your Emby libraries must match a share that the OPPO can mount. "Search" turns the OPPO on and lists the shares it sees; a match is suggested automatically. Check it with "Test".',
            discover: 'Search the OPPO\'s shares',
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
            ampEntity: 'Home Assistant — amplifier entity (e.g. media_player.denon):',
            save: 'Save',
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
            deviceName: 'Nom dans le menu « Lire sur… » :',
            startTimeout: 'Délai de démarrage (secondes) :',
            remote: 'Télécommande',
            remoteHelp: 'Page de télécommande complète (lecture, chapitres, pavé directionnel, menus, audio et sous-titres) à ouvrir sur un téléphone et à ajouter à son écran d\'accueil. Connexion avec un compte Emby.',
            openRemote: 'Ouvrir la télécommande',
            address: 'adresse :',
            access: 'Accès aux films',
            accessHelp: 'Chaque dossier de vos bibliothèques Emby doit correspondre à un partage que l\'OPPO sait monter. « Rechercher » allume l\'OPPO et liste les partages qu\'il voit ; une correspondance est proposée automatiquement. Vérifiez-la avec « Tester ».',
            discover: 'Rechercher les partages de l\'OPPO',
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
            ampEntity: 'Home Assistant — entité de l\'ampli (ex. media_player.denon) :',
            save: 'Enregistrer',
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
        }

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
                config.DeviceName = view.querySelector('#txtDeviceName').value.trim() || 'OPPO';
                config.StartTimeoutSeconds = parseInt(view.querySelector('#txtStartTimeout').value, 10) || 60;
                config.WebhookUrl = view.querySelector('#txtWebhookUrl').value.trim();
                config.RemoteAddress = view.querySelector('#txtRemoteAddress').value.trim();
                config.RemoteApiKey = view.querySelector('#txtRemoteApiKey').value.trim();
                config.RemoteTvEntity = view.querySelector('#txtRemoteTvEntity').value.trim();
                config.HomeAssistantUrl = view.querySelector('#txtHaUrl').value.trim();
                config.HomeAssistantToken = view.querySelector('#txtHaToken').value.trim();
                config.AmplifierEntity = view.querySelector('#txtAmpEntity').value.trim();
                config.Mappings = readAll();
                ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                    loading.hide();
                    Dashboard.processPluginConfigurationUpdateResult(result);
                });
            });
            return false;
        });

        view.addEventListener('viewshow', function () {
            translatePage(view);
            var remoteUrl = ApiClient.getUrl('Oppo/Remote');
            view.querySelector('.lnkOppoRemote').href = remoteUrl;
            view.querySelector('.txtOppoRemoteUrl').textContent = remoteUrl;
            loading.show();
            Promise.all([ApiClient.getPluginConfiguration(pluginId), ApiClient.getVirtualFolders()]).then(function (res) {
                var config = res[0];
                view.querySelector('#txtOppoAddress').value = config.OppoAddress || '';
                view.querySelector('#txtDeviceName').value = config.DeviceName || 'OPPO';
                view.querySelector('#txtStartTimeout').value = config.StartTimeoutSeconds || 60;
                view.querySelector('#txtWebhookUrl').value = config.WebhookUrl || '';
                view.querySelector('#txtRemoteAddress').value = config.RemoteAddress || '';
                view.querySelector('#txtRemoteApiKey').value = config.RemoteApiKey || '';
                view.querySelector('#txtRemoteTvEntity').value = config.RemoteTvEntity || '';
                view.querySelector('#txtHaUrl').value = config.HomeAssistantUrl || '';
                view.querySelector('#txtHaToken').value = config.HomeAssistantToken || '';
                view.querySelector('#txtAmpEntity').value = config.AmplifierEntity || '';
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
