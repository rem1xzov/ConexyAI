package conexy.app;

import android.app.NotificationManager;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.widget.Toast;

import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/**
 * Кнопки «Разрешить»/«Отклонить» в уведомлении: POST {baseUrl}/api/mobile/actions/{actionId}
 * с Bearer-токеном из {@link PhoneAuthStore}. Токен в лог не попадает.
 */
public class ActionReceiver extends BroadcastReceiver {
    private static final String TAG = "ActionReceiver";

    @Override
    public void onReceive(final Context context, Intent intent) {
        final String action = intent.getAction();
        final boolean approve = NotificationHelper.ACTION_APPROVE.equals(action);
        if (!approve && !NotificationHelper.ACTION_DENY.equals(action)) {
            return;
        }
        final String actionId = intent.getStringExtra(NotificationHelper.EXTRA_ACTION_ID);
        final String tag = intent.getStringExtra(NotificationHelper.EXTRA_NOTIF_TAG);
        final int notifId = intent.getIntExtra(NotificationHelper.EXTRA_NOTIF_ID, 0);
        if (actionId == null || actionId.isEmpty()) {
            return;
        }

        final PendingResult pending = goAsync();
        final Context app = context.getApplicationContext();
        new Thread(() -> {
            try {
                int code = -1;
                try {
                    code = post(app, actionId, approve);
                } catch (Throwable error) {
                    Log.w(TAG, "Не удалось отправить решение: " + error.getClass().getSimpleName());
                }
                // 404/409/410: решение уже принято в другом месте — уведомление больше не нужно.
                boolean done = (code >= 200 && code < 300) || code == 404 || code == 409 || code == 410;
                if (done) {
                    NotificationManager manager = (NotificationManager) app.getSystemService(Context.NOTIFICATION_SERVICE);
                    if (manager != null) {
                        manager.cancel(tag, notifId);
                    }
                } else {
                    new Handler(Looper.getMainLooper()).post(() ->
                            Toast.makeText(app, R.string.notif_action_failed, Toast.LENGTH_LONG).show());
                }
            } finally {
                pending.finish();
            }
        }, "conexy-notif-action").start();
    }

    /** @return HTTP-код или -1, если запрос не отправлен. */
    private static int post(Context context, String actionId, boolean approved) throws Exception {
        String token = PhoneAuthStore.token(context);
        String baseUrl = PhoneAuthStore.baseUrl(context);
        if (token == null || baseUrl == null) {
            return -1;
        }
        while (baseUrl.endsWith("/")) {
            baseUrl = baseUrl.substring(0, baseUrl.length() - 1);
        }
        URL url = new URL(baseUrl + "/api/mobile/actions/" + Uri.encode(actionId));
        if (!"https".equals(url.getProtocol()) && !"http".equals(url.getProtocol())) {
            return -1;
        }
        HttpURLConnection connection = (HttpURLConnection) url.openConnection();
        try {
            connection.setRequestMethod("POST");
            // goAsync даёт ~10 с на весь onReceive, поэтому тайм-ауты короткие.
            connection.setConnectTimeout(4_000);
            connection.setReadTimeout(4_000);
            connection.setDoOutput(true);
            connection.setRequestProperty("Authorization", "Bearer " + token);
            connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
            byte[] body = ("{\"approved\":" + approved + "}").getBytes(StandardCharsets.UTF_8);
            connection.setFixedLengthStreamingMode(body.length);
            try (OutputStream out = connection.getOutputStream()) {
                out.write(body);
            }
            return connection.getResponseCode();
        } finally {
            connection.disconnect();
        }
    }
}
