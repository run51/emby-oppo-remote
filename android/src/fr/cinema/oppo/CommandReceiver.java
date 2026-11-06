package fr.cinema.oppo;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** Receives the notification buttons (previous / play-pause / next / stop) and sends the command to the plugin. */
public class CommandReceiver extends BroadcastReceiver {

    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null || !MediaControls.ACTION_COMMAND.equals(intent.getAction())) return;
        String code = intent.getStringExtra(MediaControls.EXTRA_CODE);
        if (!MediaControls.validCode(code)) return;
        final PendingResult pending = goAsync();
        MediaControls.send(context, code, pending::finish);
        MediaControls controls = MediaControls.peek();
        if (controls != null) controls.afterCommand(code);
    }
}
