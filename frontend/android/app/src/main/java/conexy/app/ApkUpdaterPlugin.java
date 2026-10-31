package conexy.app;

import android.app.DownloadManager;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.database.Cursor;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.Settings;
import android.util.Log;

import androidx.activity.result.ActivityResult;
import androidx.core.app.NotificationCompat;
import androidx.core.content.ContextCompat;
import androidx.core.content.FileProvider;
import androidx.core.content.pm.PackageInfoCompat;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.ActivityCallback;
import com.getcapacitor.annotation.CapacitorPlugin;

import java.io.File;
import java.security.MessageDigest;
import java.util.Arrays;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;

/**
 * APK_UPDATER: установка обновления «в одно касание».
 *
 * <p>Custom Tab ({@code @capacitor/browser}) умеет только показывать страницы: он открывал
 * страницу релиза GitHub и висел в бесконечной загрузке, потому что Chrome не устанавливает APK.
 * Здесь скачивание отдаётся системному {@link DownloadManager} (прогресс в шторке, докачка),
 * а по завершении файл открывается через {@code FileProvider} системным установщиком.
 */
@CapacitorPlugin(name = "ApkUpdater")
public class ApkUpdaterPlugin extends Plugin {

    private static final String TAG = "ApkUpdater";
    private static final String APK_MIME = "application/vnd.android.package-archive";
    private static final String APK_NAME = "conexy-update.apk";
    private static final String PREFS = "conexy_apk_updater";
    private static final String KEY_DOWNLOAD_ID = "download_id";
    private static final String CHANNEL_ID = "conexy_app_update";
    private static final int NOTIFICATION_ID = 7712;
    /**
     * APK_SOURCE_ALLOWLIST: скачиваем только релизы GitHub (и их CDN — цели редиректа). Переходы
     * DownloadManager не контролируем, поэтому сам файл дополнительно проверяется в {@link #verifyApk}.
     */
    private static final Set<String> ALLOWED_HOSTS = new HashSet<>(Arrays.asList(
            "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"));

    private BroadcastReceiver downloadReceiver;

    @Override
    public void load() {
        downloadReceiver = new BroadcastReceiver() {
            @Override
            public void onReceive(Context context, Intent intent) {
                if (!DownloadManager.ACTION_DOWNLOAD_COMPLETE.equals(intent.getAction())) {
                    return;
                }
                handleDownloadFinished(intent.getLongExtra(DownloadManager.EXTRA_DOWNLOAD_ID, -1));
            }
        };
        ContextCompat.registerReceiver(
                getContext(),
                downloadReceiver,
                new IntentFilter(DownloadManager.ACTION_DOWNLOAD_COMPLETE),
                ContextCompat.RECEIVER_EXPORTED);

        // Загрузку могло закончить, пока приложение было убито системой: доводим установку.
        long pending = prefs().getLong(KEY_DOWNLOAD_ID, -1);
        if (pending != -1) {
            handleDownloadFinished(pending);
        }
    }

    @Override
    protected void handleOnDestroy() {
        if (downloadReceiver != null) {
            try {
                getContext().unregisterReceiver(downloadReceiver);
            } catch (IllegalArgumentException ignored) {
                // Ресивер уже снят вместе с контекстом.
            }
        }
        super.handleOnDestroy();
    }

    @PluginMethod
    public void downloadAndInstall(PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        String url = call.getString("url");
        if (url == null || url.trim().isEmpty()) {
            call.reject("URL_REQUIRED", "Не передан адрес APK");
            return;
        }
        if (!isAllowedUrl(url)) {
            call.reject("Адрес APK вне разрешённых источников (только https GitHub)", "URL_NOT_ALLOWED");
            return;
        }
        if (!canInstallPackages()) {
            // Android 8+ требует отдельное разрешение «Установка неизвестных приложений».
            // Просим его сразу, а загрузку начинаем после возврата из настроек.
            openInstallSettings(call);
            return;
        }
        startDownload(call, url);
    }

    @ActivityCallback
    private void installPermissionResult(PluginCall call, ActivityResult result) {
        if (call == null) {
            return;
        }
        if (canInstallPackages()) {
            startDownload(call, call.getString("url"));
        } else {
            call.reject("INSTALL_PERMISSION_DENIED", "Разрешение на установку приложений не выдано");
        }
    }

