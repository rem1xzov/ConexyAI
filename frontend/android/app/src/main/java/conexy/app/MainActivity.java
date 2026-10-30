package conexy.app;

import android.Manifest;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Bundle;
import android.view.inputmethod.InputMethodManager;
import android.webkit.PermissionRequest;
import android.webkit.WebView;

import androidx.core.content.ContextCompat;
import androidx.core.graphics.Insets;
import androidx.core.view.ViewCompat;
import androidx.core.view.WindowInsetsCompat;

import com.getcapacitor.BridgeActivity;
import com.getcapacitor.BridgeWebChromeClient;
import com.getcapacitor.WebViewListener;

/**
 * MEDIA_PERMISSIONS: WebView по умолчанию не отдаёт getUserMedia ни за камеру, ни за микрофон,
 * даже когда приложение получило системные разрешения. Без этого голосовой ввод не работает,
 * и система даже не показывает запрос пользователю.
 *
 * <p>Разрешение Android запрашивается лениво, в момент первого getUserMedia: платформа сама
 * показывает диалог и не спрашивает о микрофоне тех, кто им не пользуется.
 *
 * <p>SAFE_AREA: Android 15 рисует WebView во весь экран (edge-to-edge), а env(safe-area-inset-*)
 * внутри WebView на Android остаётся нулевым — шапка уезжала под статус-бар, а композер под
 * полосу жестов. Отсюда отступы системных панелей уходят в CSS переменными --safe-*.
 */
public class MainActivity extends BridgeActivity {

    private static final int AUDIO_REQUEST = 4211;
    private static final int CAMERA_REQUEST = 4212;

    private PermissionRequest pendingRequest;

    private int safeTopPx;
    private int safeBottomPx;
    private int safeLeftPx;
    private int safeRightPx;
    private int keyboardPx;
    private boolean insetsKnown;

    @Override
    public void onCreate(Bundle savedInstanceState) {
        // APK_UPDATER / VOICE_INPUT: локальные плагины регистрируются до создания Bridge.
        registerPlugin(ApkUpdaterPlugin.class);
        registerPlugin(SpeechInputPlugin.class);
        // PHONE_AGENT: инструменты телефона, разрешения, уведомления и push.
        registerPlugin(PhoneAgentPlugin.class);
        super.onCreate(savedInstanceState);

        WebView webView = getBridge().getWebView();
        if (webView == null) {
            return;
        }

        // SAFE_AREA: новая страница ещё не знает своих отступов — отдаём их после каждой загрузки.
        getBridge().addWebViewListener(new WebViewListener() {
            @Override
            public void onPageLoaded(WebView view) {
                pushSafeArea(view);
            }
        });

        // SAFE_AREA: слушатель висит на WebView, а не на окне: он срабатывает после того, как
        // Capacitor разместил WebView, и обновляется при повороте и показе/скрытии клавиатуры.
        ViewCompat.setOnApplyWindowInsetsListener(webView, (view, windowInsets) -> {
            Insets bars = windowInsets.getInsets(
                    WindowInsetsCompat.Type.systemBars() | WindowInsetsCompat.Type.displayCutout());
            safeTopPx = bars.top;
            safeBottomPx = bars.bottom;
            safeLeftPx = bars.left;
            safeRightPx = bars.right;
            // KEYBOARD: экранная клавиатура. Если WebView уже сжат системой, эти отступы нулевые,
            // и CSS берёт видимую высоту из visualViewport — обе оценки сходятся в min().
            keyboardPx = windowInsets.getInsets(WindowInsetsCompat.Type.ime()).bottom;
            insetsKnown = true;
            pushSafeArea(webView);
            // Высота WebView могла ещe не пересчитаться в этом кадре — повторяем после раскладки.
            webView.post(() -> pushSafeArea(webView));
            // Insets не поглощаем: WebView рисуется во весь экран, отступы нам нужны только в CSS.
            return windowInsets;
        });
        ViewCompat.requestApplyInsets(webView);
    }

