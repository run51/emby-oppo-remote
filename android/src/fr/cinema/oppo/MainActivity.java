package fr.cinema.oppo;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.speech.RecognizerIntent;
import android.text.InputType;
import android.view.KeyEvent;
import android.view.View;
import android.view.Window;
import android.view.WindowInsets;
import android.view.WindowInsetsController;
import android.view.WindowManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.TextView;

import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Locale;

/**
 * "OPPO" app: shows full screen the remote control served by the Emby plugin (.../emby/Oppo/Remote).
 * The interface lives in the plugin: its updates arrive without reinstalling the app.
 * User-visible texts are in res/values (English) and res/values-fr (French).
 */
public class MainActivity extends Activity {

    private static final String PREFS = "oppo";
    private static final String KEY_SERVER = "server";
    private static final String DEFAULT_SERVER = "http://";
    private static final int VOICE = 1;
    private static final int NOTIFICATIONS = 2;
    private static final String KEY_NOTIF_ASKED = "notif_asked";

    private WebView web;
    private boolean errorShown;
    /** Home edition: true while the page shows a reachable amplifier; the phone's volume keys then drive it. */
    private volatile boolean ampKeys;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        Window w = getWindow();
        w.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        w.setStatusBarColor(Color.rgb(14, 15, 18));
        w.setNavigationBarColor(Color.rgb(14, 15, 18));

