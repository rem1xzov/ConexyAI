package conexy.app;

import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.os.Handler;
import android.os.Looper;
import android.webkit.MimeTypeMap;

import androidx.core.content.FileProvider;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.RandomAccessFile;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;

import java.util.Comparator;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.regex.Pattern;

/** Файловые инструменты: безопасные пути, чтение/запись/поиск, шаринг, буфер обмена. */
final class PhoneToolsFiles {
    private static final int READ_DEFAULT = 20_000;
    private static final int READ_MAX = 100_000;
    private static final long WRITE_MAX_CHARS = 2_000_000L;
    /** Максимальный размер файла после записи/дописывания. */
    private static final long WRITE_FILE_MAX_BYTES = 20L * 1024 * 1024;
    /** Максимальный объём одного копирования/перемещения через copy-fallback. */
    private static final long COPY_MAX_BYTES = 4L * 1024 * 1024 * 1024;
    private static final long CONTENT_SEARCH_MAX_FILE = 2L * 1024 * 1024;
    private static final String[] PROTECTED_TOP = {
            "Android", "DCIM", "Pictures", "Download", "Documents", "Music", "Movies", "Alarms", "Notifications",
            "Podcasts", "Ringtones", "Recordings"
    };
    private static final List<String> BINARY_EXT = Arrays.asList(
            "jpg", "jpeg", "png", "gif", "webp", "heic", "heif", "bmp", "mp4", "mkv", "avi", "mov", "3gp", "webm",
            "mp3", "m4a", "aac", "ogg", "wav", "flac", "zip", "rar", "7z", "gz", "tar", "apk", "aab", "pdf", "doc",
            "docx", "xls", "xlsx", "ppt", "pptx", "db", "sqlite", "so", "dex", "jar", "class", "bin", "iso", "exe");

    private PhoneToolsFiles() {}

    // ---------------------------------------------------------------- корни и пути

    static boolean hasAllFiles() {
        return Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && Environment.isExternalStorageManager();
    }

    private static File canon(File file) {
        try {
            return file.getCanonicalFile();
        } catch (IOException error) {
            return file.getAbsoluteFile();
        }
    }

    private static File extRoot() {
        return canon(Environment.getExternalStorageDirectory());
    }

    /** Рабочая папка агента (создаётся при необходимости). */
    static File workspace(Context context) {
        File dir;
        if (hasAllFiles()) {
            dir = new File(Environment.getExternalStorageDirectory(), "Documents/ConexyAI");
        } else {
            File base = context.getExternalFilesDir(null);
            dir = new File(base != null ? base : context.getFilesDir(), "workspace");
        }
        //noinspection ResultOfMethodCallIgnored
        dir.mkdirs();
        return canon(dir);
    }

    /** Корень, выше которого файловые инструменты не поднимаются. */
    static File root(Context context) {
        return hasAllFiles() ? extRoot() : workspace(context);
    }

    private static boolean isUnder(File child, File parent) {
        String c = child.getPath();
        String p = parent.getPath();
        return c.equals(p) || c.startsWith(p.endsWith(File.separator) ? p : p + File.separator);
    }

    private static List<File> privateRoots(Context context) {
        List<File> roots = new ArrayList<>();
        roots.add(canon(new File(context.getFilesDir(), "picked")));
        roots.add(canon(new File(context.getFilesDir(), "widgets")));
        roots.add(canon(context.getCacheDir()));
        return roots;
    }

    /** Преобразует путь в канонический файл внутри разрешённых корней или бросает IOException. */
    static File resolve(Context context, String path, boolean allowAppPrivate) throws IOException {
        String p = path == null ? "" : path.trim();
        if (p.indexOf('\u0000') >= 0) {
            throw new IOException("Недопустимый путь");
        }
        if (p.startsWith("file://")) {
            p = Uri.parse(p).getPath();
            if (p == null) {
                throw new IOException("Недопустимый путь");
            }
        }
        File workspace = workspace(context);
        File file;
        if (p.isEmpty()) {
            file = workspace;
        } else if (p.startsWith("/")) {
            file = new File(p);
        } else {
            file = new File(workspace, p);
        }
        File canonical = file.getCanonicalFile();
        File root = root(context);

        if (isUnder(canonical, root)) {
            // Проверка нужна всегда: рабочая папка без all_files лежит в Android/data/<свой пакет>.
            checkForbidden(context, canonical);
            return canonical;
        }
        if (allowAppPrivate) {
            for (File privateRoot : privateRoots(context)) {
                if (isUnder(canonical, privateRoot)) {
                    return canonical;
                }
            }
        }
        throw new IOException("Путь вне разрешённой области: " + p
                + ". Доступно: " + root.getPath()
                + (hasAllFiles() ? "" : " (чтобы работать со всей памятью, выдайте доступ «ко всем файлам»: "
                        + "phone_request_permission all_files)"));
    }