    @Override
    public void onStart() {
        super.onStart();
        WebView webView = getBridge().getWebView();
        if (webView == null) {
            return;
        }

        webView.setWebChromeClient(new BridgeWebChromeClient(getBridge()) {
            @Override
            public void onPermissionRequest(final PermissionRequest request) {
                runOnUiThread(() -> {
                    // Второй запрос не должен затереть первый: тот остался бы без ответа.
                    if (pendingRequest != null) {
                        try {
                            pendingRequest.deny();
                        } catch (RuntimeException ignored) {
                            // запрос уже неактуален
                        }
                        pendingRequest = null;
                    }
                    for (String resource : request.getResources()) {
                        if (PermissionRequest.RESOURCE_AUDIO_CAPTURE.equals(resource)
                                && !hasPermission(Manifest.permission.RECORD_AUDIO)) {
                            pendingRequest = request;
                            requestPermissions(new String[] { Manifest.permission.RECORD_AUDIO }, AUDIO_REQUEST);
                            return;
                        }
                        if (PermissionRequest.RESOURCE_VIDEO_CAPTURE.equals(resource)
                                && !hasPermission(Manifest.permission.CAMERA)) {
                            pendingRequest = request;
                            requestPermissions(new String[] { Manifest.permission.CAMERA }, CAMERA_REQUEST);
                            return;
                        }
                    }
                    request.grant(request.getResources());
                });
            }
        });
    }

    /**
     * KEYBOARD_RESTORE: шторка уведомлений забирает фокус окна и прячет клавиатуру. Когда фокус
     * вернулся, страница сама решает, печатал ли человек до этого (useKeyboardRestore), и возвращает
     * фокус в композер. Программный focus() в WebView не всегда поднимает клавиатуру, поэтому
     * после его просим IME явно.
     */
    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (!hasFocus) {
            return;
        }
        WebView webView = getBridge() != null ? getBridge().getWebView() : null;
        if (webView == null) {
            return;
        }
        webView.evaluateJavascript(
                "(function(){try{return !!(window.__conexyRestoreKeyboard&&window.__conexyRestoreKeyboard());}catch(e){return false;}})()",
                result -> {
                    if (!"true".equals(result)) {
                        return;
                    }
                    // Страница фокусирует поле через кадр + 30 мс — даём ей успеть.
                    webView.postDelayed(() -> {
                        webView.requestFocus();
                        InputMethodManager imm = (InputMethodManager) getSystemService(Context.INPUT_METHOD_SERVICE);
                        if (imm != null) {
                            imm.showSoftInput(webView, InputMethodManager.SHOW_IMPLICIT);
                        }
                    }, 120);
                });
    }

    /** Отдаёт CSS отступы системных панелей в тех же px, в которых считает WebView. */
    private void pushSafeArea(WebView webView) {
        if (!insetsKnown) {
            return;
        }
        float density = getResources().getDisplayMetrics().density;
        String js = "(function(){var s=document.documentElement.style;"
                + "s.setProperty('--safe-top','" + Math.round(safeTopPx / density) + "px');"
                + "s.setProperty('--safe-bottom','" + Math.round(safeBottomPx / density) + "px');"
                + "s.setProperty('--safe-left','" + Math.round(safeLeftPx / density) + "px');"
                + "s.setProperty('--safe-right','" + Math.round(safeRightPx / density) + "px');"
                + "s.setProperty('--kb-native','" + Math.round(keyboardPx / density) + "px');"
                + "s.setProperty('--window-height','" + Math.round(webView.getHeight() / density) + "px');"
                + "})();";
        webView.evaluateJavascript(js, null);
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        PermissionRequest request = pendingRequest;
        pendingRequest = null;
        if (request == null) {
            return;
        }

        boolean granted = grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED;
        if (granted) {
            // Отдаём странице только те ресурсы, которые она просила и которые теперь разрешены.
            java.util.List<String> allowed = new java.util.ArrayList<>();
            for (String resource : request.getResources()) {
                boolean audioOk = PermissionRequest.RESOURCE_AUDIO_CAPTURE.equals(resource)
                        && hasPermission(Manifest.permission.RECORD_AUDIO);
                boolean videoOk = PermissionRequest.RESOURCE_VIDEO_CAPTURE.equals(resource)
                        && hasPermission(Manifest.permission.CAMERA);
                if (audioOk || videoOk) {
                    allowed.add(resource);
                }
            }
            if (!allowed.isEmpty()) {
                request.grant(allowed.toArray(new String[0]));
                return;
            }
        }
        request.deny();
    }

    private boolean hasPermission(String permission) {
        return ContextCompat.checkSelfPermission(this, permission) == PackageManager.PERMISSION_GRANTED;
    }
}
