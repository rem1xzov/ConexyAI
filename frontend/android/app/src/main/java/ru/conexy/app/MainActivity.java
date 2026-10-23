package ru.conexy.app;

import android.Manifest;
import android.content.pm.PackageManager;
import android.webkit.PermissionRequest;
import android.webkit.WebChromeClient;
import android.webkit.WebView;

import androidx.core.content.ContextCompat;

import com.getcapacitor.BridgeActivity;
import com.getcapacitor.BridgeWebChromeClient;

/**
 * MEDIA_PERMISSIONS: WebView по умолчанию не отдаёт getUserMedia ни за камеру, ни за микрофон,
 * даже когда приложение получило системные разрешения. Без этого голосовой ввод не работает,
 * и система даже не показывает запрос пользователю.
 *
 * <p>Разрешение Android запрашивается лениво, в момент первого getUserMedia: платформа сама
 * показывает диалог и не спрашивает о микрофоне тех, кто им не пользуется.
 */
public class MainActivity extends BridgeActivity {

    private static final int AUDIO_REQUEST = 4211;
    private static final int CAMERA_REQUEST = 4212;

    private PermissionRequest pendingRequest;

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
