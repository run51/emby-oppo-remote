package fr.cinema.oppo;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.drawable.Icon;
import android.media.MediaMetadata;
import android.media.session.MediaSession;
import android.media.session.PlaybackState;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;

import org.json.JSONObject;

import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Lock-screen / notification media controls for the movie playing on the OPPO.
 * The web page pushes the player state through window.OppoAndroid.mediaState(json);
 * this class mirrors it in a MediaSession and a MediaStyle notification.
 * Commands (buttons, headset, lock screen) are sent to the Emby plugin over HTTP.
 * All state changes run on the main thread.
 */
final class MediaControls {

    static final String PREFS = "oppo";
    static final String KEY_BASE = "media_base";
    static final String KEY_TOKEN = "media_token";
    static final String ACTION_COMMAND = "fr.cinema.oppo.COMMAND";
    static final String EXTRA_CODE = "code";

    private static final String CHANNEL = "playback";
    private static final int NOTIFICATION_ID = 1;
    private static final String CUSTOM_STOP = "stop";
    private static final ExecutorService IO = Executors.newFixedThreadPool(2);
    private static final Handler MAIN = new Handler(Looper.getMainLooper());

    private static MediaControls instance;

    private final Context app;
    private MediaSession session;
    private boolean shown;

    // Last known state
    private String base = "", token = "", title = "", chapter = "", image = "";
    private boolean playing;
    private boolean buffering;
    private long positionMs, durationMs;

    // Small artwork cache (URL -> bitmap)
    private final Map<String, Bitmap> artCache = new LinkedHashMap<String, Bitmap>(4, 0.75f, true) {
        @Override
        protected boolean removeEldestEntry(Map.Entry<String, Bitmap> eldest) {
            return size() > 4;
        }
    };
    private String artLoading;

    /** While the notification is shown, the app asks the plugin for the state itself: in the background the page stops reporting it. */
    private static final long POLL_MS = 5000;
    private final Runnable poll = new Runnable() {
        @Override public void run() {
            if (!shown) return;
            refresh();
            MAIN.postDelayed(this, POLL_MS);
        }
    };
    private boolean polling;

    private MediaControls(Context context) {
        app = context.getApplicationContext();
    }

    /** Main thread only. */
    static MediaControls get(Context context) {
        if (instance == null) instance = new MediaControls(context);
        return instance;
    }

    /** Main thread only; null if the process has no controls yet. */
    static MediaControls peek() {
        return instance;
    }

    /** True if the state means a title is playing or paused (used to ask for the notification permission). */
    static boolean isActiveState(String json) {
        try {
            String s = new JSONObject(json).optString("state", "").trim().toUpperCase(java.util.Locale.ROOT);
            return s.equals("PLAY") || s.equals("PAUSE");
        } catch (Exception e) {
            return false;
        }
    }

    /** Applies a state pushed by the web page (see the contract in MainActivity.Bridge.mediaState). */
    void update(String json) {
        JSONObject o;
        try { o = new JSONObject(json); } catch (Exception e) { return; }
        String state = o.optString("state", "").trim().toUpperCase(java.util.Locale.ROOT);
        String t = o.optString("title", "").trim();
        if (state.isEmpty() || state.equals("STOP") || state.equals("SCREEN SAVER") || t.isEmpty()) {
            hide();
            return;
        }
        boolean active = state.equals("PLAY") || state.equals("PAUSE");
        if (!active && !shown) return; // LOADING, DISC MENU...: only keep an existing notification

        String b = o.optString("base", "").trim().replaceAll("/+$", "");
        String k = o.optString("token", "");
        if (b.startsWith("http://") || b.startsWith("https://")) base = b;
        if (!k.isEmpty()) token = k;
        SharedPreferences p = app.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        if (!base.equals(p.getString(KEY_BASE, "")) || !token.equals(p.getString(KEY_TOKEN, ""))) {
            p.edit().putString(KEY_BASE, base).putString(KEY_TOKEN, token).apply();
        }

        title = t;
        chapter = o.optString("chapter", "").trim();
        image = o.optString("image", "").trim();
        positionMs = Math.max(0, o.optLong("position", 0)) * 1000L;
        durationMs = Math.max(0, o.optLong("duration", 0)) * 1000L;
        buffering = state.equals("LOADING");
        if (state.equals("PLAY")) playing = true;
        else if (!buffering) playing = false;
        render();
    }

