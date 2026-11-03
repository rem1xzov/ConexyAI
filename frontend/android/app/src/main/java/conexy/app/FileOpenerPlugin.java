package conexy.app;

import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.net.Uri;
import android.util.Base64;

import androidx.core.content.FileProvider;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;

/**
 * ATTACHMENT_ORIGINALS: открывает файл, который пользователь сам отправил в чат, в системном
 * приложении (просмотрщик PDF, офис, галерея). В WebView скачивание blob-ссылок не работает,
 * поэтому байты приходят из страницы, кладутся в cache/share/attachments/ (эта папка уже отдаётся
 * через FileProvider) и открываются через ACTION_VIEW.
 */
@CapacitorPlugin(name = "FileOpener")
public class FileOpenerPlugin extends Plugin {

    /** Предел на один файл — как у вложений сайта (40 МБ сырых байт). */
    private static final long MAX_BYTES = 40_000_000L;

    @PluginMethod
    public void open(PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        String base64 = call.getString("base64");
        String fileName = safeName(call.getString("fileName"));
        String contentType = call.getString("contentType");
        if (base64 == null || base64.isEmpty()) {
            call.reject("No file data", "NO_DATA");
            return;
        }
        if (contentType == null || contentType.isEmpty() || !contentType.contains("/")) {
            contentType = "application/octet-stream";
        }

        File file;
        try {
            byte[] bytes = Base64.decode(base64, Base64.DEFAULT);
            if (bytes.length > MAX_BYTES) {
                call.reject("File is too large", "TOO_LARGE");
                return;
            }
            File dir = new File(new File(getContext().getCacheDir(), "share"), "attachments");
            if (!dir.isDirectory() && !dir.mkdirs()) {
                call.reject("Cannot create the cache folder", "IO");
                return;
            }
            // Старые копии не нужны: открытый файл уже передан другому приложению.
            File[] old = dir.listFiles();
            if (old != null) {
                for (File f : old) {
                    //noinspection ResultOfMethodCallIgnored
                    f.delete();
                }
            }
            file = new File(dir, fileName);
            try (FileOutputStream out = new FileOutputStream(file)) {
                out.write(bytes);
            }
        } catch (IllegalArgumentException | IOException e) {
            call.reject("Cannot write the file", "IO");
            return;
        }

        Uri uri = FileProvider.getUriForFile(getContext(), getContext().getPackageName() + ".fileprovider", file);
        Intent view = new Intent(Intent.ACTION_VIEW);
        view.setDataAndType(uri, contentType);
        view.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        Intent chooser = Intent.createChooser(view, fileName);
        chooser.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_GRANT_READ_URI_PERMISSION);
        try {
            getActivity().startActivity(chooser);
        } catch (ActivityNotFoundException e) {
            call.reject("No app can open this file", "NO_APP");
            return;
        }
        JSObject result = new JSObject();
        result.put("opened", true);
        call.resolve(result);
    }

    /** Только имя файла, без путей и управляющих символов. */
    private static String safeName(String raw) {
        String name = raw == null ? "" : raw;
        int slash = Math.max(name.lastIndexOf('/'), name.lastIndexOf('\\'));
        if (slash >= 0) {
            name = name.substring(slash + 1);
        }
        name = name.replaceAll("[\\p{Cntrl}:*?\"<>|]", "_").trim();
        if (name.isEmpty() || name.equals(".") || name.equals("..")) {
            name = "file";
        }
        return name.length() > 120 ? name.substring(name.length() - 120) : name;
    }
}
