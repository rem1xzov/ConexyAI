package conexy.app;

import android.content.Context;
import android.content.SharedPreferences;

import androidx.security.crypto.EncryptedSharedPreferences;
import androidx.security.crypto.MasterKey;

/**
 * JWT и адрес сервера для нативных кнопок уведомлений. Хранится в EncryptedSharedPreferences;
 * если Keystore на устройстве сломан — в приватных SharedPreferences приложения.
 * Токен никогда не пишется в лог.
 */
final class PhoneAuthStore {
    private static final String PLAIN = "conexy_phone_auth_plain";
    private static final String ENCRYPTED = "conexy_phone_auth";
    private static final String KEY_TOKEN = "token";
    private static final String KEY_BASE_URL = "base_url";

    private PhoneAuthStore() {}

    private static SharedPreferences cached;

    /** Создание EncryptedSharedPreferences дорогое (Keystore) — делаем один раз; вызывать не с главного потока. */
    private static synchronized SharedPreferences prefs(Context context) {
        if (cached == null) {
            cached = create(context);
        }
        return cached;
    }

    private static SharedPreferences create(Context context) {
        Context app = context.getApplicationContext();
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
            return app.getSharedPreferences(PLAIN, Context.MODE_PRIVATE);
        }
    }

    static synchronized void save(Context context, String token, String baseUrl) {
        SharedPreferences.Editor editor = prefs(context).edit();
        if (token == null || token.trim().isEmpty()) {
            editor.remove(KEY_TOKEN);
        } else {
            editor.putString(KEY_TOKEN, token.trim());
        }
        if (baseUrl != null && !baseUrl.trim().isEmpty()) {
            editor.putString(KEY_BASE_URL, baseUrl.trim());
        }
        editor.commit();
    }

    static synchronized String token(Context context) {
        return prefs(context).getString(KEY_TOKEN, null);
    }

    static synchronized String baseUrl(Context context) {
        return prefs(context).getString(KEY_BASE_URL, null);
    }
}