    private void openInstallSettings(PluginCall call) {
        Intent intent = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES);
        intent.setData(Uri.parse("package:" + getContext().getPackageName()));
        startActivityForResult(call, intent, "installPermissionResult");
    }

    private void startDownload(PluginCall call, String url) {
        if (url == null) {
            call.reject("URL_REQUIRED", "Не передан адрес APK");
            return;
        }
        if (!isAllowedUrl(url)) {
            call.reject("Адрес APK вне разрешённых источников (только https GitHub)", "URL_NOT_ALLOWED");
            return;
        }
        try {
            Context context = getContext();
            File target = apkFile();
            if (target != null && target.exists()) {
                // DownloadManager отказывается писать поверх существующего файла.
                //noinspection ResultOfMethodCallIgnored
                target.delete();
            }

            DownloadManager.Request request = new DownloadManager.Request(Uri.parse(url));
            request.setMimeType(APK_MIME);
            request.setTitle("ConexyAI");
            request.setDescription("Загрузка обновления");
            request.setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED);
            request.setDestinationInExternalFilesDir(context, Environment.DIRECTORY_DOWNLOADS, APK_NAME);
            request.setAllowedOverRoaming(false);

            DownloadManager manager = (DownloadManager) context.getSystemService(Context.DOWNLOAD_SERVICE);
            if (manager == null) {
                call.reject("DOWNLOAD_MANAGER_UNAVAILABLE", "Системный загрузчик недоступен");
                return;
            }

            long id = manager.enqueue(request);
            prefs().edit().putLong(KEY_DOWNLOAD_ID, id).apply();

            JSObject result = new JSObject();
            result.put("started", true);
            call.resolve(result);
        } catch (Exception error) {
            call.reject("DOWNLOAD_FAILED", "Не удалось начать загрузку", error);
        }
    }

    private void handleDownloadFinished(long downloadId) {
        if (downloadId <= 0) {
            return;
        }
        SharedPreferences prefs = prefs();
        if (prefs.getLong(KEY_DOWNLOAD_ID, -1) != downloadId) {
            return;
        }
        prefs.edit().remove(KEY_DOWNLOAD_ID).apply();

        if (!isDownloadSuccessful(downloadId)) {
            notifyListeners("updateFailed", new JSObject().put("reason", "download"));
            return;
        }
        if (!canInstallPackages()) {
            // Пользователь отозвал разрешение, пока шла загрузка: пусть нажмёт «Обновить» снова.
            notifyListeners("updateFailed", new JSObject().put("reason", "permission"));
            return;
        }
        installApk(apkFile());
    }

    private boolean isDownloadSuccessful(long downloadId) {
        DownloadManager manager = (DownloadManager) getContext().getSystemService(Context.DOWNLOAD_SERVICE);
        if (manager == null) {
            return false;
        }
        try (Cursor cursor = manager.query(new DownloadManager.Query().setFilterById(downloadId))) {
            if (cursor == null || !cursor.moveToFirst()) {
                return false;
            }
            int statusIndex = cursor.getColumnIndex(DownloadManager.COLUMN_STATUS);
            return statusIndex >= 0 && cursor.getInt(statusIndex) == DownloadManager.STATUS_SUCCESSFUL;
        } catch (Exception error) {
            Log.w(TAG, "Не удалось узнать статус загрузки", error);
            return false;
        }
    }

    private void installApk(File apk) {
        if (apk == null || !apk.exists()) {
            notifyListeners("updateFailed", new JSObject().put("reason", "missing"));
            return;
        }
        String problem = verifyApk(apk);
        if (problem != null) {
            Log.w(TAG, "APK отклонён: " + problem);
            //noinspection ResultOfMethodCallIgnored
            apk.delete();
            notifyListeners("updateFailed", new JSObject().put("reason", "verification").put("message", problem));
            return;
        }
        try {
            Uri contentUri = FileProvider.getUriForFile(
                    getContext(), getContext().getPackageName() + ".fileprovider", apk);
            Intent intent = new Intent(Intent.ACTION_VIEW);
            intent.setDataAndType(contentUri, APK_MIME);
            intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            getContext().startActivity(intent);
            notifyListeners("updateInstalling", new JSObject().put("ok", true));
        } catch (Exception error) {
            // Например, установщик недоступен или Android заблокировал запуск активности из фона.
            Log.w(TAG, "Не удалось открыть установщик, показываю уведомление", error);
            postInstallNotification(apk);
            notifyListeners("updateFailed", new JSObject().put("reason", "installer"));
        }
    }

    private void postInstallNotification(File apk) {
        Context context = getContext();
        NotificationManager manager = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
        if (manager == null) {
            return;
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    CHANNEL_ID, "Обновления ConexyAI", NotificationManager.IMPORTANCE_DEFAULT);
            manager.createNotificationChannel(channel);
        }
        try {
            Uri contentUri = FileProvider.getUriForFile(
                    context, context.getPackageName() + ".fileprovider", apk);
            Intent intent = new Intent(Intent.ACTION_VIEW);
            intent.setDataAndType(contentUri, APK_MIME);
            intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);

            int pendingFlags = PendingIntent.FLAG_UPDATE_CURRENT;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                pendingFlags |= PendingIntent.FLAG_IMMUTABLE;
            }
            PendingIntent pending = PendingIntent.getActivity(context, 0, intent, pendingFlags);

            Notification notification = new NotificationCompat.Builder(context, CHANNEL_ID)
                    .setSmallIcon(android.R.drawable.stat_sys_download_done)
                    .setContentTitle("ConexyAI")
                    .setContentText("Нажмите, чтобы установить обновление")
                    .setContentIntent(pending)
                    .setAutoCancel(true)
                    .build();
            manager.notify(NOTIFICATION_ID, notification);
        } catch (Exception error) {
            Log.w(TAG, "Не удалось показать уведомление об установке", error);
        }
    }

    static boolean isAllowedUrl(String url) {
        try {
            Uri uri = Uri.parse(url.trim());
            String host = uri.getHost();
            return "https".equalsIgnoreCase(uri.getScheme())
                    && host != null
                    && ALLOWED_HOSTS.contains(host.toLowerCase(Locale.ROOT))
                    && (uri.getPort() == -1 || uri.getPort() == 443)
                    && uri.getUserInfo() == null;
        } catch (Throwable error) {
            return false;
        }
    }

    /**
     * APK_VERIFY: перед установкой — тот же пакет, та же подпись, что у установленного приложения,
     * и версия новее. Иначе подменённый файл открылся бы в системном установщике от имени приложения.
     *
     * @return описание проблемы или null, если APK можно устанавливать.
     */
    @SuppressWarnings("deprecation")
    private String verifyApk(File apk) {
        try {
            Context context = getContext();
            PackageManager pm = context.getPackageManager();
            String ownPackage = context.getPackageName();
            int flags = Build.VERSION.SDK_INT >= Build.VERSION_CODES.P
                    ? PackageManager.GET_SIGNING_CERTIFICATES
                    : PackageManager.GET_SIGNATURES;

            PackageInfo archive = pm.getPackageArchiveInfo(apk.getAbsolutePath(), flags);
            if (archive == null) {
                return "файл не является корректным APK";
            }
            if (!ownPackage.equals(archive.packageName)) {
                return "чужой пакет: " + archive.packageName;
            }
            PackageInfo installed = pm.getPackageInfo(ownPackage, flags);
            if (PackageInfoCompat.getLongVersionCode(archive) <= PackageInfoCompat.getLongVersionCode(installed)) {
                return "версия не новее установленной";
            }
            Set<String> expected = signerDigests(installed);
            Set<String> actual = signerDigests(archive);
            if (expected.isEmpty() || !expected.equals(actual)) {
                return "подпись не совпадает с установленным приложением";
            }
            return null;
        } catch (Throwable error) {
            return "не удалось проверить APK: " + error.getClass().getSimpleName();
        }
    }

    /** SHA-256 текущих сертификатов подписи (с фолбэком на устаревший signatures до API 28). */
    @SuppressWarnings("deprecation")
    private static Set<String> signerDigests(PackageInfo info) throws Exception {
        Signature[] signatures = null;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P && info.signingInfo != null) {
            signatures = info.signingInfo.getApkContentsSigners();
        }
        if (signatures == null || signatures.length == 0) {
            signatures = info.signatures;
        }
        Set<String> digests = new HashSet<>();
        if (signatures == null) {
            return digests;
        }
        MessageDigest sha = MessageDigest.getInstance("SHA-256");
        for (Signature signature : signatures) {
            byte[] hash = sha.digest(signature.toByteArray());
            StringBuilder hex = new StringBuilder();
            for (byte b : hash) {
                hex.append(String.format(Locale.ROOT, "%02x", b));
            }
            digests.add(hex.toString());
        }
        return digests;
    }

    private boolean canInstallPackages() {
        return Build.VERSION.SDK_INT < Build.VERSION_CODES.O
                || getContext().getPackageManager().canRequestPackageInstalls();
    }

    private File apkFile() {
        File dir = getContext().getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS);
        return dir == null ? null : new File(dir, APK_NAME);
    }

    private SharedPreferences prefs() {
        return getContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }
}