    private static void checkForbidden(Context context, File canonical) throws IOException {
        File ext = extRoot();
        if (!isUnder(canonical, ext)) {
            return;
        }
        String rel = canonical.getPath().substring(ext.getPath().length());
        while (rel.startsWith("/")) {
            rel = rel.substring(1);
        }
        String[] segments = rel.split("/");
        // Регистр не учитываем: на FUSE/sdcardfs «android/DATA» — тот же каталог.
        if (segments.length >= 2 && "Android".equalsIgnoreCase(segments[0])
                && ("data".equalsIgnoreCase(segments[1]) || "obb".equalsIgnoreCase(segments[1])
                        || "obj".equalsIgnoreCase(segments[1]))) {
            boolean own = segments.length >= 3 && context.getPackageName().equals(segments[2]);
            if (!own) {
                throw new IOException("Каталоги других приложений (Android/data, Android/obb) недоступны");
            }
        }
    }

    private static boolean isProtectedPath(Context context, File file) {
        File ext = extRoot();
        File workspace = workspace(context);
        // Сам корень, рабочая папка и любой её предок (в том числе Android/data/<пакет>).
        if (file.equals(ext) || file.equals(root(context)) || isUnder(workspace, file)) {
            return true;
        }
        if (file.getParentFile() != null && file.getParentFile().equals(ext)) {
            for (String name : PROTECTED_TOP) {
                if (name.equalsIgnoreCase(file.getName())) {
                    return true;
                }
            }
        }
        return false;
    }

    private static boolean isSymlink(File file) {
        try {
            File parent = file.getParentFile();
            File base = parent == null ? file : new File(parent.getCanonicalFile(), file.getName());
            return !base.getCanonicalFile().equals(base.getAbsoluteFile());
        } catch (IOException error) {
            return true;
        }
    }

    private static ToolResult denied(IOException error) {
        return ToolResult.err(error.getMessage());
    }

    // ---------------------------------------------------------------- list

    static ToolResult list(Context context, JSONObject args) {
        try {
            File dir = resolve(context, PhoneFmt.str(args, "path"), false);
            boolean hidden = args.optBoolean("show_hidden", false);
            if (!dir.exists()) {
                return ToolResult.err("Папка не существует: " + dir.getPath());
            }
            if (!dir.isDirectory()) {
                return ToolResult.err("Это файл, а не папка: " + dir.getPath() + " (" + PhoneFmt.size(dir.length()) + ")");
            }
            File[] children = dir.listFiles();
            if (children == null) {
                return ToolResult.err("Нет доступа к папке: " + dir.getPath());
            }
            Arrays.sort(children, new Comparator<File>() {
                @Override
                public int compare(File a, File b) {
                    if (a.isDirectory() != b.isDirectory()) {
                        return a.isDirectory() ? -1 : 1;
                    }
                    return a.getName().compareToIgnoreCase(b.getName());
                }
            });
            StringBuilder out = new StringBuilder();
            int shown = 0;
            int total = 0;
            for (File child : children) {
                if (!hidden && child.getName().startsWith(".")) {
                    continue;
                }
                total++;
                if (shown >= 500) {
                    continue;
                }
                shown++;
                if (child.isDirectory()) {
                    out.append("d  ").append(child.getName()).append("/  ").append(PhoneFmt.time(child.lastModified()));
                } else {
                    out.append("f  ").append(child.getName()).append("  ").append(PhoneFmt.size(child.length()))
                            .append("  ").append(PhoneFmt.time(child.lastModified()));
                }
                out.append('\n');
            }
            String header = "Папка: " + dir.getPath() + " (элементов: " + total
                    + (total > shown ? ", показано " + shown : "") + ")\n";
            return ToolResult.ok(header + (total == 0 ? "(пусто)\n" : out.toString()));
        } catch (IOException error) {
            return denied(error);
        }
    }

