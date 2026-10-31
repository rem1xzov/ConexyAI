package conexy.app;

import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.webkit.WebView;

import com.getcapacitor.Bridge;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;

import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

/**
 * BRIDGE_ORIGIN_GUARD: нативные плагины (инструменты телефона, установка APK, микрофон) доступны
 * только странице нашего сервера. Если WebView каким-то образом оказался на чужом сайте, его JS
 * не должен получить доступ к мосту.
 */
final class OriginGuard {
    /** SERVER_ORIGIN: единственный адрес сервера; из JS адрес больше не принимается. */
    static final String SERVER_URL = "https://conexyai.ru";

    private static final long UI_WAIT_MS = 2_000;

    private OriginGuard() {}

    static boolean isTrustedUrl(String url) {
        if (url == null || url.isEmpty()) {
            return false;
        }
        try {
            Uri uri = Uri.parse(url);
            String host = uri.getHost();
            int port = uri.getPort();
            return "https".equalsIgnoreCase(uri.getScheme())
                    && host != null
                    && ("conexyai.ru".equalsIgnoreCase(host) || "www.conexyai.ru".equalsIgnoreCase(host))
                    && (port == -1 || port == 443);
        } catch (Throwable error) {
            return false;
        }
    }

    /** Текущий URL WebView; читается на UI-потоке (WebView не потокобезопасен). */
    static String currentUrl(Bridge bridge) {
        if (bridge == null) {
            return null;
        }
        final WebView webView = bridge.getWebView();
        if (webView == null) {
            return null;
        }
        if (Looper.myLooper() == Looper.getMainLooper()) {
            return webView.getUrl();
        }
        final AtomicReference<String> url = new AtomicReference<>();
        final CountDownLatch latch = new CountDownLatch(1);
        new Handler(Looper.getMainLooper()).post(() -> {
            try {
                url.set(webView.getUrl());
            } catch (Throwable ignored) {
                // WebView уже уничтожен — считаем источник недоверенным
            } finally {
                latch.countDown();
            }
        });
        try {
            if (!latch.await(UI_WAIT_MS, TimeUnit.MILLISECONDS)) {
                return null;
            }
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            return null;
        }
        return url.get();
    }

    /** @return true, если вызов пришёл со страницы нашего сервера; иначе вызов отклонён. */
    static boolean check(Plugin plugin, PluginCall call) {
        if (isTrustedUrl(currentUrl(plugin.getBridge()))) {
            return true;
        }
        call.reject("Вызов разрешён только со страницы " + SERVER_URL, "UNTRUSTED_ORIGIN");
        return false;
    }
}
