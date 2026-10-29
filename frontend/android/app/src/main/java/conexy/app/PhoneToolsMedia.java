package conexy.app;

import android.app.WallpaperManager;
import android.appwidget.AppWidgetManager;
import android.content.ComponentName;
import android.content.ContentResolver;
import android.content.ContentUris;
import android.content.Context;
import android.content.Intent;
import android.database.Cursor;
import android.graphics.Bitmap;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.MediaStore;
import android.provider.OpenableColumns;
import android.webkit.MimeTypeMap;

import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.List;
import java.util.Locale;

/** Галерея, выбор фото, просмотр картинки, виджет с фото, обои. */
final class PhoneToolsMedia {
    private static final long PICKED_TTL_MS = 7L * 24 * 60 * 60 * 1000;
    private static final long PICKED_MAX_BYTES = 300L * 1024 * 1024;

    private PhoneToolsMedia() {}

    // ---------------------------------------------------------------- gallery_list

    static ToolResult galleryList(Context context, JSONObject args) {
        boolean videos = "videos".equals(args.optString("media", "images"));
        String permission = videos ? "videos" : "photos";
        if (!"granted".equals(PhonePermissions.state(context, permission))) {
            return ToolResult.err("Нет доступа к " + (videos ? "видео" : "фото") + " в галерее: вызовите "
                    + "phone_request_permission " + permission + ", либо используйте phone_gallery_pick "
                    + "(пользователь сам выберет файлы).");
        }
        int limit = PhoneFmt.clamp(args.optInt("limit", 30), 1, 200);
        Uri base = videos ? MediaStore.Video.Media.EXTERNAL_CONTENT_URI : MediaStore.Images.Media.EXTERNAL_CONTENT_URI;
        String[] projection = videos
                ? new String[] { MediaStore.MediaColumns._ID, MediaStore.MediaColumns.DISPLAY_NAME,
                        MediaStore.MediaColumns.SIZE, MediaStore.MediaColumns.WIDTH, MediaStore.MediaColumns.HEIGHT,
                        MediaStore.MediaColumns.MIME_TYPE, MediaStore.MediaColumns.DATE_ADDED,
                        MediaStore.Video.VideoColumns.BUCKET_DISPLAY_NAME, MediaStore.Video.VideoColumns.DURATION }
                : new String[] { MediaStore.MediaColumns._ID, MediaStore.MediaColumns.DISPLAY_NAME,
                        MediaStore.MediaColumns.SIZE, MediaStore.MediaColumns.WIDTH, MediaStore.MediaColumns.HEIGHT,
                        MediaStore.MediaColumns.MIME_TYPE, MediaStore.MediaColumns.DATE_ADDED,
                        MediaStore.Images.ImageColumns.BUCKET_DISPLAY_NAME };
        ContentResolver resolver = context.getContentResolver();
        StringBuilder out = new StringBuilder();
        int count = 0;
        try {
            Cursor cursor;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                Bundle query = new Bundle();
                query.putInt(ContentResolver.QUERY_ARG_LIMIT, limit);
                query.putStringArray(ContentResolver.QUERY_ARG_SORT_COLUMNS,
                        new String[] { MediaStore.MediaColumns.DATE_ADDED });
                query.putInt(ContentResolver.QUERY_ARG_SORT_DIRECTION, ContentResolver.QUERY_SORT_DIRECTION_DESCENDING);
                cursor = resolver.query(base, projection, query, null);
            } else {
                cursor = resolver.query(base, projection, null, null,
                        MediaStore.MediaColumns.DATE_ADDED + " DESC LIMIT " + limit);
            }
            if (cursor == null) {
                return ToolResult.err("Галерея недоступна (MediaStore вернул пустой результат)");
            }
            try {
                while (cursor.moveToNext() && count < limit) {
                    long id = cursor.getLong(0);
                    Uri uri = ContentUris.withAppendedId(base, id);
                    out.append(uri).append(" | ").append(cursor.getString(1)).append(" | ")
                            .append(cursor.getInt(3)).append('x').append(cursor.getInt(4)).append(" | ")
                            .append(PhoneFmt.size(cursor.getLong(2))).append(" | ")
                            .append(PhoneFmt.time(cursor.getLong(6) * 1000)).append(" | ")
                            .append(cursor.getString(7) == null ? "" : cursor.getString(7));
                    if (videos) {
                        out.append(" | ").append(PhoneFmt.duration(cursor.getLong(8)));
                    }
                    out.append('\n');
                    count++;
                }
            } finally {
                cursor.close();
            }
        } catch (SecurityException error) {
            return ToolResult.err("Системa отказала в доступе к галерее: " + error.getMessage());
        }
        StringBuilder header = new StringBuilder("Последние " + (videos ? "видео" : "фото") + ": " + count + "\n");
        if (Build.VERSION.SDK_INT >= 34 && !videos
                && !"granted".equals(fullAccess(context, android.Manifest.permission.READ_MEDIA_IMAGES))) {
            header.append("⚠ У приложения частичный доступ: видны только фото, которые выбрал пользователь.\n");
        }
        if (count == 0) {
            return ToolResult.ok(header + "(пусто)");
        }
        header.append("uri | имя | размер px | вес | дата | альбом\n");
        return ToolResult.ok(header.toString() + out);
    }

    private static String fullAccess(Context context, String permission) {
        return context.checkSelfPermission(permission) == android.content.pm.PackageManager.PERMISSION_GRANTED
                ? "granted" : "denied";
    }

    // ---------------------------------------------------------------- gallery_pick

    static Intent pickIntent(String media, boolean multiple, boolean systemPicker) {
        String type = "videos".equals(media) ? "video/*" : "image/*";
        Intent intent;
        if (systemPicker && Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            intent = new Intent(MediaStore.ACTION_PICK_IMAGES);
            intent.setType(type);
            if (multiple) {
                intent.putExtra(MediaStore.EXTRA_PICK_IMAGES_MAX, Math.min(MediaStore.getPickImagesMaxLimit(), 20));
            }
        } else {
            intent = new Intent(Intent.ACTION_GET_CONTENT);
            intent.setType(type);
            intent.addCategory(Intent.CATEGORY_OPENABLE);
            if (multiple) {
                intent.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
            }
        }
        return intent;
    }

    /** Копирует выбранное в приватную папку приложения, чтобы доступ не пропал вместе с временным разрешением. */
    static ToolResult copyPicked(Context context, List<Uri> uris) {
        if (uris.isEmpty()) {
            return ToolResult.ok("Пользователь ничего не выбрал.");
        }
        File dir = new File(context.getFilesDir(), "picked");
        //noinspection ResultOfMethodCallIgnored
        dir.mkdirs();
        cleanOld(dir);
        StringBuilder out = new StringBuilder();
        int saved = 0;
        long stamp = System.currentTimeMillis();
        for (int i = 0; i < uris.size(); i++) {
            Uri uri = uris.get(i);
            try {
                String mime = context.getContentResolver().getType(uri);
                String ext = extensionFor(context, uri, mime);
                File target = new File(dir, "pick_" + stamp + "_" + i + (ext.isEmpty() ? "" : "." + ext));
                boolean complete = false;
                try (InputStream in = context.getContentResolver().openInputStream(uri);
                        OutputStream os = new FileOutputStream(target)) {
                    if (in == null) {
                        throw new IOException("пустой поток");
                    }
                    byte[] buffer = new byte[64 * 1024];
                    int n;
                    long total = 0;
                    while ((n = in.read(buffer)) > 0) {
                        total += n;
                        if (total > PICKED_MAX_BYTES) {
                            throw new IOException("файл больше " + PhoneFmt.size(PICKED_MAX_BYTES));
                        }
                        os.write(buffer, 0, n);
                    }
                    complete = true;
                } finally {
                    if (!complete) {
                        //noinspection ResultOfMethodCallIgnored
                        target.delete();
                    }
                }
                saved++;
                out.append(target.getPath()).append(" | ").append(mime == null ? "?" : mime).append(" | ")
                        .append(PhoneFmt.size(target.length())).append('\n');
            } catch (Exception error) {
                out.append("не удалось скопировать ").append(uri).append(": ")
                        .append(error.getClass().getSimpleName()).append('\n');
            }
        }
        if (saved == 0) {
            return ToolResult.err("Не удалось сохранить выбранные файлы:\n" + out);
        }
        return ToolResult.ok("Выбрано файлов: " + saved + ". Пути можно передавать в phone_image_view, "
                + "phone_widget_photo, phone_set_wallpaper, phone_share, phone_file_copy:\n" + out);
    }

    private static void cleanOld(File dir) {
        File[] files = dir.listFiles();
        if (files == null) {
            return;
        }
        long threshold = System.currentTimeMillis() - PICKED_TTL_MS;
        for (File file : files) {
            if (file.isFile() && file.lastModified() < threshold) {
                //noinspection ResultOfMethodCallIgnored
                file.delete();
            }
        }
    }

    private static String extensionFor(Context context, Uri uri, String mime) {
        String ext = mime == null ? null : MimeTypeMap.getSingleton().getExtensionFromMimeType(mime);
        if (ext != null) {
            return ext;
        }
        try (Cursor cursor = context.getContentResolver().query(uri, new String[] { OpenableColumns.DISPLAY_NAME },
                null, null, null)) {
            if (cursor != null && cursor.moveToFirst()) {
                String name = cursor.getString(0);
                int dot = name == null ? -1 : name.lastIndexOf('.');
                if (dot >= 0 && dot < name.length() - 1) {
                    return name.substring(dot + 1).toLowerCase(Locale.ROOT).replaceAll("[^a-z0-9]", "");
                }
            }
        } catch (Exception ignored) {
            // имя недоступно
        }
        return "bin";
    }

    // ---------------------------------------------------------------- image_view

    static ToolResult imageView(Context context, JSONObject args) {
        try {
            Uri uri = UriResolver.parse(context, PhoneFmt.str(args, "uri"));
            Bitmap bitmap = ImageUtil.decode(context, uri, 1280);
            int w = bitmap.getWidth();
            int h = bitmap.getHeight();
            byte[] jpeg = ImageUtil.toJpeg(bitmap, 1280, 80);
            return ToolResult.ok("IMAGE_BASE64:image/jpeg:" + ImageUtil.base64(jpeg)
                    + "\nИзображение " + uri + " (" + w + "x" + h + " после сжатия, " + PhoneFmt.size(jpeg.length) + ")");
        } catch (IOException error) {
            return ToolResult.err(error.getMessage() == null ? "Не удалось открыть изображение" : error.getMessage());
        } catch (SecurityException error) {
            return ToolResult.err("Нет доступа к этому изображению: выдайте доступ к галерее (phone_request_permission photos) "
                    + "или выберите файл через phone_gallery_pick");
        } catch (OutOfMemoryError error) {
            return ToolResult.err("Изображение слишком большое для обработки");
        }
    }

    // ---------------------------------------------------------------- widget_photo

    static ToolResult widgetPhoto(Context context, JSONObject args) {
        String caption = PhoneFmt.str(args, "caption");
        try {
            Uri uri = UriResolver.parse(context, PhoneFmt.str(args, "uri"));
            Bitmap bitmap = ImageUtil.decode(context, uri, 1024);
            byte[] jpeg = ImageUtil.toJpeg(bitmap, 1024, 85);
            File dir = new File(context.getFilesDir(), "widgets");
            //noinspection ResultOfMethodCallIgnored
            dir.mkdirs();
            File file = new File(dir, "widget_" + System.currentTimeMillis() + ".jpg");
            try (OutputStream out = new FileOutputStream(file)) {
                out.write(jpeg);
            }
            PhotoWidgetProvider.enqueuePending(context, file.getPath(), caption == null ? "" : caption);

            AppWidgetManager manager = AppWidgetManager.getInstance(context);
            ComponentName provider = new ComponentName(context, PhotoWidgetProvider.class);
            int before = manager.getAppWidgetIds(provider).length;
            boolean supported = Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && manager.isRequestPinAppWidgetSupported();
            if (supported && manager.requestPinAppWidget(provider, null, null)) {
                for (int i = 0; i < 25; i++) {
                    Thread.sleep(1000);
                    if (manager.getAppWidgetIds(provider).length > before) {
                        return ToolResult.ok("Виджет с фото добавлен на главный экран"
                                + (caption != null && !caption.isEmpty() ? " (подпись: «" + caption + "»)" : "") + ".");
                    }
                }
                return ToolResult.ok("Системное окно «Добавить виджет» показано, но за 25 с виджет не появился: "
                        + "пользователь должен нажать «Добавить» и разместить виджет. Фото сохранено и будет подставлено "
                        + "в первый добавленный виджет «Фото ConexyAI».");
            }
            return ToolResult.ok("Фото для виджета сохранено, но лаунчер не поддерживает автоматическое закрепление. "
                    + "Инструкция: долгое нажатие на пустом месте главного экрана → «Виджеты» → «ConexyAI» → "
                    + "«Фото ConexyAI» → перетащите на экран. Сохранённое фото"
                    + (caption != null && !caption.isEmpty() ? " и подпись «" + caption + "»" : "")
                    + " подставятся автоматически.");
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            return ToolResult.err("Прервано");
        } catch (IOException error) {
            return ToolResult.err(error.getMessage() == null ? "Не удалось подготовить фото" : error.getMessage());
        } catch (SecurityException error) {
            return ToolResult.err("Нет доступа к изображению: " + error.getMessage());
        } catch (OutOfMemoryError error) {
            return ToolResult.err("Изображение слишком большое для обработки");
        }
    }

    // ---------------------------------------------------------------- wallpaper

    static ToolResult setWallpaper(Context context, JSONObject args) {
        String target = args.optString("target", "both");
        try {
            WallpaperManager manager = WallpaperManager.getInstance(context);
            if (!manager.isWallpaperSupported()) {
                return ToolResult.err("Это устройство не поддерживает смену обоев");
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N && !manager.isSetWallpaperAllowed()) {
                return ToolResult.err("Смена обоев запрещена политикой устройства");
            }
            Uri uri = UriResolver.parse(context, PhoneFmt.str(args, "uri"));
            Bitmap bitmap = ImageUtil.decode(context, uri, 2560);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                int flags;
                switch (target) {
                    case "home":
                        flags = WallpaperManager.FLAG_SYSTEM;
                        break;
                    case "lock":
                        flags = WallpaperManager.FLAG_LOCK;
                        break;
                    case "both":
                        flags = WallpaperManager.FLAG_SYSTEM | WallpaperManager.FLAG_LOCK;
                        break;
                    default:
                        return ToolResult.err("target: home|lock|both");
                }
                manager.setBitmap(bitmap, null, true, flags);
            } else {
                manager.setBitmap(bitmap);
            }
            return ToolResult.ok("Обои установлены (" + target + ").");
        } catch (IOException error) {
            return ToolResult.err(error.getMessage() == null ? "Не удалось установить обои" : error.getMessage());
        } catch (SecurityException error) {
            return ToolResult.err("Нет доступа к изображению или разрешения SET_WALLPAPER: " + error.getMessage());
        } catch (OutOfMemoryError error) {
            return ToolResult.err("Изображение слишком большое для обработки");
        }
    }
}