    /** Optimistic update after a command, before the page reports the real state. */
    void afterCommand(String code) {
        if (!shown) return;
        switch (code) {
            case "PAU": playing = false; buffering = false; render(); break;
            case "PLA": playing = true; buffering = false; render(); break;
            case "STP": hide(); break;
            default: break;
        }
    }

    /** Removes the notification, releases the session and forgets the token. */
    void hide() {
        shown = false;
        polling = false;
        MAIN.removeCallbacks(poll);
        NotificationManager nm = (NotificationManager) app.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm != null) nm.cancel(NOTIFICATION_ID);
        if (session != null) {
            session.setActive(false);
            session.release();
            session = null;
        }
        token = "";
        app.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().remove(KEY_TOKEN).apply();
    }

    // ---------------------------------------------------------------- rendering

    private void render() {
        shown = true;
        if (!polling) { polling = true; MAIN.postDelayed(poll, POLL_MS); }
        ensureSession();
        Bitmap art = image.isEmpty() ? null : artCache.get(image);
        if (art == null && !image.isEmpty()) loadArt(image);

        MediaMetadata.Builder md = new MediaMetadata.Builder()
                .putString(MediaMetadata.METADATA_KEY_TITLE, title)
                .putString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE, title);
        if (!chapter.isEmpty()) {
            String sub = app.getString(R.string.media_chapter, chapter);
            md.putString(MediaMetadata.METADATA_KEY_ARTIST, sub)
              .putString(MediaMetadata.METADATA_KEY_DISPLAY_SUBTITLE, sub);
        }
        if (durationMs > 0) md.putLong(MediaMetadata.METADATA_KEY_DURATION, durationMs);
        if (art != null) md.putBitmap(MediaMetadata.METADATA_KEY_ART, art);
        session.setMetadata(md.build());

        int st = buffering ? PlaybackState.STATE_BUFFERING : playing ? PlaybackState.STATE_PLAYING : PlaybackState.STATE_PAUSED;
        session.setPlaybackState(new PlaybackState.Builder()
                .setActions(PlaybackState.ACTION_PLAY | PlaybackState.ACTION_PAUSE | PlaybackState.ACTION_PLAY_PAUSE
                        | PlaybackState.ACTION_SKIP_TO_NEXT | PlaybackState.ACTION_SKIP_TO_PREVIOUS | PlaybackState.ACTION_STOP)
                .addCustomAction(new PlaybackState.CustomAction.Builder(CUSTOM_STOP, app.getString(R.string.media_stop), R.drawable.ic_media_stop).build())
                .setState(st, positionMs, playing ? 1f : 0f, SystemClock.elapsedRealtime())
                .build());
        session.setActive(true);

        Notification.Builder nb = Build.VERSION.SDK_INT >= 26
                ? new Notification.Builder(app, ensureChannel())
                : legacyBuilder();
        nb.setSmallIcon(R.drawable.ic_notification)
          .setContentTitle(title)
          .setContentText(chapter.isEmpty() ? null : app.getString(R.string.media_chapter, chapter))
          .setContentIntent(openApp())
          .setVisibility(Notification.VISIBILITY_PUBLIC)
          .setOngoing(playing)
          .setShowWhen(false)
          .setOnlyAlertOnce(true)
          .addAction(action(R.drawable.ic_media_previous, R.string.media_previous, "PRE"))
          .addAction(playing
                  ? action(R.drawable.ic_media_pause, R.string.media_pause, "PAU")
                  : action(R.drawable.ic_media_play, R.string.media_play, "PLA"))
          .addAction(action(R.drawable.ic_media_next, R.string.media_next, "NXT"))
          .addAction(action(R.drawable.ic_media_stop, R.string.media_stop, "STP"))
          .setStyle(new Notification.MediaStyle()
                  .setMediaSession(session.getSessionToken())
                  .setShowActionsInCompactView(0, 1, 2));
        if (art != null) nb.setLargeIcon(art);
        NotificationManager nm = (NotificationManager) app.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm == null) return;
        try {
            nm.notify(NOTIFICATION_ID, nb.build()); // silently ignored if the permission was refused
        } catch (SecurityException e) {
            // notifications not allowed: the MediaSession alone still handles media buttons
        }
    }

    @SuppressWarnings("deprecation")
    private Notification.Builder legacyBuilder() {
        return new Notification.Builder(app).setPriority(Notification.PRIORITY_LOW);
    }

    private String ensureChannel() {
        NotificationManager nm = (NotificationManager) app.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm != null && Build.VERSION.SDK_INT >= 26) {
            NotificationChannel ch = new NotificationChannel(CHANNEL, app.getString(R.string.channel_playback), NotificationManager.IMPORTANCE_LOW);
            ch.setShowBadge(false);
            ch.setSound(null, null);
            ch.setLockscreenVisibility(Notification.VISIBILITY_PUBLIC);
            nm.createNotificationChannel(ch); // no-op if it exists (the name is refreshed for the current language)
        }
        return CHANNEL;
    }

    private void ensureSession() {
        if (session != null) return;
        session = new MediaSession(app, "OPPO");
        session.setFlags(MediaSession.FLAG_HANDLES_MEDIA_BUTTONS | MediaSession.FLAG_HANDLES_TRANSPORT_CONTROLS);
        session.setSessionActivity(openApp());
        session.setCallback(new MediaSession.Callback() {
            @Override public void onPlay() { command("PLA"); }
            @Override public void onPause() { command("PAU"); }
            @Override public void onSkipToNext() { command("NXT"); }
            @Override public void onSkipToPrevious() { command("PRE"); }
            @Override public void onStop() { command("STP"); }
            @Override public void onCustomAction(String action, android.os.Bundle extras) {
                if (CUSTOM_STOP.equals(action)) command("STP");
            }
        }, MAIN);
    }

    private void command(String code) {
        send(app, code, null);
        afterCommand(code);
    }

    private PendingIntent openApp() {
        Intent i = new Intent(app, MainActivity.class)
                .setAction(Intent.ACTION_MAIN)
                .addCategory(Intent.CATEGORY_LAUNCHER)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        return PendingIntent.getActivity(app, 0, i, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
    }

    private Notification.Action action(int icon, int label, String code) {
        Intent i = new Intent(app, CommandReceiver.class).setAction(ACTION_COMMAND).putExtra(EXTRA_CODE, code);
        PendingIntent pi = PendingIntent.getBroadcast(app, code.hashCode(), i, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Action.Builder(Icon.createWithResource(app, icon), app.getString(label), pi).build();
    }

    private void loadArt(final String url) {
        if (url.equals(artLoading)) return;
        artLoading = url;
        final String b = base, k = token;
        IO.execute(() -> {
            Bitmap bmp = null;
            HttpURLConnection c = null;
            try {
                c = (HttpURLConnection) new URL(url).openConnection();
                c.setConnectTimeout(5000);
                c.setReadTimeout(5000);
                // Send the token only to the Emby server itself
                if (!k.isEmpty() && !b.isEmpty() && url.startsWith(b + "/")) c.setRequestProperty("X-Emby-Token", k);
                if (c.getResponseCode() == 200) {
                    try (InputStream in = c.getInputStream()) { bmp = BitmapFactory.decodeStream(in); }
                }
            } catch (Exception e) {
                // no artwork
            } finally {
                if (c != null) c.disconnect();
            }
            final Bitmap result = bmp;
            MAIN.post(() -> {
                if (url.equals(artLoading)) artLoading = null;
                if (result == null) return;
                artCache.put(url, result);
                if (shown && url.equals(image)) render();
            });
        });
    }

    /** Reads the player state from the plugin (worker thread) and applies it (main thread). */
    void refresh() {
        IO.execute(() -> {
            final String json = fetchState(app);
            if (json != null) MAIN.post(() -> update(json));
        });
    }

    /**
     * GET {base}/emby/Oppo/Remote/State, converted to the mediaState JSON contract. Worker thread only.
     * Returns null if the server cannot be reached (the display is then left as it is).
     */
    static String fetchState(Context context) {
        SharedPreferences p = context.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        final String b = p.getString(KEY_BASE, ""), k = p.getString(KEY_TOKEN, "");
        if (b.isEmpty() || k.isEmpty()) return null;
        HttpURLConnection c = null;
        try {
            c = (HttpURLConnection) new URL(b + "/emby/Oppo/Remote/State").openConnection();
            c.setConnectTimeout(5000);
            c.setReadTimeout(8000);
            c.setRequestProperty("X-Emby-Token", k);
            c.setRequestProperty("Accept", "application/json");
            c.setRequestProperty("Accept-Language", java.util.Locale.getDefault().getLanguage());
            if (c.getResponseCode() != 200) return null;
            StringBuilder sb = new StringBuilder();
            try (InputStream in = c.getInputStream()) {
                byte[] buf = new byte[4096];
                int n;
                java.io.ByteArrayOutputStream out = new java.io.ByteArrayOutputStream();
                while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                sb.append(out.toString("UTF-8"));
            }
            JSONObject s = new JSONObject(sb.toString());
            boolean reachable = s.optBoolean("Reachable", false);
            String itemId = s.optString("ItemId", ""), tag = s.optString("ImageTag", "");
            JSONObject o = new JSONObject();
            o.put("base", b);
            o.put("token", k);
            o.put("state", reachable ? s.optString("State", "") : "");
            o.put("title", s.optString("Title", ""));
            o.put("chapter", s.optString("Chapter", ""));
            o.put("position", Math.round(s.optDouble("Position", 0)));
            o.put("duration", Math.round(s.optDouble("Duration", 0)));
            o.put("image", !itemId.isEmpty() && !tag.isEmpty() && !"null".equals(tag)
                    ? b + "/emby/Items/" + itemId + "/Images/Primary?maxHeight=300&quality=85&tag=" + tag : "");
            return o.toString();
        } catch (Exception e) {
            return null;
        } finally {
            if (c != null) c.disconnect();
        }
    }

    // ---------------------------------------------------------------- commands

    /** Allowed command codes. */
    static boolean validCode(String code) {
        return "PAU".equals(code) || "PLA".equals(code) || "PRE".equals(code) || "NXT".equals(code) || "STP".equals(code);
    }

    /**
     * Sends POST {base}/emby/Oppo/Remote/Key?Code=CODE with the X-Emby-Token header, off the main thread.
     * base/token come from the private SharedPreferences saved by update(). done (optional) runs afterwards on the worker thread.
     */
    static void send(Context context, final String code, final Runnable done) {
        SharedPreferences p = context.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        final String b = p.getString(KEY_BASE, ""), k = p.getString(KEY_TOKEN, "");
        if (!validCode(code) || b.isEmpty() || k.isEmpty()) {
            if (done != null) done.run();
            return;
        }
        IO.execute(() -> {
            HttpURLConnection c = null;
            try {
                c = (HttpURLConnection) new URL(b + "/emby/Oppo/Remote/Key?Code=" + code).openConnection();
                c.setRequestMethod("POST");
                c.setConnectTimeout(5000);
                c.setReadTimeout(5000);
                c.setRequestProperty("X-Emby-Token", k);
                c.setDoOutput(true);
                c.setFixedLengthStreamingMode(0);
                c.getOutputStream().close();
                c.getResponseCode();
            } catch (Exception e) {
                // server unreachable: the next state from the page corrects the display
            } finally {
                if (c != null) c.disconnect();
            }
            // Show the result (new chapter, position) right away, even with the app in the background.
            try { Thread.sleep(900); } catch (InterruptedException ignored) { }
            final String json = fetchState(context);
            if (json != null) {
                MAIN.post(() -> {
                    MediaControls controls = peek();
                    if (controls != null && controls.shown) controls.update(json);
                });
            }
            if (done != null) done.run();
        });
    }
}
