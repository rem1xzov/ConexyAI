package conexy.app;

import android.Manifest;
import android.app.Activity;
import android.app.AppOpsManager;

import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.os.PowerManager;
import android.provider.Settings;
import android.text.TextUtils;

import androidx.core.app.ActivityCompat;
import androidx.core.app.NotificationManagerCompat;
import androidx.core.content.ContextCompat;

import org.json.JSONException;
import org.json.JSONObject;

/** Состояния разрешений: granted / denied / prompt / unavailable. */
final class PhonePermissions {
    static final String[] RUNTIME_IDS = {
            "camera", "microphone", "photos", "videos", "audio", "notifications", "contacts", "location"
    };
    static final String[] SPECIAL_IDS = {
            "usage_access", "all_files", "accessibility", "battery_optimization",
            "install_packages", "overlay", "write_settings"
    };

    private static final String PREFS = "conexy_phone_permissions";

    private PhonePermissions() {}

    static boolean isKnown(String id) {
        for (String known : RUNTIME_IDS) {
            if (known.equals(id)) {
                return true;
            }
        }
        for (String known : SPECIAL_IDS) {
            if (known.equals(id)) {
                return true;
            }
        }
        return false;
    }

    /** Список системных runtime-разрешений для id или null, если это спец-доступ. */
    static String[] runtimePermissions(String id) {
        switch (id) {
            case "camera":
                return new String[] { Manifest.permission.CAMERA };
            case "microphone":
                return new String[] { Manifest.permission.RECORD_AUDIO };
            case "photos":
                if (Build.VERSION.SDK_INT >= 34) {
                    return new String[] { Manifest.permission.READ_MEDIA_IMAGES,
                            Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED };
                }
                if (Build.VERSION.SDK_INT >= 33) {
                    return new String[] { Manifest.permission.READ_MEDIA_IMAGES };
                }
                return new String[] { Manifest.permission.READ_EXTERNAL_STORAGE };
            case "videos":
                if (Build.VERSION.SDK_INT >= 34) {
                    return new String[] { Manifest.permission.READ_MEDIA_VIDEO,
                            Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED };
                }
                if (Build.VERSION.SDK_INT >= 33) {
                    return new String[] { Manifest.permission.READ_MEDIA_VIDEO };
                }
                return new String[] { Manifest.permission.READ_EXTERNAL_STORAGE };
            case "audio":
                if (Build.VERSION.SDK_INT >= 33) {
                    return new String[] { Manifest.permission.READ_MEDIA_AUDIO };
                }
                return new String[] { Manifest.permission.READ_EXTERNAL_STORAGE };
            case "notifications":
                if (Build.VERSION.SDK_INT >= 33) {
                    return new String[] { Manifest.permission.POST_NOTIFICATIONS };
                }
                return null;
            case "contacts":
                return new String[] { Manifest.permission.READ_CONTACTS };
            case "location":
                return new String[] { Manifest.permission.ACCESS_FINE_LOCATION,
                        Manifest.permission.ACCESS_COARSE_LOCATION };
            case "all_files":
                if (Build.VERSION.SDK_INT < 30) {
                    return new String[] { Manifest.permission.READ_EXTERNAL_STORAGE,
                            Manifest.permission.WRITE_EXTERNAL_STORAGE };
                }
                return null;
            default:
                return null;
        }
    }

    private static boolean has(Context context, String permission) {
        return ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED;
    }

    /** Достаточно ли выданных runtime-разрешений (для фото/видео на 34+ хватает и частичного доступа). */
    private static boolean runtimeGranted(Context context, String id, String[] perms) {
        switch (id) {
            case "photos":
                if (Build.VERSION.SDK_INT >= 34) {
                    return has(context, Manifest.permission.READ_MEDIA_IMAGES)
                            || has(context, Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED);
                }
                return has(context, perms[0]);
            case "videos":
                if (Build.VERSION.SDK_INT >= 34) {
                    return has(context, Manifest.permission.READ_MEDIA_VIDEO)
                            || has(context, Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED);
                }
                return has(context, perms[0]);
            case "location":
                return has(context, Manifest.permission.ACCESS_FINE_LOCATION)
                        || has(context, Manifest.permission.ACCESS_COARSE_LOCATION);
            default:
                for (String permission : perms) {
                    if (!has(context, permission)) {
                        return false;
                    }
                }
                return true;
        }
    }

