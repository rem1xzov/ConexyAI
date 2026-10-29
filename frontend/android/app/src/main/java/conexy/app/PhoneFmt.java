package conexy.app;

import org.json.JSONObject;

import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

/** Форматирование чисел и дат для текстовых ответов инструментов. */
final class PhoneFmt {
    private PhoneFmt() {}

    static String size(long bytes) {
        if (bytes < 0) {
            return "?";
        }
        if (bytes < 1024) {
            return bytes + " Б";
        }
        double value = bytes;
        String[] units = { "КБ", "МБ", "ГБ", "ТБ" };
        int unit = -1;
        while (value >= 1024 && unit < units.length - 1) {
            value /= 1024;
            unit++;
        }
        return String.format(Locale.US, value >= 100 ? "%.0f %s" : "%.1f %s", value, units[unit]);
    }

    static String time(long millis) {
        if (millis <= 0) {
            return "—";
        }
        return new SimpleDateFormat("yyyy-MM-dd HH:mm", Locale.US).format(new Date(millis));
    }

    static String duration(long millis) {
        long minutes = millis / 60_000;
        if (minutes < 1) {
            return (millis / 1000) + " с";
        }
        long hours = minutes / 60;
        return hours > 0 ? hours + " ч " + (minutes % 60) + " мин" : minutes + " мин";
    }

    static int clamp(int value, int min, int max) {
        return Math.max(min, Math.min(max, value));
    }

    static String str(JSONObject args, String key) {
        if (args == null || !args.has(key) || args.isNull(key)) {
            return null;
        }
        String value = args.optString(key, null);
        return value == null ? null : value;
    }
}