    // ---------------------------------------------------------------- read

    static ToolResult read(Context context, JSONObject args) {
        String path = PhoneFmt.str(args, "path");
        if (path == null || path.trim().isEmpty()) {
            return ToolResult.err("Не передан path");
        }
        try {
            File file = resolve(context, path, true);
            if (!file.exists()) {
                return ToolResult.err("Файл не существует: " + file.getPath());
            }
            if (file.isDirectory()) {
                return ToolResult.err("Это папка: используйте phone_file_list");
            }
            long size = file.length();
            long offset = Math.max(0, args.optLong("offset", 0));
            int limit = PhoneFmt.clamp(args.optInt("limit", READ_DEFAULT), 1, READ_MAX);

            String ext = extension(file.getName());
            if (BINARY_EXT.contains(ext)) {
                return ToolResult.err("Файл «" + file.getName() + "» бинарный (" + PhoneFmt.size(size) + "). "
                        + "Картинки смотрите через phone_image_view, остальные файлы можно передать через phone_share "
                        + "или скопировать phone_file_copy.");
            }
            if (offset >= size && size > 0) {
                return ToolResult.err("offset " + offset + " за концом файла (размер " + size + " байт)");
            }
            byte[] buffer = new byte[limit];
            int read;
            try (RandomAccessFile raf = new RandomAccessFile(file, "r")) {
                raf.seek(offset);
                read = raf.read(buffer, 0, limit);
            }
            if (read < 0) {
                read = 0;
            }
            if (looksBinary(buffer, read)) {
                return ToolResult.err("Файл «" + file.getName() + "» похож на бинарный. Для просмотра картинок "
                        + "используйте phone_image_view, для передачи — phone_share.");
            }
            boolean eof = offset + read >= size;
            int usable = eof ? read : trimIncompleteUtf8(buffer, read);
            String text = new String(buffer, 0, usable, StandardCharsets.UTF_8);
            long next = offset + usable;
            String header = "[" + file.getPath() + " | размер " + size + " байт | байты " + offset + ".." + next
                    + (eof ? " | конец файла" : " | продолжение: offset=" + next) + "]\n";
            return ToolResult.ok(header + text);
        } catch (IOException error) {
            return denied(error);
        }
    }

    private static boolean looksBinary(byte[] data, int length) {
        int checked = Math.min(length, 8000);
        int suspicious = 0;
        for (int i = 0; i < checked; i++) {
            int b = data[i] & 0xFF;
            if (b == 0) {
                return true;
            }
            if (b < 9 || (b > 13 && b < 32)) {
                suspicious++;
            }
        }
        return checked > 0 && suspicious * 20 > checked;
    }

    private static int trimIncompleteUtf8(byte[] bytes, int length) {
        int i = length - 1;
        int back = 0;
        while (i >= 0 && back < 4 && (bytes[i] & 0xC0) == 0x80) {
            i--;
            back++;
        }
        if (i < 0) {
            return length;
        }
        int lead = bytes[i] & 0xFF;
        int need = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
        return (length - i) < need ? i : length;
    }

    private static String extension(String name) {
        int dot = name.lastIndexOf('.');
        return dot < 0 ? "" : name.substring(dot + 1).toLowerCase(Locale.ROOT);
    }

    // ---------------------------------------------------------------- search

