package conexy.app;

import android.content.Context;
import android.net.Uri;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileNotFoundException;
import java.io.IOException;
import java.io.InputStream;

/** Разбор uri/путей, которые приходят от модели: content:// (MediaStore, picker), file:// или путь. */
final class UriResolver {
    private UriResolver() {}

    static Uri parse(Context context, String value) throws IOException {
        if (value == null || value.trim().isEmpty()) {
            throw new IOException("Не передан uri");
        }
        String v = value.trim();
        if (v.startsWith("content://")) {
            Uri uri = Uri.parse(v);
            String authority = uri.getAuthority();
            if (authority != null && authority.startsWith(context.getPackageName())) {
                throw new IOException("Используйте путь к файлу, а не внутренний content://-адрес приложения");
            }
            return uri;
        }
        File file = PhoneToolsFiles.resolve(context, v, true);
        if (!file.isFile()) {
            throw new IOException("Файл не найден: " + file.getPath());
        }
        return Uri.fromFile(file);
    }

    static InputStream open(Context context, Uri uri) throws IOException {
        if ("file".equals(uri.getScheme())) {
            return new FileInputStream(new File(uri.getPath()));
        }
        InputStream in = context.getContentResolver().openInputStream(uri);
        if (in == null) {
            throw new FileNotFoundException("Не удалось открыть " + uri);
        }
        return in;
    }
}
