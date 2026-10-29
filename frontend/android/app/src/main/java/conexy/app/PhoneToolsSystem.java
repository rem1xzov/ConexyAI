package conexy.app;

import android.app.ActivityManager;
import android.app.usage.StorageStats;
import android.app.usage.StorageStatsManager;
import android.app.usage.UsageStats;
import android.app.usage.UsageStatsManager;
import android.content.ActivityNotFoundException;

import android.content.Context;
import android.content.Intent;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.net.Uri;
import android.os.BatteryManager;
import android.os.Build;
import android.os.Environment;
import android.os.PowerManager;
import android.os.Process;
import android.os.StatFs;
import android.os.storage.StorageManager;
import android.provider.MediaStore;
import android.provider.Settings;
import android.util.DisplayMetrics;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.TimeZone;
import java.util.regex.Pattern;

/** Приложения, батарея, хранилище, настройки, запуск, удаление, кэш, остановка. */
final class PhoneToolsSystem {
    private static final Pattern PACKAGE = Pattern.compile("^[A-Za-z0-9_]+(\\.[A-Za-z0-9_]+)+$");
    private static final long DAY_MS = 24L * 60 * 60 * 1000;
    private static final String[] PROTECTED_PACKAGES = {
            "android", "com.android.systemui", "com.android.settings", "com.google.android.gms",
            "com.google.android.gsf", "com.android.phone", "com.android.providers.settings"
    };
    private static final String[] CLEAR_CACHE_TEXTS = { "Очистить кэш", "Clear cache" };
    private static final String[] STORAGE_TEXTS = {
            "Хранилище и кэш", "Хранилище", "Память", "Использование памяти",
            "Storage & cache", "Storage and cache", "Storage", "Memory"
    };
    private static final String[] FORCE_STOP_TEXTS = {
            "Принудительно остановить", "Остановить", "Force stop", "Stop"
    };
    private static final String[] CONFIRM_TEXTS = { "ОК", "OK", "Остановить", "Force stop", "Да", "Yes" };

    private PhoneToolsSystem() {}

    // ---------------------------------------------------------------- вспомогательное

    static JSONObject infoObject(Context context) {
        JSONObject info = new JSONObject();
        try {
            info.put("model", Build.MODEL);
            info.put("manufacturer", Build.MANUFACTURER);
            info.put("androidVersion", Build.VERSION.RELEASE);
            info.put("sdk", Build.VERSION.SDK_INT);
            String version = "";
            try {
                PackageInfo pi = context.getPackageManager().getPackageInfo(context.getPackageName(), 0);
                version = pi.versionName == null ? "" : pi.versionName;
            } catch (PackageManager.NameNotFoundException ignored) {
                // свой пакет всегда найден
            }
            info.put("appVersion", version);
        } catch (JSONException ignored) {
            // ключи — строки
        }
        return info;
    }

    private static boolean validPackage(String pkg) {
        return pkg != null && PACKAGE.matcher(pkg).matches();
    }

    private static boolean isProtectedPackage(String pkg) {
        for (String item : PROTECTED_PACKAGES) {
            if (item.equals(pkg)) {
                return true;
            }
        }
        return false;
    }

    private static ApplicationInfo appInfo(Context context, String pkg) {
        try {
            return context.getPackageManager().getApplicationInfo(pkg, 0);
        } catch (PackageManager.NameNotFoundException error) {
            return null;
        }
    }

    private static boolean isSystem(ApplicationInfo info) {
        return (info.flags & ApplicationInfo.FLAG_SYSTEM) != 0;
    }

    private static Map<String, UsageStats> usage(Context context, int days) {
        if (!PhonePermissions.usageAccessGranted(context)) {
            return null;
        }
        try {
            UsageStatsManager usm = (UsageStatsManager) context.getSystemService(Context.USAGE_STATS_SERVICE);
            if (usm == null) {
                return null;
            }
            long end = System.currentTimeMillis();
            return usm.queryAndAggregateUsageStats(end - days * DAY_MS, end);
        } catch (Throwable error) {
            return null;
        }
    }