    static ToolResult search(Context context, JSONObject args) {
        try {
            File start = resolve(context, PhoneFmt.str(args, "path"), false);
            if (!start.exists()) {
                return ToolResult.err("Путь не существует: " + start.getPath());
            }
            String pattern = PhoneFmt.str(args, "pattern");
            String contains = PhoneFmt.str(args, "contains");
            if ((pattern == null || pattern.isEmpty()) && (contains == null || contains.isEmpty())) {
                return ToolResult.err("Укажите pattern (имя/маска) и/или contains (текст внутри файлов)");
            }
            int limit = PhoneFmt.clamp(args.optInt("limit", 50), 1, 200);
            Pattern namePattern = null;
            if (pattern != null && !pattern.isEmpty()) {
                namePattern = buildNamePattern(pattern);
            }
            String needle = contains == null || contains.isEmpty() ? null : contains.toLowerCase(Locale.ROOT);

            long deadline = System.currentTimeMillis() + 20_000;
            int visited = 0;
            boolean truncated = false;
            List<String> results = new ArrayList<>();
            ArrayDeque<File> stack = new ArrayDeque<>();
            stack.push(start);
            File ext = extRoot();
            while (!stack.isEmpty()) {
                if (System.currentTimeMillis() > deadline || visited > 30_000) {
                    truncated = true;
                    break;
                }
                File current = stack.pop();
                File[] children = current.isDirectory() ? current.listFiles() : new File[] { current };
                if (children == null) {
                    continue;
                }
                for (File child : children) {
                    visited++;
                    String name = child.getName();
                    if (name.startsWith(".") && child != start) {
                        continue;
                    }
                    if (child != start && isSymlink(child)) {
                        continue;
                    }
                    if (child.isDirectory()) {
                        if (child.getParentFile() != null && child.getParentFile().equals(ext)
                                && "Android".equalsIgnoreCase(name)) {
                            continue;
                        }
                        stack.push(child);
                        if (needle == null && namePattern != null && namePattern.matcher(name).find()) {
                            results.add(child.getPath() + "/");
                        }
                    } else {
                        boolean nameOk = namePattern == null || namePattern.matcher(name).find();
                        if (!nameOk) {
                            continue;
                        }
                        if (needle == null) {
                            results.add(child.getPath() + "  " + PhoneFmt.size(child.length()));
                        } else {
                            String hit = grepFile(child, needle);
                            if (hit != null) {
                                results.add(child.getPath() + ":" + hit);
                            }
                        }
                    }
                    if (results.size() >= limit) {
                        truncated = true;
                        stack.clear();
                        break;
                    }
                }
            }
            if (results.isEmpty()) {
                return ToolResult.ok("Ничего не найдено в " + start.getPath() + (truncated ? " (поиск прерван по лимиту)" : ""));
            }
            StringBuilder out = new StringBuilder("Найдено: " + results.size() + (truncated ? " (возможно, есть ещё)" : "") + "\n");
            for (String line : results) {
                out.append(line).append('\n');
            }
            return ToolResult.ok(out.toString());
        } catch (IOException error) {
            return denied(error);
        }
    }

    private static Pattern buildNamePattern(String pattern) {
        if (pattern.indexOf('*') >= 0 || pattern.indexOf('?') >= 0) {
            StringBuilder regex = new StringBuilder("^");
            for (char c : pattern.toCharArray()) {
                if (c == '*') {
                    regex.append(".*");
                } else if (c == '?') {
                    regex.append('.');
                } else {
                    regex.append(Pattern.quote(String.valueOf(c)));
                }
            }
            regex.append('$');
            return Pattern.compile(regex.toString(), Pattern.CASE_INSENSITIVE);
        }
        return Pattern.compile(Pattern.quote(pattern), Pattern.CASE_INSENSITIVE);
    }

    private static String grepFile(File file, String needleLower) {
        if (file.length() > CONTENT_SEARCH_MAX_FILE || BINARY_EXT.contains(extension(file.getName()))) {
            return null;
        }
        try (BufferedReader reader = new BufferedReader(
                new InputStreamReader(new FileInputStream(file), StandardCharsets.UTF_8))) {
            String line;
            int number = 0;
            while ((line = reader.readLine()) != null && number < 50_000) {
                number++;
                if (line.indexOf('\u0000') >= 0) {
                    return null;
                }
                if (line.toLowerCase(Locale.ROOT).contains(needleLower)) {
                    String snippet = line.trim();
                    if (snippet.length() > 160) {
                        snippet = snippet.substring(0, 160) + "…";
                    }
                    return number + ": " + snippet;
                }
            }
        } catch (IOException ignored) {
            // нечитаемый файл пропускаем
        }
        return null;
    }

    // ---------------------------------------------------------------- write / mkdir

