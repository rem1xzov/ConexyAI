package conexy.app;

import android.app.PendingIntent;
import android.appwidget.AppWidgetManager;
import android.appwidget.AppWidgetProvider;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.os.Build;
import android.view.View;
import android.widget.RemoteViews;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;

/**
 * Виджет «Фото ConexyAI». Картинка и подпись лежат в файлах/настройках приложения: новый виджет
 * получает первое фото из очереди, подготовленной инструментом phone_widget_photo.
 */
public class PhotoWidgetProvider extends AppWidgetProvider {
    private static final String PREFS = "conexy_photo_widget";
    private static final String KEY_PENDING = "pending";
    private static final int MAX_PENDING = 10;
    private static final int RENDER_MAX_SIDE = 720;

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    static synchronized void enqueuePending(Context context, String path, String caption) {
        try {
            SharedPreferences prefs = prefs(context);
            JSONArray queue = new JSONArray(prefs.getString(KEY_PENDING, "[]"));
            JSONObject item = new JSONObject();
            item.put("path", path);
            item.put("caption", caption == null ? "" : caption);
            queue.put(item);
            while (queue.length() > MAX_PENDING) {
                JSONObject dropped = queue.optJSONObject(0);
                if (dropped != null) {
                    //noinspection ResultOfMethodCallIgnored
                    new File(dropped.optString("path")).delete();
                }
                JSONArray trimmed = new JSONArray();
                for (int i = 1; i < queue.length(); i++) {
                    trimmed.put(queue.get(i));
                }
                queue = trimmed;
            }
            prefs.edit().putString(KEY_PENDING, queue.toString()).apply();
        } catch (JSONException ignored) {
            // повреждённая очередь — начинаем заново при следующем вызове
            prefs(context).edit().remove(KEY_PENDING).apply();
        }
    }

    private static synchronized JSONObject popPending(Context context) {
        try {
            SharedPreferences prefs = prefs(context);
            JSONArray queue = new JSONArray(prefs.getString(KEY_PENDING, "[]"));
            if (queue.length() == 0) {
                return null;
            }
            JSONObject first = queue.getJSONObject(0);
            JSONArray rest = new JSONArray();
            for (int i = 1; i < queue.length(); i++) {
                rest.put(queue.get(i));
            }
            prefs.edit().putString(KEY_PENDING, rest.toString()).apply();
            return first;
        } catch (JSONException error) {
            return null;
        }
    }

    @Override
    public void onUpdate(Context context, AppWidgetManager manager, int[] appWidgetIds) {
        for (int id : appWidgetIds) {
            render(context, manager, id);
        }
    }

    @Override
    public void onDeleted(Context context, int[] appWidgetIds) {
        SharedPreferences prefs = prefs(context);
        SharedPreferences.Editor editor = prefs.edit();
        for (int id : appWidgetIds) {
            String raw = prefs.getString("cfg_" + id, null);
            if (raw != null) {
                try {
                    //noinspection ResultOfMethodCallIgnored
                    new File(new JSONObject(raw).optString("path")).delete();
                } catch (JSONException ignored) {
                    // файл не определить — пропускаем
                }
            }
            editor.remove("cfg_" + id);
        }
        editor.apply();
    }

    private static void render(Context context, AppWidgetManager manager, int id) {
        SharedPreferences prefs = prefs(context);
        String raw = prefs.getString("cfg_" + id, null);
        JSONObject cfg = null;
        try {
            if (raw != null) {
                cfg = new JSONObject(raw);
            } else {
                cfg = popPending(context);
                if (cfg != null) {
                    prefs.edit().putString("cfg_" + id, cfg.toString()).apply();
                }
            }
        } catch (JSONException ignored) {
            cfg = null;
        }

        RemoteViews views = new RemoteViews(context.getPackageName(), R.layout.widget_photo);
        Bitmap bitmap = null;
        String caption = "";
        if (cfg != null) {
            caption = cfg.optString("caption", "");
            bitmap = loadScaled(cfg.optString("path"));
        }
        if (bitmap != null) {
            views.setImageViewBitmap(R.id.widget_image, bitmap);
            views.setViewVisibility(R.id.widget_empty, View.GONE);
        } else {
            views.setImageViewBitmap(R.id.widget_image, null);
            views.setViewVisibility(R.id.widget_empty, View.VISIBLE);
        }
        if (caption.isEmpty()) {
            views.setViewVisibility(R.id.widget_caption, View.GONE);
        } else {
            views.setViewVisibility(R.id.widget_caption, View.VISIBLE);
            views.setTextViewText(R.id.widget_caption, caption);
        }

        Intent open = new Intent(context, MainActivity.class);
        open.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP);
        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            flags |= PendingIntent.FLAG_IMMUTABLE;
        }
        views.setOnClickPendingIntent(R.id.widget_root, PendingIntent.getActivity(context, 0, open, flags));
        manager.updateAppWidget(id, views);
    }

    private static Bitmap loadScaled(String path) {
        if (path == null || path.isEmpty() || !new File(path).isFile()) {
            return null;
        }
        BitmapFactory.Options bounds = new BitmapFactory.Options();
        bounds.inJustDecodeBounds = true;
        BitmapFactory.decodeFile(path, bounds);
        int sample = 1;
        int longest = Math.max(bounds.outWidth, bounds.outHeight);
        while (longest > 0 && longest / (sample * 2) >= RENDER_MAX_SIDE) {
            sample *= 2;
        }
        BitmapFactory.Options options = new BitmapFactory.Options();
        options.inSampleSize = sample;
        return BitmapFactory.decodeFile(path, options);
    }
}