    /** {app, data, cache} в байтах или null (нужен usage_access и Android 8+). */
    private static long[] appSizes(Context context, String pkg) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O || !PhonePermissions.usageAccessGranted(context)) {
            return null;
        }
        try {
            StorageStatsManager manager = (StorageStatsManager) context.getSystemService(Context.STORAGE_STATS_SERVICE);
            if (manager == null) {
                return null;
            }
            StorageStats stats = manager.queryStatsForPackage(StorageManager.UUID_DEFAULT, pkg, Process.myUserHandle());
            return new long[] { stats.getAppBytes(), stats.getDataBytes(), stats.getCacheBytes() };
        } catch (Throwable error) {
            return null;
        }
    }

    private static long cacheBytes(Context context, String pkg) {
        long[] sizes = appSizes(context, pkg);
        return sizes == null ? -1 : sizes[2];
    }

    private static String label(Context context, ApplicationInfo info) {
        try {
            return String.valueOf(context.getPackageManager().getApplicationLabel(info));
        } catch (Throwable error) {
            return info.packageName;
        }
    }

    /**
     * Android 10+ блокирует запуск Activity из фона без ошибки (только в logcat). Исключение — выданное
     * «поверх других окон». Делаем попытку в любом случае, но честно предупреждаем об этом.
     */
    static String backgroundStartWarning(Context context) {
        try {
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q || Settings.canDrawOverlays(context)) {
                return "";
            }
            ActivityManager.RunningAppProcessInfo state = new ActivityManager.RunningAppProcessInfo();
            ActivityManager.getMyMemoryState(state);
            if (state.importance <= ActivityManager.RunningAppProcessInfo.IMPORTANCE_VISIBLE) {
                return "";
            }
            return " ⚠ Приложение сейчас не на экране и без доступа overlay: Android мог заблокировать открытие "
                    + "(проверьте phone_screen_read; чтобы запуски из фона работали — phone_request_permission overlay).";
        } catch (Throwable error) {
            return "";
        }
    }

    private static ToolResult startActivityResult(Context context, Intent intent, String okText) {
        try {
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            context.startActivity(intent);
            return ToolResult.ok(okText + backgroundStartWarning(context));
        } catch (ActivityNotFoundException error) {
            return ToolResult.err("На устройстве нет экрана для этого действия");
        } catch (SecurityException error) {
            return ToolResult.err("Система запретила открыть экран: " + error.getMessage());
        } catch (RuntimeException error) {
            return ToolResult.err("Не удалось открыть экран: " + error.getClass().getSimpleName());
        }
    }

    private static Intent appDetailsIntent(String pkg) {
        return new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + pkg));
    }

    private static void sleep(long ms) {
        try {
            Thread.sleep(ms);
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
        }
    }

    // ---------------------------------------------------------------- device info

    static ToolResult deviceInfo(Context context) {
        try {
            JSONObject info = infoObject(context);
            info.put("brand", Build.BRAND);
            info.put("device", Build.DEVICE);
            info.put("locale", Locale.getDefault().toString());
            info.put("timezone", TimeZone.getDefault().getID());
            DisplayMetrics dm = context.getResources().getDisplayMetrics();
            info.put("screen", dm.widthPixels + "x" + dm.heightPixels + " @" + dm.densityDpi + "dpi");
            ActivityManager am = (ActivityManager) context.getSystemService(Context.ACTIVITY_SERVICE);
            if (am != null) {
                ActivityManager.MemoryInfo mi = new ActivityManager.MemoryInfo();
                am.getMemoryInfo(mi);
                info.put("ram", PhoneFmt.size(mi.availMem) + " свободно из " + PhoneFmt.size(mi.totalMem));
            }
            StatFs fs = new StatFs(Environment.getDataDirectory().getPath());
            info.put("storage", PhoneFmt.size(fs.getAvailableBytes()) + " свободно из " + PhoneFmt.size(fs.getTotalBytes()));
            Intent battery = context.registerReceiver(null, new android.content.IntentFilter(Intent.ACTION_BATTERY_CHANGED));
            if (battery != null) {
                info.put("battery", batteryPercent(battery) + "%");
            }
            info.put("uptime", PhoneFmt.duration(android.os.SystemClock.elapsedRealtime()));
            info.put("workspace", PhoneToolsFiles.workspace(context).getPath());
            info.put("all_files_access", PhoneToolsFiles.hasAllFiles());
            info.put("accessibility_enabled", ConexyAccessibilityService.instance() != null);
            return ToolResult.ok(info.toString(2));
        } catch (Throwable error) {
            return ToolResult.err("Не удалось собрать сведения: " + error.getClass().getSimpleName());
        }
    }

    // ---------------------------------------------------------------- приложения

    private static final class AppRow {
        String label;
        String pkg;
        boolean system;
        long size = -1;
        long lastUsed;
        long foreground;
    }

    static ToolResult appsList(Context context, JSONObject args) {
        String filter = args.optString("filter", "all");
        String sort = args.optString("sort", "");
        int limit = PhoneFmt.clamp(args.optInt("limit", 50), 1, 300);
        Map<String, UsageStats> usage = usage(context, 30);
        if (sort.isEmpty()) {
            sort = usage != null ? "last_used" : "name";
        }
        List<AppRow> rows = new ArrayList<>();
        for (ApplicationInfo info : context.getPackageManager().getInstalledApplications(0)) {
            boolean system = isSystem(info);
            if ("user".equals(filter) && system) {
                continue;
            }
            if ("system".equals(filter) && !system) {
                continue;
            }
            AppRow row = new AppRow();
            row.pkg = info.packageName;
            row.label = label(context, info);
            row.system = system;
            if (usage != null) {
                UsageStats stats = usage.get(info.packageName);
                if (stats != null) {
                    row.lastUsed = stats.getLastTimeUsed();
                    row.foreground = stats.getTotalTimeInForeground();
                }
            }
            rows.add(row);
        }
        final String sortKey = sort;
        boolean needSizes = "size".equals(sortKey);
        if (needSizes) {
            for (AppRow row : rows) {
                long[] sizes = appSizes(context, row.pkg);
                row.size = sizes == null ? -1 : sizes[0] + sizes[1] + sizes[2];
            }
        }
        Collections.sort(rows, new Comparator<AppRow>() {
            @Override
            public int compare(AppRow a, AppRow b) {
                switch (sortKey) {
                    case "last_used":
                        return Long.compare(b.lastUsed, a.lastUsed);
                    case "foreground_time":
                        return Long.compare(b.foreground, a.foreground);
                    case "size":
                        return Long.compare(b.size, a.size);
                    default:
                        return a.label.compareToIgnoreCase(b.label);
                }
            }
        });
        int shown = Math.min(limit, rows.size());
        if (!needSizes) {
            for (int i = 0; i < shown; i++) {
                long[] sizes = appSizes(context, rows.get(i).pkg);
                rows.get(i).size = sizes == null ? -1 : sizes[0] + sizes[1] + sizes[2];
            }
        }
        StringBuilder out = new StringBuilder();
        out.append("Приложений: ").append(rows.size()).append(", показано ").append(shown)
                .append(", сортировка: ").append(sortKey).append('\n');
        if (usage == null) {
            out.append("⚠ Нет доступа к статистике использования (last_used/foreground_time/size недоступны): "
                    + "вызовите phone_request_permission usage_access.\n");
        } else {
            out.append("Время в приложении и «последний запуск» — за последние 30 дней.\n");
        }
        for (int i = 0; i < shown; i++) {
            AppRow row = rows.get(i);
            out.append(row.label).append(" | ").append(row.pkg).append(" | ").append(row.system ? "system" : "user");
            if (row.size >= 0) {
                out.append(" | ").append(PhoneFmt.size(row.size));
            }
            if (usage != null) {
                out.append(" | последний запуск: ").append(row.lastUsed > 0 ? PhoneFmt.time(row.lastUsed) : "нет данных")
                        .append(" | на экране: ").append(PhoneFmt.duration(row.foreground));
            }
            out.append('\n');
        }
        return ToolResult.ok(out.toString());
    }

    // ---------------------------------------------------------------- батарея

    private static int batteryPercent(Intent battery) {
        int level = battery.getIntExtra(BatteryManager.EXTRA_LEVEL, -1);
        int scale = battery.getIntExtra(BatteryManager.EXTRA_SCALE, 100);
        return level < 0 || scale <= 0 ? -1 : Math.round(level * 100f / scale);
    }

    static ToolResult batteryReport(Context context) {
        Intent battery = context.registerReceiver(null, new android.content.IntentFilter(Intent.ACTION_BATTERY_CHANGED));
        if (battery == null) {
            return ToolResult.err("Система не отдала данные батареи");
        }
        int percent = batteryPercent(battery);
        float temp = battery.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, 0) / 10f;
        int health = battery.getIntExtra(BatteryManager.EXTRA_HEALTH, BatteryManager.BATTERY_HEALTH_UNKNOWN);
        int status = battery.getIntExtra(BatteryManager.EXTRA_STATUS, BatteryManager.BATTERY_STATUS_UNKNOWN);
        int plugged = battery.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0);
        int voltage = battery.getIntExtra(BatteryManager.EXTRA_VOLTAGE, 0);
        boolean charging = status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL;

        StringBuilder out = new StringBuilder("Батарея\n");
        out.append("Заряд: ").append(percent).append("%\n");
        out.append("Состояние: ").append(statusText(status));
        if (plugged != 0) {
            out.append(" (").append(plugText(plugged)).append(')');
        }
        out.append('\n');
        out.append("Температура: ").append(String.format(Locale.US, "%.1f", temp)).append(" °C\n");
        out.append("Здоровье: ").append(healthText(health)).append('\n');
        if (voltage > 0) {
            out.append("Напряжение: ").append(voltage).append(" мВ\n");
        }
        BatteryManager bm = (BatteryManager) context.getSystemService(Context.BATTERY_SERVICE);
        if (bm != null) {
            long current = bm.getLongProperty(BatteryManager.BATTERY_PROPERTY_CURRENT_NOW);
            if (current != Long.MIN_VALUE && current != 0) {
                out.append("Ток: ").append(Math.round(current / 1000.0)).append(" мА (знак зависит от производителя)\n");
            }
        }
        PowerManager pm = (PowerManager) context.getSystemService(Context.POWER_SERVICE);
        boolean powerSave = pm != null && pm.isPowerSaveMode();
        out.append("Режим экономии: ").append(powerSave ? "включён" : "выключен").append('\n');

        List<String> hints = new ArrayList<>();
        Map<String, UsageStats> usage = usage(context, 1);
        if (usage == null) {
            out.append("\nТоп приложений по времени на экране: недоступен — нет доступа к статистике использования "
                    + "(phone_request_permission usage_access). Android не отдаёт приложениям расход в мА·ч по программам.\n");
            hints.add("Выдайте доступ usage_access, чтобы увидеть, какие приложения больше всего работают на экране.");
        } else {
            List<UsageStats> list = new ArrayList<>(usage.values());
            Collections.sort(list, new Comparator<UsageStats>() {
                @Override
                public int compare(UsageStats a, UsageStats b) {
                    return Long.compare(b.getTotalTimeInForeground(), a.getTotalTimeInForeground());
                }
            });
            out.append("\nТоп приложений по времени на экране за 24 ч (прямого расхода в % Android приложениям не отдаёт):\n");
            int shown = 0;
            for (UsageStats stats : list) {
                if (stats.getTotalTimeInForeground() < 60_000 || shown >= 10) {
                    break;
                }
                ApplicationInfo info = appInfo(context, stats.getPackageName());
                String name = info != null ? label(context, info) : stats.getPackageName();
                out.append("  ").append(name).append(" (").append(stats.getPackageName()).append(") — ")
                        .append(PhoneFmt.duration(stats.getTotalTimeInForeground())).append('\n');
                if (stats.getTotalTimeInForeground() > 2 * 60 * 60 * 1000L && info != null && !isSystem(info)) {
                    hints.add(name + " провело на экране более 2 ч: проверьте его настройки батареи "
                            + "(phone_open_settings app_details, package=" + stats.getPackageName() + ").");
                }
                shown++;
            }
            if (shown == 0) {
                out.append("  (нет заметной активности)\n");
            }
        }

        if (temp >= 40f) {
            hints.add("Батарея горячая (" + String.format(Locale.US, "%.1f", temp)
                    + " °C): закройте тяжёлые приложения, снимите чехол, не заряжайте на солнце.");
        }
        if (health != BatteryManager.BATTERY_HEALTH_GOOD && health != BatteryManager.BATTERY_HEALTH_UNKNOWN) {
            hints.add("Состояние батареи: " + healthText(health) + " — возможно, нужна диагностика/замена.");
        }
        if (!charging && percent >= 0 && percent < 20 && !powerSave) {
            hints.add("Заряд ниже 20%: включите режим экономии заряда (phone_open_settings battery).");
        }
        if (!powerSave && !charging && percent >= 0 && percent < 50) {
            hints.add("Снизьте яркость/тайм-аут экрана (phone_open_settings display), отключите ненужные Wi-Fi/Bluetooth/геолокацию.");
        }
        if (PhonePermissions.state(context, "battery_optimization").equals("granted")) {
            hints.add("ConexyAI исключено из оптимизации батареи — это увеличивает расход; верните оптимизацию, если не нужна работа в фоне.");
        }
        out.append("\nПодсказки по оптимизации:\n");
        if (hints.isEmpty()) {
            out.append("  Критичных проблем не видно.\n");
        } else {
            for (String hint : hints) {
                out.append("  • ").append(hint).append('\n');
            }
        }
        return ToolResult.ok(out.toString());
    }

    private static String statusText(int status) {
        switch (status) {
            case BatteryManager.BATTERY_STATUS_CHARGING:
                return "заряжается";
            case BatteryManager.BATTERY_STATUS_DISCHARGING:
                return "разряжается";
            case BatteryManager.BATTERY_STATUS_FULL:
                return "заряжена";
            case BatteryManager.BATTERY_STATUS_NOT_CHARGING:
                return "не заряжается";
            default:
                return "неизвестно";
        }
    }

    private static String plugText(int plugged) {
        if ((plugged & BatteryManager.BATTERY_PLUGGED_AC) != 0) return "от сети";
        if ((plugged & BatteryManager.BATTERY_PLUGGED_USB) != 0) return "по USB";
        if ((plugged & BatteryManager.BATTERY_PLUGGED_WIRELESS) != 0) return "беспроводная";
        return "подключено";
    }

    private static String healthText(int health) {
        switch (health) {
            case BatteryManager.BATTERY_HEALTH_GOOD:
                return "хорошее";
            case BatteryManager.BATTERY_HEALTH_OVERHEAT:
                return "перегрев";
            case BatteryManager.BATTERY_HEALTH_DEAD:
                return "неисправна";
            case BatteryManager.BATTERY_HEALTH_OVER_VOLTAGE:
                return "повышенное напряжение";
            case BatteryManager.BATTERY_HEALTH_COLD:
                return "переохлаждение";
            case BatteryManager.BATTERY_HEALTH_UNSPECIFIED_FAILURE:
                return "неизвестный сбой";
            default:
                return "неизвестно";
        }
    }

    // ---------------------------------------------------------------- хранилище

    static ToolResult storageReport(Context context) {
        StringBuilder out = new StringBuilder("Хранилище\n");
        try {
            StatFs fs = new StatFs(Environment.getDataDirectory().getPath());
            long total = fs.getTotalBytes();
            long free = fs.getAvailableBytes();
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                try {
                    StorageStatsManager manager = (StorageStatsManager) context.getSystemService(Context.STORAGE_STATS_SERVICE);
                    if (manager != null) {
                        total = manager.getTotalBytes(StorageManager.UUID_DEFAULT);
                        free = manager.getFreeBytes(StorageManager.UUID_DEFAULT);
                    }
                } catch (Throwable ignored) {
                    // остаёмся на значениях StatFs
                }
            }
            out.append("Всего: ").append(PhoneFmt.size(total)).append(", свободно: ").append(PhoneFmt.size(free))
                    .append(", занято: ").append(PhoneFmt.size(total - free)).append(" (")
                    .append(total > 0 ? Math.round((total - free) * 100f / total) : 0).append("%)\n");
        } catch (Throwable error) {
            out.append("Не удалось определить общий объём\n");
        }

        out.append("\nМедиа (по данным MediaStore, нужен доступ к фото/видео/аудио):\n");
        appendMediaSize(context, out, "Фото", MediaStore.Images.Media.EXTERNAL_CONTENT_URI, "photos");
        appendMediaSize(context, out, "Видео", MediaStore.Video.Media.EXTERNAL_CONTENT_URI, "videos");
        appendMediaSize(context, out, "Аудио", MediaStore.Audio.Media.EXTERNAL_CONTENT_URI, "audio");

        if (!PhonePermissions.usageAccessGranted(context) || Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            out.append("\nРазмеры приложений недоступны: нужен доступ usage_access (phone_request_permission usage_access)\n");
        } else {
            List<AppRow> rows = new ArrayList<>();

            for (ApplicationInfo info : context.getPackageManager().getInstalledApplications(0)) {
                if (isSystem(info)) {
                    continue;
                }
                long[] sizes = appSizes(context, info.packageName);
                if (sizes == null) {
                    continue;
                }
                AppRow row = new AppRow();
                row.pkg = info.packageName;
                row.label = label(context, info);
                row.size = sizes[0] + sizes[1] + sizes[2];
                row.lastUsed = sizes[2]; // временно: размер кэша
                rows.add(row);
            }
            Collections.sort(rows, new Comparator<AppRow>() {
                @Override
                public int compare(AppRow a, AppRow b) {
                    return Long.compare(b.size, a.size);
                }
            });
            out.append("\nСамые тяжёлые пользовательские приложения:\n");
            for (int i = 0; i < Math.min(10, rows.size()); i++) {
                AppRow row = rows.get(i);
                out.append("  ").append(row.label).append(" (").append(row.pkg).append(") — ")
                        .append(PhoneFmt.size(row.size)).append(", кэш ").append(PhoneFmt.size(row.lastUsed)).append('\n');
            }
            Collections.sort(rows, new Comparator<AppRow>() {
                @Override
                public int compare(AppRow a, AppRow b) {
                    return Long.compare(b.lastUsed, a.lastUsed);
                }
            });
            out.append("\nБольше всего кэша (кандидаты для phone_clear_cache):\n");
            for (int i = 0; i < Math.min(8, rows.size()); i++) {
                AppRow row = rows.get(i);
                if (row.lastUsed < 20L * 1024 * 1024) {
                    break;
                }
                out.append("  ").append(row.label).append(" (").append(row.pkg).append(") — ")
                        .append(PhoneFmt.size(row.lastUsed)).append('\n');
            }

        }
        long own = dirSize(context.getCacheDir()) + dirSize(context.getExternalCacheDir());
        out.append("\nКэш самого ConexyAI: ").append(PhoneFmt.size(own)).append('\n');
        return ToolResult.ok(out.toString());
    }

    private static void appendMediaSize(Context context, StringBuilder out, String title, Uri uri, String permission) {
        if (!"granted".equals(PhonePermissions.state(context, permission))) {
            out.append("  ").append(title).append(": нет доступа (").append(permission).append(")\n");
            return;
        }
        long sum = 0;
        int count = 0;
        try (Cursor cursor = context.getContentResolver().query(uri, new String[] { MediaStore.MediaColumns.SIZE },
                null, null, null)) {
            if (cursor != null) {
                while (cursor.moveToNext()) {
                    sum += cursor.getLong(0);
                    count++;
                }
            }
            out.append("  ").append(title).append(": ").append(count).append(" файлов, ").append(PhoneFmt.size(sum)).append('\n');
        } catch (Throwable error) {
            out.append("  ").append(title).append(": не удалось прочитать\n");
        }
    }

    private static long dirSize(File dir) {
        if (dir == null || !dir.exists()) {
            return 0;
        }
        if (dir.isFile()) {
            return dir.length();
        }
        long total = 0;
        File[] children = dir.listFiles();
        if (children != null) {
            for (File child : children) {
                total += dirSize(child);
            }
        }
        return total;
    }

    private static void deleteContents(File dir) {
        if (dir == null) {
            return;
        }
        File[] children = dir.listFiles();
        if (children == null) {
            return;
        }
        for (File child : children) {
            if (child.isDirectory()) {
                deleteContents(child);
            }
            //noinspection ResultOfMethodCallIgnored
            child.delete();
        }
    }

    // ---------------------------------------------------------------- настройки / запуск / url

    static ToolResult openSettings(Context context, JSONObject args) {
        String screen = args.optString("screen", "");
        String pkg = PhoneFmt.str(args, "package");
        if (pkg != null && !validPackage(pkg)) {
            return ToolResult.err("Некорректное имя пакета: " + pkg);
        }
        String target = pkg != null ? pkg : context.getPackageName();
        Intent intent;
        switch (screen) {
            case "app_details":
            case "storage":
                // Прямой ссылки на «Хранилище» приложения нет: открываем сведения, откуда в один тап — хранилище.
                if ("storage".equals(screen) && pkg == null) {
                    intent = new Intent(Settings.ACTION_INTERNAL_STORAGE_SETTINGS);
                } else {
                    intent = appDetailsIntent(target);
                }
                break;
            case "battery":
                intent = new Intent(Intent.ACTION_POWER_USAGE_SUMMARY);
                break;
            case "battery_optimization":
                intent = new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS);
                break;
            case "usage_access":
                intent = new Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS);
                break;
            case "accessibility":
                intent = new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS);
                break;
            case "notifications":
                intent = new Intent("android.settings.NOTIFICATION_SETTINGS");
                break;
            case "app_notifications":
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    intent = new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, target);
                } else {
                    intent = appDetailsIntent(target);
                }
                break;
            case "wifi":
                intent = new Intent(Settings.ACTION_WIFI_SETTINGS);
                break;
            case "bluetooth":
                intent = new Intent(Settings.ACTION_BLUETOOTH_SETTINGS);
                break;
            case "display":
                intent = new Intent(Settings.ACTION_DISPLAY_SETTINGS);
                break;
            case "sound":
                intent = new Intent(Settings.ACTION_SOUND_SETTINGS);
                break;
            case "location":
                intent = new Intent(Settings.ACTION_LOCATION_SOURCE_SETTINGS);
                break;
            case "developer":
                intent = new Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS);
                break;
            case "all_files":
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    intent = new Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION);
                } else {
                    intent = appDetailsIntent(target);
                }
                break;
            case "default_apps":
                intent = new Intent(Settings.ACTION_MANAGE_DEFAULT_APPS_SETTINGS);
                break;
            case "date":
                intent = new Intent(Settings.ACTION_DATE_SETTINGS);
                break;
            case "home":
                intent = new Intent(Settings.ACTION_HOME_SETTINGS);
                break;
            case "apps":
                intent = new Intent(Settings.ACTION_MANAGE_APPLICATIONS_SETTINGS);
                break;
            default:
                return ToolResult.err("Неизвестный screen: «" + screen + "». Допустимо: app_details, battery, "
                        + "battery_optimization, storage, usage_access, accessibility, notifications, "
                        + "app_notifications, wifi, bluetooth, display, sound, location, developer, all_files, "
                        + "default_apps, date, home, apps");
        }
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        try {
            context.startActivity(intent);
            return ToolResult.ok("Открыт экран настроек «" + screen + "»"
                    + ("storage".equals(screen) && pkg != null ? " (сведения о приложении: хранилище — пункт «Хранилище и кэш»)" : "")
                    + "." + backgroundStartWarning(context));
        } catch (ActivityNotFoundException | SecurityException error) {
            try {
                Intent fallback = new Intent(Settings.ACTION_SETTINGS);
                fallback.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                context.startActivity(fallback);
                return ToolResult.ok("Нужного экрана «" + screen + "» на этом устройстве нет — открыты общие настройки");
            } catch (Throwable inner) {
                return ToolResult.err("Не удалось открыть настройки");
            }
        }
    }

    static ToolResult launchApp(Context context, JSONObject args) {
        String pkg = PhoneFmt.str(args, "package");
        if (!validPackage(pkg)) {
            return ToolResult.err("Нужен корректный package (например com.android.chrome)");
        }
        Intent intent = context.getPackageManager().getLaunchIntentForPackage(pkg);
        if (intent == null) {
            return ToolResult.err("Приложение " + pkg + " не установлено или не имеет экрана запуска");
        }
        return startActivityResult(context, intent, "Запрошен запуск " + pkg
                + ". Если приложение было не на экране, Android мог заблокировать запуск — проверьте phone_screen_read.");
    }

    static ToolResult openUrl(Context context, JSONObject args) {
        String url = PhoneFmt.str(args, "url");
        if (url == null || url.trim().isEmpty()) {
            return ToolResult.err("Не передан url");
        }
        Uri uri = Uri.parse(url.trim());
        String scheme = uri.getScheme() == null ? "" : uri.getScheme().toLowerCase(Locale.ROOT);
        if (!(scheme.equals("http") || scheme.equals("https") || scheme.equals("mailto") || scheme.equals("tel")
                || scheme.equals("geo"))) {
            return ToolResult.err("Разрешены только http, https, mailto, tel, geo");
        }
        return startActivityResult(context, new Intent(Intent.ACTION_VIEW, uri), "Открыто: " + uri);
    }

    // ---------------------------------------------------------------- удаление / кэш / остановка

    static ToolResult uninstallApp(Context context, JSONObject args) {
        String pkg = PhoneFmt.str(args, "package");
        if (!validPackage(pkg)) {
            return ToolResult.err("Нужен корректный package");
        }
        if (pkg.equals(context.getPackageName())) {
            return ToolResult.err("Удалять само приложение ConexyAI через агента нельзя");
        }
        ApplicationInfo info = appInfo(context, pkg);
        if (info == null) {
            return ToolResult.err("Приложение не установлено: " + pkg);
        }
        if (isProtectedPackage(pkg) || (isSystem(info) && (info.flags & ApplicationInfo.FLAG_UPDATED_SYSTEM_APP) == 0)) {
            return ToolResult.err("Системное приложение " + pkg + " удалить нельзя");
        }
        String name = label(context, info);
        Intent intent = new Intent(Intent.ACTION_DELETE, Uri.parse("package:" + pkg));
        intent.putExtra(Intent.EXTRA_RETURN_RESULT, false);
        ToolResult started = startActivityResult(context, intent, "");
        if (started.isError) {
            return started;
        }
        for (int i = 0; i < 40; i++) {
            sleep(1000);
            if (appInfo(context, pkg) == null) {
                return ToolResult.ok("Приложение «" + name + "» (" + pkg + ") удалено.");
            }
        }
        return ToolResult.ok("Системный диалог удаления «" + name + "» (" + pkg + ") показан, но за 40 с приложение "
                + "не удалено: пользователь не подтвердил или отказался. Решение принимает пользователь.");
    }

    static ToolResult clearCache(Context context, JSONObject args) {
        String pkg = PhoneFmt.str(args, "package");
        if (pkg == null || pkg.isEmpty() || pkg.equals(context.getPackageName())) {
            long before = dirSize(context.getCacheDir()) + dirSize(context.getExternalCacheDir());
            deleteContents(context.getCacheDir());
            deleteContents(context.getExternalCacheDir());
            long after = dirSize(context.getCacheDir()) + dirSize(context.getExternalCacheDir());
            return ToolResult.ok("Кэш ConexyAI очищен: освобождено " + PhoneFmt.size(Math.max(0, before - after))
                    + " (было " + PhoneFmt.size(before) + ").");
        }
        if (!validPackage(pkg)) {
            return ToolResult.err("Некорректное имя пакета: " + pkg);
        }
        ApplicationInfo info = appInfo(context, pkg);
        if (info == null) {
            return ToolResult.err("Приложение не установлено: " + pkg);
        }
        String name = label(context, info);
        long before = cacheBytes(context, pkg);
        ToolResult opened = startActivityResult(context, appDetailsIntent(pkg), "");
        if (opened.isError) {
            return opened;
        }
        sleep(1500);
        ConexyAccessibilityService service = ConexyAccessibilityService.instance();
        String manual = "Кэш чужого приложения Android позволяет очистить только пользователю: «Хранилище» → «Очистить кэш».";
        if (service == null) {
            return ToolResult.ok("Кэш «" + name + "» НЕ очищен. Открыт экран «Сведения о приложении». " + manual
                    + " Чтобы агент нажал сам, включите службу доступности ConexyAI (phone_request_permission accessibility).");
        }
        String clicked = service.clickAnyText(CLEAR_CACHE_TEXTS, 2000);
        if (clicked == null) {
            String storage = service.clickAnyText(STORAGE_TEXTS, 3500);
            if (storage == null) {
                return ToolResult.ok("Кэш «" + name + "» НЕ очищен: на экране сведений не нашёл пункта «Хранилище». "
                        + "Экран оставлен открытым — " + manual);
            }
            sleep(1200);
            clicked = service.clickAnyText(CLEAR_CACHE_TEXTS, 4000);
        }
        if (clicked == null) {
            return ToolResult.ok("Кэш «" + name + "» НЕ очищен: открыл «Хранилище», но кнопку «Очистить кэш» не нашёл "
                    + "(возможно, кэш уже пуст). Экран оставлен открытым.");
        }
        sleep(1500);
        long after = cacheBytes(context, pkg);
        returnToApp(context, service);
        if (before >= 0 && after >= 0) {
            return ToolResult.ok("Нажал «" + clicked + "» для «" + name + "». Кэш: было " + PhoneFmt.size(before)
                    + ", стало " + PhoneFmt.size(after) + (after < before ? " — очищено." : " — не изменился."));
        }
        return ToolResult.ok("Нажал «" + clicked + "» для «" + name + "». Проверить результат нельзя "
                + "(нет доступа usage_access), считайте очистку выполненной по клику.");
    }

    static ToolResult forceStopApp(Context context, JSONObject args) {
        String pkg = PhoneFmt.str(args, "package");
        if (!validPackage(pkg)) {
            return ToolResult.err("Нужен корректный package");
        }
        if (pkg.equals(context.getPackageName())) {
            return ToolResult.err("Останавливать само приложение ConexyAI через агента нельзя");
        }
        if (isProtectedPackage(pkg)) {
            return ToolResult.err("Системный пакет " + pkg + " останавливать нельзя");
        }
        ApplicationInfo info = appInfo(context, pkg);
        if (info == null) {
            return ToolResult.err("Приложение не установлено: " + pkg);
        }
        String name = label(context, info);
        boolean wasStopped = (info.flags & ApplicationInfo.FLAG_STOPPED) != 0;
        try {
            ActivityManager am = (ActivityManager) context.getSystemService(Context.ACTIVITY_SERVICE);
            if (am != null) {
                am.killBackgroundProcesses(pkg);
            }
        } catch (Throwable ignored) {
            // не критично: основной путь — экран сведений
        }
        ToolResult opened = startActivityResult(context, appDetailsIntent(pkg), "");
        if (opened.isError) {
            return opened;
        }
        sleep(1500);
        ConexyAccessibilityService service = ConexyAccessibilityService.instance();
        if (service == null) {
            return ToolResult.ok("«" + name + "» принудительно НЕ остановлено (фоновые процессы попросил завершить). "
                    + "Открыт экран «Сведения о приложении»: нажмите «Остановить». "
                    + "Чтобы агент нажал сам, включите службу доступности ConexyAI (phone_request_permission accessibility).");
        }
        String clicked = service.clickAnyText(FORCE_STOP_TEXTS, 3500);
        if (clicked == null) {
            return ToolResult.ok("«" + name + "»: кнопка «Остановить» не найдена или неактивна"
                    + (wasStopped ? " (приложение уже было остановлено)" : "") + ". Экран оставлен открытым.");
        }
        sleep(800);
        service.clickAnyText(CONFIRM_TEXTS, 3000);
        sleep(1000);
        ApplicationInfo after = appInfo(context, pkg);
        boolean stopped = after != null && (after.flags & ApplicationInfo.FLAG_STOPPED) != 0;
        returnToApp(context, service);
        if (stopped) {
            return ToolResult.ok("«" + name + "» (" + pkg + ") принудительно остановлено (система подтверждает состояние «остановлено»).");
        }
        return ToolResult.ok("Нажал «" + clicked + "» для «" + name + "», но система не показывает состояние «остановлено» — "
                + "возможно, диалог подтверждения не был нажат. Проверьте phone_screen_read.");
    }

    /** Возвращает пользователя в ConexyAI кнопкой «назад» (до 3 раз), не запуская активности из фона. */
    private static void returnToApp(Context context, ConexyAccessibilityService service) {
        for (int i = 0; i < 3; i++) {
            try {
                android.view.accessibility.AccessibilityNodeInfo root = service.getRootInActiveWindow();
                if (root != null && context.getPackageName().contentEquals(root.getPackageName())) {
                    return;
                }
            } catch (Throwable ignored) {
                // продолжаем нажимать «назад»
            }
            service.globalAction("back");
            sleep(700);
        }
    }

}