    static ToolResult write(Context context, JSONObject args) {
        String path = PhoneFmt.str(args, "path");
        String content = args.has("content") && !args.isNull("content") ? args.optString("content", "") : null;
        if (path == null || path.trim().isEmpty() || content == null) {
            return ToolResult.err("Нужны path и content");
        }
        if (content.length() > WRITE_MAX_CHARS) {
            return ToolResult.err("Слишком большой content (максимум " + WRITE_MAX_CHARS + " символов); пишите по частям с append=true");
        }
        boolean append = args.optBoolean("append", false);
        try {
            File file = resolve(context, path, false);
            if (file.isDirectory()) {
                return ToolResult.err("Это папка: " + file.getPath());
            }
            if (isProtectedPath(context, file)) {
                return ToolResult.err("Запись в системную/корневую папку запрещена");
            }
            File parent = file.getParentFile();
            if (parent != null && !parent.exists() && !parent.mkdirs()) {
                return ToolResult.err("Не удалось создать папку: " + parent.getPath());
            }
            byte[] bytes = content.getBytes(StandardCharsets.UTF_8);
            long existing = append && file.exists() ? file.length() : 0;
            if (existing + bytes.length > WRITE_FILE_MAX_BYTES) {
                return ToolResult.err("Файл получится больше " + PhoneFmt.size(WRITE_FILE_MAX_BYTES)
                        + " — такой объём через phone_file_write не пишется");
            }
            try (OutputStream out = new FileOutputStream(file, append)) {
                out.write(bytes);
            }
            return ToolResult.ok((append ? "Дописано " : "Записано ") + bytes.length + " байт в " + file.getPath()
                    + " (теперь " + PhoneFmt.size(file.length()) + ")");
        } catch (IOException error) {
            return ToolResult.err(error.getMessage() == null ? "Ошибка записи" : error.getMessage());
        }
    }

    static ToolResult mkdir(Context context, JSONObject args) {
        String path = PhoneFmt.str(args, "path");
        if (path == null || path.trim().isEmpty()) {
            return ToolResult.err("Не передан path");
        }
        try {
            File dir = resolve(context, path, false);
            if (dir.isDirectory()) {
                return ToolResult.ok("Папка уже существует: " + dir.getPath());
            }
            if (dir.exists()) {
                return ToolResult.err("По этому пути уже есть файл: " + dir.getPath());
            }
            if (!dir.mkdirs()) {
                return ToolResult.err("Не удалось создать папку: " + dir.getPath());
            }
            return ToolResult.ok("Папка создана: " + dir.getPath());
        } catch (IOException error) {
            return denied(error);
        }
    }

    // ---------------------------------------------------------------- copy / move / delete

    static ToolResult copy(Context context, JSONObject args) {
        return transfer(context, args, false);
    }

    static ToolResult move(Context context, JSONObject args) {
        return transfer(context, args, true);
    }

    private static ToolResult transfer(Context context, JSONObject args, boolean move) {
        String from = PhoneFmt.str(args, "from");
        String to = PhoneFmt.str(args, "to");
        if (from == null || to == null || from.trim().isEmpty() || to.trim().isEmpty()) {
            return ToolResult.err("Нужны from и to");
        }
        try {
            // Источник при копировании может лежать и в приватной папке «picked» (выбранные фото).
            File src = resolve(context, from, !move);
            File dst = resolve(context, to, false);
            if (!src.exists()) {
                return ToolResult.err("Источник не существует: " + src.getPath());
            }
            if (move && isProtectedPath(context, src)) {
                return ToolResult.err("Перемещение системной/корневой папки запрещено: " + src.getPath());
            }
            if (dst.isDirectory()) {
                dst = new File(dst, src.getName());
            }
            if (dst.exists()) {
                return ToolResult.err("Назначение уже существует: " + dst.getPath() + " — выберите другое имя или удалите его");
            }
            if (src.isDirectory() && isUnder(dst, src)) {
                return ToolResult.err("Нельзя копировать/перемещать папку внутрь самой себя");
            }
            File parent = dst.getParentFile();
            if (parent != null && !parent.exists() && !parent.mkdirs()) {
                return ToolResult.err("Не удалось создать папку: " + parent.getPath());
            }
            if (move && src.renameTo(dst)) {
                return ToolResult.ok("Перемещено: " + src.getPath() + " → " + dst.getPath());
            }
            int[] count = new int[] { 0 };
            copyRecursive(src, dst, count, 0, new long[] { 0 });
            if (move) {
                if (!deleteRecursive(src, new int[] { 0 })) {
                    return ToolResult.err("Скопировано в " + dst.getPath() + ", но исходник удалить не удалось");
                }
                return ToolResult.ok("Перемещено (копирование+удаление): " + src.getPath() + " → " + dst.getPath());
            }
            return ToolResult.ok("Скопировано файлов: " + count[0] + " → " + dst.getPath());
        } catch (IOException error) {
            return ToolResult.err(error.getMessage() == null ? "Ошибка копирования" : error.getMessage());
        }
    }