        web = new WebView(this);
        web.setBackgroundColor(Color.rgb(14, 15, 18));
        setContentView(web);
        immersive();

        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setDatabaseEnabled(true);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setSupportZoom(false);
        s.setUserAgentString(s.getUserAgentString() + " OppoAndroid/1.3");
        web.addJavascriptInterface(new Bridge(), "OppoAndroid");
        web.setWebChromeClient(new WebChromeClient());
        web.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                Uri target = request.getUrl();
                Uri home = Uri.parse(server());
                if (target.getHost() != null && target.getHost().equals(home.getHost())) return false;
                try { startActivity(new Intent(Intent.ACTION_VIEW, target)); } catch (ActivityNotFoundException e) { /* nothing to open it with */ }
                return true;
            }

            @Override
            public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                if (request.isForMainFrame() && !errorShown) {
                    errorShown = true;
                    askServer(getString(R.string.server_error, error.getDescription()));
                }
            }

            @Override
            public void onPageFinished(WebView view, String url) {
                errorShown = false;
            }
        });

        if (state != null) web.restoreState(state);
        else if (getSharedPreferences(PREFS, MODE_PRIVATE).contains(KEY_SERVER)) load();
        else askServer(getString(R.string.server_prompt));
    }

    private String server() {
        return getSharedPreferences(PREFS, MODE_PRIVATE).getString(KEY_SERVER, DEFAULT_SERVER);
    }

    private void load() {
        web.loadUrl(server().replaceAll("/+$", "") + "/emby/Oppo/Remote");
    }

    private void askServer(String message) {
        final EditText input = new EditText(this);
        input.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        input.setText(server());
        input.setSelectAllOnFocus(true);
        TextView label = new TextView(this);
        label.setText(message);
        label.setPadding(0, 0, 0, 16);
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(56, 32, 56, 0);
        box.addView(label);
        box.addView(input);
        new AlertDialog.Builder(this, android.R.style.Theme_Material_Dialog_Alert)
                .setTitle(R.string.dialog_title)
                .setView(box)
                .setCancelable(false)
                .setPositiveButton(R.string.connect, (d, which) -> {
                    String value = input.getText().toString().trim();
                    if (!value.startsWith("http")) value = "http://" + value;
                    getSharedPreferences(PREFS, MODE_PRIVATE).edit().putString(KEY_SERVER, value).apply();
                    errorShown = false;
                    load();
                })
                .show();
    }

    /** Full screen: status and navigation bars hidden, they reappear with a swipe. */
    private void immersive() {
        if (Build.VERSION.SDK_INT >= 30) {
            WindowInsetsController c = getWindow().getInsetsController();
            if (c != null) {
                c.hide(WindowInsets.Type.statusBars() | WindowInsets.Type.navigationBars());
                c.setSystemBarsBehavior(WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
            }
        } else {
            getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                    | View.SYSTEM_UI_FLAG_FULLSCREEN | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                    | View.SYSTEM_UI_FLAG_LAYOUT_STABLE | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
                    | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN);
        }
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) immersive();
    }

    @Override
    protected void onSaveInstanceState(Bundle out) {
        super.onSaveInstanceState(out);
        web.saveState(out);
    }

    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        int code = event.getKeyCode();
        if (ampKeys && (code == KeyEvent.KEYCODE_VOLUME_UP || code == KeyEvent.KEYCODE_VOLUME_DOWN || code == KeyEvent.KEYCODE_VOLUME_MUTE)) {
            // Volume keys go to the amplifier instead of the phone (key repeats while held; the page throttles).
            if (event.getAction() == KeyEvent.ACTION_DOWN) {
                String action = code == KeyEvent.KEYCODE_VOLUME_UP ? "up" : code == KeyEvent.KEYCODE_VOLUME_DOWN ? "down" : "mute";
                if (action.equals("mute") && event.getRepeatCount() > 0) return true;
                web.evaluateJavascript("window.oppoAmpKey && window.oppoAmpKey('" + action + "')", null);
            }
            return true;
        }
        return super.dispatchKeyEvent(event);
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (keyCode == KeyEvent.KEYCODE_BACK && web.canGoBack()) {
            web.goBack();
            return true;
        }
        return super.onKeyDown(keyCode, event);
    }

    @Override
    protected void onDestroy() {
        // The page can no longer report the state: do not leave a stale notification behind
        if (isFinishing()) {
            MediaControls controls = MediaControls.peek();
            if (controls != null) controls.hide();
        }
        super.onDestroy();
    }

    /** Android 13+: asks once for the notification permission (refusal: the notification is just not shown). */
    private void askNotificationsOnce() {
        if (Build.VERSION.SDK_INT < 33) return;
        if (checkSelfPermission("android.permission.POST_NOTIFICATIONS") == PackageManager.PERMISSION_GRANTED) return;
        SharedPreferences p = getSharedPreferences(PREFS, MODE_PRIVATE);
        if (p.getBoolean(KEY_NOTIF_ASKED, false)) return;
        p.edit().putBoolean(KEY_NOTIF_ASKED, true).apply();
        requestPermissions(new String[] { "android.permission.POST_NOTIFICATIONS" }, NOTIFICATIONS);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != VOICE) return;
        String text = "";
        if (resultCode == RESULT_OK && data != null) {
            ArrayList<String> results = data.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS);
            if (results != null && !results.isEmpty()) text = results.get(0);
        }
        web.evaluateJavascript("window.oppoVoiceResult && window.oppoVoiceResult(" + JSONObject.quote(text) + ")", null);
    }

    /** Functions exposed to the web page (window.OppoAndroid). */
    private final class Bridge {
        @JavascriptInterface
        public boolean canListen() {
            return getPackageManager().queryIntentActivities(new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH), 0).size() > 0;
        }

        @JavascriptInterface
        public void listen() {
            runOnUiThread(() -> {
                Intent i = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH);
                i.putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM);
                i.putExtra(RecognizerIntent.EXTRA_LANGUAGE, Locale.getDefault().toLanguageTag());
                i.putExtra(RecognizerIntent.EXTRA_PROMPT, getString(R.string.voice_prompt));
                try { startActivityForResult(i, VOICE); } catch (ActivityNotFoundException e) { /* no speech recognition available */ }
            });
        }

        @JavascriptInterface
        public void changeServer() {
            runOnUiThread(() -> askServer(getString(R.string.server_prompt)));
        }

        @JavascriptInterface
        public String version() {
            return "1.3.1";
        }

        /** Called by the page: the amplifier row is shown (true) or hidden (false). */
        @JavascriptInterface
        public void ampAvailable(boolean available) {
            ampKeys = available && "home".equals(getString(R.string.edition));
        }

        /** "standard" (no home automation) or "home" (Showtime, All off, amplifier volume). */
        @JavascriptInterface
        public String edition() {
            return getString(R.string.edition);
        }

        /**
         * Player state for the lock-screen / notification controls, called by the page on every state update:
         * {"base": "http://host:8096", "token": "...", "state": "PLAY"|"PAUSE"|"STOP"|""|..., "title": "...",
         *  "chapter": "3 / 13", "position": seconds, "duration": seconds, "image": "http://..."}.
         * state "" / STOP / SCREEN SAVER or an empty title removes the notification.
         */
        @JavascriptInterface
        public void mediaState(final String json) {
            if (json == null) return;
            final boolean active = MediaControls.isActiveState(json);
            runOnUiThread(() -> {
                if (active) askNotificationsOnce();
                MediaControls.get(MainActivity.this).update(json);
            });
        }
    }
}
