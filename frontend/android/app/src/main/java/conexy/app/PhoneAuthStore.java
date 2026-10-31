package conexy.app;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Build;

import androidx.security.crypto.EncryptedSharedPreferences;
import androidx.security.crypto.MasterKey;

/**
 * JWT для нативных кнопок уведомлений. Хранится в EncryptedSharedPreferences; если Keystore на
 * устройстве сломан — только в памяти процесса (NO_PLAINTEXT_TOKEN: на диск открытым текстом не пишем).
 * Адрес сервера не хранится и из JS не принимается — см. {@link OriginGuard#SERVER_URL}.
 * Токен никогда не пишется в лог.
 */
final class PhoneAuthStore {
    /** LEGACY_PLAINTEXT: старое открытое хранилище — удаляется при первом обращении. */
    private static final String LEGACY_PLAIN = "conexy_phone_auth_plain";
    private static final String ENCRYPTED = "conexy_phone_auth";
    private static final String KEY_TOKEN = "token";
    /** LEGACY_BASE_URL: раньше адрес приходил из JS; теперь только удаляется. */
    private static final String KEY_BASE_URL = "base_url";

    private PhoneAuthStore() {}

    private static boolean initialized;
    /** null — Keystore недоступен, работаем с {@link #memoryToken}. */
    private static SharedPreferences cached;
    private static String memoryToken;

    /** Создание EncryptedSharedPreferences дорогое (Keystore) — делаем один раз; вызывать не с главного потока. */
    private static synchronized SharedPreferences prefs(Context context) {
        if (!initialized) {
            initialized = true;
            Context app = context.getApplicationContext();
            dropLegacyPlain(app);
            cached = create(app);
            if (cached != null && cached.contains(KEY_BASE_URL)) {
                cached.edit().remove(KEY_BASE_URL).commit();
            }
        }
        return cached;
    }

    private static void dropLegacyPlain(Context app) {
        try {
            app.getSharedPreferences(LEGACY_PLAIN, Context.MODE_PRIVATE).edit().clear().commit();
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                app.deleteSharedPreferences(LEGACY_PLAIN);
            }
        } catch (Throwable ignored) {
            // не критично: содержимое уже очищено
        }
    }

    private static SharedPreferences create(Context app) {
        try {
            MasterKey key = new MasterKey.Builder(app)
                    .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
                    .build();
            return EncryptedSharedPreferences.create(
                    app,
                    ENCRYPTED,
                    key,
                    EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
                    EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM);
        } catch (Throwable error) {
            return null;
        }
    }

    /** token == null/пусто — выход: очищается всё. */
    static synchronized void save(Context context, String token) {
        SharedPreferences prefs = prefs(context);
        String value = token == null || token.trim().isEmpty() ? null : token.trim();
        memoryToken = prefs == null ? value : null;
        if (prefs == null) {
            return;
        }
        SharedPreferences.Editor editor = prefs.edit();
        if (value == null) {
            editor.clear();
        } else {
            editor.putString(KEY_TOKEN, value);
            editor.remove(KEY_BASE_URL);
        }
        editor.commit();
    }

    static synchronized String token(Context context) {
        SharedPreferences prefs = prefs(context);
        return prefs == null ? memoryToken : prefs.getString(KEY_TOKEN, null);
    }
}