    private static void copyRecursive(File src, File dst, int[] count, int depth, long[] bytes) throws IOException {
        if (depth > 30) {
            throw new IOException("Слишком глубокая вложенность");
        }
        if (src.isDirectory()) {
            if (!dst.mkdirs() && !dst.isDirectory()) {
                throw new IOException("Не удалось создать папку " + dst.getPath());
            }
            File[] children = src.listFiles();
            if (children != null) {
                for (File child : children) {
                    if (isSymlink(child)) {
                        continue;
                    }
                    copyRecursive(child, new File(dst, child.getName()), count, depth + 1, bytes);
                }
            }
            return;
        }
        try (InputStream in = new FileInputStream(src); OutputStream out = new FileOutputStream(dst)) {
            byte[] buffer = new byte[64 * 1024];
            int n;
            while ((n = in.read(buffer)) > 0) {
                bytes[0] += n;
                if (bytes[0] > COPY_MAX_BYTES) {
                    throw new IOException("Слишком большой объём копирования (больше 4 ГБ)");
                }
                out.write(buffer, 0, n);
            }
        }
        count[0]++;
    }

    static ToolResult delete(Context context, JSONObject args) {
        String path = PhoneFmt.str(args, "path");
        if (path == null || path.trim().isEmpty()) {
            return ToolResult.err("Не передан path");
        }
        boolean recursive = args.optBoolean("recursive", false);
        try {
            File file = resolve(context, path, false);
            if (!file.exists() && !isSymlink(file)) {
                return ToolResult.err("Не существует: " + file.getPath());
            }
            if (isProtectedPath(context, file)) {
                return ToolResult.err("Удаление корневой/системной папки запрещено: " + file.getPath());
            }
            if (file.isDirectory()) {
                String[] children = file.list();
                if (children != null && children.length > 0 && !recursive) {
                    return ToolResult.err("Папка не пуста (" + children.length
                            + " элем.). Для удаления вместе с содержимым передайте recursive=true");
                }
            }
            int[] count = new int[] { 0 };
            if (!deleteRecursive(file, count)) {
                return ToolResult.err("Не удалось удалить полностью: " + file.getPath() + " (удалено элементов: " + count[0] + ")");
            }
            return ToolResult.ok("Удалено: " + file.getPath() + " (элементов: " + count[0] + ")");
        } catch (IOException error) {
            return denied(error);
        }
    }

    /** Удаляет без перехода по симлинкам (ссылка удаляется сама, цель остаётся). */
    private static boolean deleteRecursive(File file, int[] count) {
        if (file.isDirectory() && !isSymlink(file)) {
            File[] children = file.listFiles();
            if (children != null) {
                for (File child : children) {
                    if (!deleteRecursive(child, count)) {
                        return false;
                    }
                }
            }
        }
        boolean ok = file.delete();
        if (ok) {
            count[0]++;
        }
        return ok;
    }

    // ---------------------------------------------------------------- share / clipboard

    /** Файлы в папке шаринга старше этого удаляются: получатель уже давно их прочитал. */
    private static final long SHARE_TTL_MS = 60L * 60_000;