    static void markAsked(Context context, String id) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean("asked_" + id, true).apply();
    }

    static String state(Context context, String id) {
        try {
            return computeState(context, id);
        } catch (Throwable error) {
            return "unavailable";
        }
    }

    private static String computeState(Context context, String id) {
        String[] perms = runtimePermissions(id);
        if (perms != null) {
            if (runtimeGranted(context, id, perms)) {
                return "granted";
            }
            SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
            boolean asked = prefs.getBoolean("asked_" + id, false);
            if (!asked) {
                return "prompt";
            }
            if (context instanceof Activity) {
                for (String permission : perms) {
                    if (ActivityCompat.shouldShowRequestPermissionRationale((Activity) context, permission)) {
                        return "prompt";
                    }
                }
            }
            return "denied";
        }
        switch (id) {
            case "notifications":
                return NotificationManagerCompat.from(context).areNotificationsEnabled() ? "granted" : "prompt";
            case "usage_access":
                return usageAccessGranted(context) ? "granted" : "prompt";
            case "all_files":
                return Environment.isExternalStorageManager() ? "granted" : "prompt";
            case "accessibility":
                return accessibilityEnabled(context) ? "granted" : "prompt";
            case "battery_optimization": {
                PowerManager pm = (PowerManager) context.getSystemService(Context.POWER_SERVICE);
                if (pm == null) {
                    return "unavailable";
                }
                return pm.isIgnoringBatteryOptimizations(context.getPackageName()) ? "granted" : "prompt";
            }
            case "install_packages":
                if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
                    return "granted";
                }
                return context.getPackageManager().canRequestPackageInstalls() ? "granted" : "prompt";
            case "overlay":
                return Settings.canDrawOverlays(context) ? "granted" : "prompt";
            case "write_settings":
                // MIN_PERMISSIONS: WRITE_SETTINGS больше не объявлено в манифесте (ни один инструмент его не использует).
                return "unavailable";
            default:
                return "unavailable";
        }
    }

    static boolean usageAccessGranted(Context context) {
        try {
            AppOpsManager ops = (AppOpsManager) context.getSystemService(Context.APP_OPS_SERVICE);
            if (ops == null) {
                return false;
            }
            int mode;
            if (Build.VERSION.SDK_INT >= 29) {
                mode = ops.unsafeCheckOpNoThrow(AppOpsManager.OPSTR_GET_USAGE_STATS,
                        android.os.Process.myUid(), context.getPackageName());
            } else {
                mode = ops.checkOpNoThrow(AppOpsManager.OPSTR_GET_USAGE_STATS,
                        android.os.Process.myUid(), context.getPackageName());
            }
            return mode == AppOpsManager.MODE_ALLOWED;
        } catch (Throwable error) {
            return false;
        }
    }

    static boolean accessibilityEnabled(Context context) {
        if (ConexyAccessibilityService.instance() != null) {
            return true;
        }
        try {
            String enabled = Settings.Secure.getString(
                    context.getContentResolver(), Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES);
            if (TextUtils.isEmpty(enabled)) {
                return false;
            }
            String component = context.getPackageName() + "/" + ConexyAccessibilityService.class.getName();
            String shortComponent = context.getPackageName() + "/." + ConexyAccessibilityService.class.getSimpleName();
            for (String item : enabled.split(":")) {
                if (item.equalsIgnoreCase(component) || item.equalsIgnoreCase(shortComponent)) {
                    return true;
                }
            }
        } catch (Throwable ignored) {
            // нет доступа к настройкам — считаем, что служба выключена
        }
        return false;
    }

    static JSONObject all(Context context) {
        JSONObject result = new JSONObject();
        try {
            for (String id : RUNTIME_IDS) {
                result.put(id, state(context, id));
            }
            for (String id : SPECIAL_IDS) {
                result.put(id, state(context, id));
            }
        } catch (JSONException ignored) {
            // ключи — строки, исключение невозможно
        }
        return result;
    }

    /** Экран настроек для спец-доступа; null, если это runtime-разрешение. */
    static Intent settingsIntent(Context context, String id) {
        String pkg = context.getPackageName();
        Uri pkgUri = Uri.parse("package:" + pkg);
        Intent intent;
        switch (id) {
            case "usage_access":
                intent = new Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS);
                if (Build.VERSION.SDK_INT >= 29) {
                    intent.setData(pkgUri);
                }
                break;
            case "all_files":
                if (Build.VERSION.SDK_INT < 30) {
                    return null;
                }
                intent = new Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, pkgUri);
                break;
            case "accessibility":
                intent = new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS);
                break;
            case "battery_optimization":
                intent = new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, pkgUri);
                break;
            case "install_packages":
                intent = new Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, pkgUri);
                break;
            case "overlay":
                intent = new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, pkgUri);
                break;

            case "notifications":
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                    intent = new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                            .putExtra(Settings.EXTRA_APP_PACKAGE, pkg);
                } else {
                    intent = new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, pkgUri);
                }
                break;
            default:
                return null;
        }
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        return intent;
    }

    /** Подсказка, если спец-доступ не выдан после возврата из настроек. */
    static String hintIfStillDenied(String id) {
        if ("accessibility".equals(id)) {
            return "Если переключатель службы неактивен: Настройки → Приложения → ConexyAI → ⋮ → "
                    + "«Разрешить ограниченные настройки», затем включите «ConexyAI» в «Специальных возможностях».";
        }
        if ("all_files".equals(id)) {
            return "Включите переключатель «Разрешить доступ к управлению всеми файлами» для ConexyAI.";
        }
        if ("usage_access".equals(id)) {
            return "Включите переключатель «Разрешить доступ к данным об использовании» для ConexyAI.";
        }
        return "";
    }

}