    /**
     * FILEPROVIDER_SCOPE: FileProvider отдаёт только узкий набор папок (res/xml/file_paths.xml). Файл из
     * другого места (общее хранилище при «доступе ко всем файлам», кэш) сначала копируется в
     * cache/share/ — так получатель видит только этот файл, а не весь корень хранилища.
     */
    private static Uri shareUri(Context context, File file) throws IOException {
        String authority = context.getPackageName() + ".fileprovider";
        try {
            return FileProvider.getUriForFile(context, authority, file);
        } catch (IllegalArgumentException outsideProviderPaths) {
            if (file.length() > COPY_MAX_BYTES) {
                throw new IOException("Файл слишком большой для шаринга");
            }
            File dir = new File(context.getCacheDir(), "share");
            File[] old = dir.listFiles();
            if (old != null) {
                long now = System.currentTimeMillis();
                for (File stale : old) {
                    if (now - stale.lastModified() > SHARE_TTL_MS) {
                        deleteRecursive(stale, new int[1]);
                    }
                }
            }
            File slot = new File(dir, Long.toString(System.nanoTime()));
            if (!slot.mkdirs()) {
                throw new IOException("Не удалось подготовить файл для шаринга");
            }
            File copy = new File(slot, file.getName());
            try (InputStream in = new FileInputStream(file); OutputStream out = new FileOutputStream(copy)) {
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = in.read(buffer)) != -1) {
                    out.write(buffer, 0, read);
                }
            }
            return FileProvider.getUriForFile(context, authority, copy);
        }
    }

    static ToolResult share(Context context, JSONObject args) {
        String path = PhoneFmt.str(args, "path");
        String text = PhoneFmt.str(args, "text");
        if ((path == null || path.isEmpty()) && (text == null || text.isEmpty())) {
            return ToolResult.err("Нужен path и/или text");
        }
        try {
            Intent send = new Intent(Intent.ACTION_SEND);
            String description = "";
            if (path != null && !path.isEmpty()) {
                File file = resolve(context, path, true);
                if (!file.isFile()) {
                    return ToolResult.err("Файл не найден: " + file.getPath());
                }
                Uri uri = shareUri(context, file);
                String mime = MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension(file.getName()));
                send.setType(mime != null ? mime : "application/octet-stream");
                send.putExtra(Intent.EXTRA_STREAM, uri);
                send.setClipData(ClipData.newRawUri(file.getName(), uri));
                send.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
                description = "файл " + file.getName();
            } else {
                send.setType("text/plain");
            }
            if (text != null && !text.isEmpty()) {
                send.putExtra(Intent.EXTRA_TEXT, text);
                description = description.isEmpty() ? "текст" : description + " и текст";
            }
            Intent chooser = Intent.createChooser(send, null);
            chooser.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_GRANT_READ_URI_PERMISSION);
            context.startActivity(chooser);
            return ToolResult.ok("Открыто системное меню «Поделиться» (" + description
                    + "). Приложение и получателя выбирает пользователь — отправка не подтверждена."
                    + PhoneToolsSystem.backgroundStartWarning(context));
        } catch (IllegalArgumentException error) {
            return ToolResult.err("Этот файл нельзя передать через шаринг (вне разрешённых папок)");
        } catch (IOException error) {
            return denied(error);
        } catch (Exception error) {
            return ToolResult.err("Не удалось открыть «Поделиться»: " + error.getClass().getSimpleName());
        }
    }

    static ToolResult clipboardSet(final Context context, JSONObject args) {
        final String text = args.has("text") && !args.isNull("text") ? args.optString("text", "") : null;
        if (text == null) {
            return ToolResult.err("Не передан text");
        }
        final boolean[] ok = new boolean[] { false };
        final CountDownLatch latch = new CountDownLatch(1);
        new Handler(Looper.getMainLooper()).post(() -> {
            try {
                ClipboardManager clipboard = (ClipboardManager) context.getSystemService(Context.CLIPBOARD_SERVICE);
                if (clipboard != null) {
                    clipboard.setPrimaryClip(ClipData.newPlainText("ConexyAI", text));
                    ok[0] = true;
                }
            } catch (Throwable ignored) {
                // буфер недоступен
            } finally {
                latch.countDown();
            }
        });
        try {
            latch.await(3, TimeUnit.SECONDS);
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
        }
        return ok[0]
                ? ToolResult.ok("Текст скопирован в буфер обмена (" + text.length() + " симв.)")
                : ToolResult.err("Не удалось записать в буфер обмена");
    }

}
