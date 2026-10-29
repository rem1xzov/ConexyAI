package conexy.app;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.content.res.Resources;
import android.graphics.Bitmap;
import android.graphics.ColorSpace;
import android.graphics.Path;
import android.graphics.Rect;
import android.hardware.HardwareBuffer;
import android.os.Build;
import android.os.Bundle;
import android.view.Display;
import android.view.accessibility.AccessibilityEvent;
import android.view.accessibility.AccessibilityNodeInfo;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/**
 * Служба доступности: чтение экрана, нажатия, свайпы, ввод текста, глобальные действия, скриншот.
 * Все методы вызываются из фонового потока (не из главного!) — они ждут результата жестов.
 */
public class ConexyAccessibilityService extends AccessibilityService {

    private static volatile ConexyAccessibilityService instance;

    private static final java.util.concurrent.Executor SCREENSHOT_EXECUTOR =
            java.util.concurrent.Executors.newSingleThreadExecutor(r -> {
                Thread thread = new Thread(r, "conexy-screenshot");
                thread.setDaemon(true);
                return thread;
            });
    private static final int MAX_NODES = 700;
    private static final int MAX_DEPTH = 40;

    static ConexyAccessibilityService instance() {
        return instance;
    }

    @Override
    protected void onServiceConnected() {
        super.onServiceConnected();
        instance = this;
    }

    @Override
    public boolean onUnbind(android.content.Intent intent) {
        if (instance == this) {
            instance = null;
        }
        return super.onUnbind(intent);
    }

    @Override
    public void onDestroy() {
        if (instance == this) {
            instance = null;
        }
        super.onDestroy();
    }

    @Override
    public void onAccessibilityEvent(AccessibilityEvent event) {
        // События не обрабатываются: служба работает только по командам инструментов.
    }

    @Override
    public void onInterrupt() {
        // Нечего прерывать.
    }

    // ---------------------------------------------------------------- чтение экрана

    int screenWidth() {
        return Resources.getSystem().getDisplayMetrics().widthPixels;
    }

    int screenHeight() {
        return Resources.getSystem().getDisplayMetrics().heightPixels;
    }

    /** Текстовое дерево экрана с node_id вида «0.2.1» (путь по индексам детей). */
    String readScreen() {
        AccessibilityNodeInfo root = getRootInActiveWindow();
        if (root == null) {
            return null;
        }
        StringBuilder out = new StringBuilder();
        out.append("package=").append(root.getPackageName())
                .append(" screen=").append(screenWidth()).append('x').append(screenHeight()).append('\n');
        int[] counter = new int[] { 0 };
        dump(root, "0", 0, out, counter);
        if (counter[0] >= MAX_NODES) {
            out.append("… (дерево обрезано, прокрутите экран и прочитайте снова)\n");
        }
        return out.toString();
    }

    private void dump(AccessibilityNodeInfo node, String id, int depth, StringBuilder out, int[] counter) {
        if (node == null || counter[0] >= MAX_NODES || depth > MAX_DEPTH) {
            return;
        }
        if (node.isVisibleToUser()) {
            CharSequence text = node.isPassword() ? (node.getText() == null ? null : "••••••") : node.getText();
            CharSequence desc = node.getContentDescription();
            boolean interesting = notEmpty(text) || notEmpty(desc) || node.isClickable()
                    || node.isEditable() || node.isScrollable() || node.isCheckable() || node.isLongClickable();
            if (interesting) {
                counter[0]++;
                Rect bounds = new Rect();
                node.getBoundsInScreen(bounds);
                out.append(indent(Math.min(depth, 12)))
                        .append('[').append(id).append("] ")
                        .append(shortClass(node.getClassName()));
                if (notEmpty(text)) {
                    out.append(" \"").append(clip(text.toString(), 200)).append('"');
                }
                if (notEmpty(desc)) {
                    out.append(" desc=\"").append(clip(desc.toString(), 120)).append('"');
                }
                out.append(" bounds=").append(bounds.left).append(',').append(bounds.top).append(',')
                        .append(bounds.right).append(',').append(bounds.bottom);
                List<String> flags = new ArrayList<>();
                if (node.isClickable()) flags.add("clickable");
                if (node.isLongClickable()) flags.add("long_clickable");
                if (node.isEditable()) flags.add("editable");
                if (node.isScrollable()) flags.add("scrollable");
                if (node.isCheckable()) flags.add(node.isChecked() ? "checked" : "unchecked");
                if (node.isFocused()) flags.add("focused");
                if (!node.isEnabled()) flags.add("disabled");
                if (!flags.isEmpty()) {
                    out.append(" {").append(android.text.TextUtils.join(",", flags)).append('}');
                }
                out.append('\n');
            }
        }
        int count = node.getChildCount();
        for (int i = 0; i < count; i++) {
            dump(node.getChild(i), id + "." + i, depth + 1, out, counter);
        }
    }

    private static boolean notEmpty(CharSequence value) {
        return value != null && value.toString().trim().length() > 0;
    }

    private static String indent(int depth) {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < depth; i++) {
            builder.append(' ');
        }
        return builder.toString();
    }

    private static String shortClass(CharSequence name) {
        if (name == null) {
            return "View";
        }
        String value = name.toString();
        int dot = value.lastIndexOf('.');
        return dot >= 0 ? value.substring(dot + 1) : value;
    }

    private static String clip(String value, int max) {
        String single = value.replace('\n', ' ');
        return single.length() <= max ? single : single.substring(0, max) + "…";
    }

    // ---------------------------------------------------------------- поиск узлов

    AccessibilityNodeInfo findById(String nodeId) {
        AccessibilityNodeInfo node = getRootInActiveWindow();
        if (node == null || nodeId == null) {
            return null;
        }
        String[] parts = nodeId.trim().split("\\.");
        if (parts.length == 0 || !"0".equals(parts[0])) {
            return null;
        }
        for (int i = 1; i < parts.length; i++) {
            int index;
            try {
                index = Integer.parseInt(parts[i]);
            } catch (NumberFormatException error) {
                return null;
            }
            if (index < 0 || index >= node.getChildCount()) {
                return null;
            }
            node = node.getChild(index);
            if (node == null) {
                return null;
            }
        }
        return node;
    }

    /** Ищет узел по тексту/описанию: точное совпадение приоритетнее, видимые приоритетнее. */
    AccessibilityNodeInfo findByText(String text) {
        AccessibilityNodeInfo root = getRootInActiveWindow();
        if (root == null || text == null || text.trim().isEmpty()) {
            return null;
        }
        List<AccessibilityNodeInfo> found = root.findAccessibilityNodeInfosByText(text);
        if (found == null || found.isEmpty()) {
            return null;
        }
        String wanted = text.trim().toLowerCase(Locale.ROOT);
        AccessibilityNodeInfo best = null;
        int bestScore = -1;
        for (AccessibilityNodeInfo node : found) {
            int score = 0;
            if (node.isVisibleToUser()) score += 4;
            if (node.isEnabled()) score += 1;
            if (hasExactText(node, wanted)) score += 8;
            if (score > bestScore) {
                bestScore = score;
                best = node;
            }
        }
        return best;
    }

    private static boolean hasExactText(AccessibilityNodeInfo node, String wanted) {
        CharSequence text = node.getText();
        CharSequence desc = node.getContentDescription();
        return (text != null && text.toString().trim().toLowerCase(Locale.ROOT).equals(wanted))
                || (desc != null && desc.toString().trim().toLowerCase(Locale.ROOT).equals(wanted));
    }

    // ---------------------------------------------------------------- действия

    /** Нажимает узел (или ближайшего кликабельного предка); при неудаче — жест в центр узла. */
    boolean tapNode(AccessibilityNodeInfo node, boolean longPress) {
        if (node == null) {
            return false;
        }
        AccessibilityNodeInfo target = node;
        int guard = 0;
        while (target != null && guard++ < 8) {
            boolean can = longPress ? target.isLongClickable() : target.isClickable();
            if (can && target.isEnabled()) {
                int action = longPress ? AccessibilityNodeInfo.ACTION_LONG_CLICK : AccessibilityNodeInfo.ACTION_CLICK;
                if (target.performAction(action)) {
                    return true;
                }
            }
            target = target.getParent();
        }
        Rect bounds = new Rect();
        node.getBoundsInScreen(bounds);
        if (bounds.isEmpty()) {
            return false;
        }
        return tapAt(bounds.centerX(), bounds.centerY(), longPress);
    }

    boolean tapAt(float x, float y, boolean longPress) {
        Path path = new Path();
        path.moveTo(x, y);
        long duration = longPress ? 800 : 60;
        return dispatch(path, duration);
    }

    boolean swipe(float x1, float y1, float x2, float y2, long durationMs) {
        Path path = new Path();
        path.moveTo(x1, y1);
        path.lineTo(x2, y2);
        return dispatch(path, Math.max(50, Math.min(durationMs, 5000)));
    }

    private boolean dispatch(Path path, long durationMs) {
        final CountDownLatch latch = new CountDownLatch(1);
        final boolean[] ok = new boolean[] { false };
        GestureDescription.Builder builder = new GestureDescription.Builder();
        builder.addStroke(new GestureDescription.StrokeDescription(path, 0, durationMs));
        boolean started;
        try {
            started = dispatchGesture(builder.build(), new GestureResultCallback() {
                @Override
                public void onCompleted(GestureDescription gestureDescription) {
                    ok[0] = true;
                    latch.countDown();
                }

                @Override
                public void onCancelled(GestureDescription gestureDescription) {
                    latch.countDown();
                }
            }, null);
        } catch (RuntimeException error) {
            // Служба отключена/жест слишком длинный.
            return false;
        }
        if (!started) {
            return false;
        }
        try {
            latch.await(durationMs + 3000, TimeUnit.MILLISECONDS);
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            return false;
        }
        return ok[0];
    }

    /** @return null при успехе, иначе причина. */
    String typeText(String text, String nodeId) {
        AccessibilityNodeInfo target = null;
        if (nodeId != null && !nodeId.isEmpty()) {
            target = findById(nodeId);
            if (target == null) {
                return "Узел " + nodeId + " не найден (экран мог измениться — вызовите phone_screen_read снова)";
            }
        } else {
            target = findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
            if (target == null || !target.isEditable()) {
                target = firstEditable(getRootInActiveWindow(), 0);
            }
            if (target == null) {
                return "Не найдено поле ввода: нажмите на поле (phone_tap) или передайте node_id";
            }
        }
        if (!target.isEditable()) {
            AccessibilityNodeInfo editable = firstEditable(target, 0);
            if (editable != null) {
                target = editable;
            } else {
                return "Узел не является полем ввода";
            }
        }
        target.performAction(AccessibilityNodeInfo.ACTION_FOCUS);
        Bundle args = new Bundle();
        args.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, text);
        if (!target.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)) {
            return "Поле не приняло текст (ACTION_SET_TEXT отклонён)";
        }
        return null;
    }

    private AccessibilityNodeInfo firstEditable(AccessibilityNodeInfo node, int depth) {
        if (node == null || depth > MAX_DEPTH) {
            return null;
        }
        if (node.isEditable() && node.isVisibleToUser()) {
            return node;
        }
        for (int i = 0; i < node.getChildCount(); i++) {
            AccessibilityNodeInfo found = firstEditable(node.getChild(i), depth + 1);
            if (found != null) {
                return found;
            }
        }
        return null;
    }

    /** @return null, если действие неизвестно. */
    Boolean globalAction(String action) {
        int code;
        switch (action) {
            case "back":
                code = GLOBAL_ACTION_BACK;
                break;
            case "home":
                code = GLOBAL_ACTION_HOME;
                break;
            case "recents":
                code = GLOBAL_ACTION_RECENTS;
                break;
            case "notifications":
                code = GLOBAL_ACTION_NOTIFICATIONS;
                break;
            case "quick_settings":
                code = GLOBAL_ACTION_QUICK_SETTINGS;
                break;
            case "lock_screen":
                if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) {
                    return Boolean.FALSE;
                }
                code = GLOBAL_ACTION_LOCK_SCREEN;
                break;
            default:
                return null;
        }
        return performGlobalAction(code);
    }

    /**
     * Ждёт появления узла с одним из текстов и нажимает его.
     *
     * @return текст, по которому нажали, или null
     */
    String clickAnyText(String[] texts, long timeoutMs) {
        long deadline = System.currentTimeMillis() + timeoutMs;
        do {
            for (String text : texts) {
                AccessibilityNodeInfo node = findByText(text);
                if (node != null && node.isEnabled() && node.isVisibleToUser() && tapNode(node, false)) {
                    return text;
                }
            }
            try {
                Thread.sleep(300);
            } catch (InterruptedException error) {
                Thread.currentThread().interrupt();
                return null;
            }
        } while (System.currentTimeMillis() < deadline);
        return null;
    }

    // ---------------------------------------------------------------- скриншот

    /** @return JPEG-байты или null; причина — в {@code error[0]}. */
    byte[] screenshotJpeg(String[] error) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) {
            error[0] = "Скриншот через службу доступности требует Android 11+";
            return null;
        }
        final CountDownLatch latch = new CountDownLatch(1);
        final Bitmap[] holder = new Bitmap[1];
        final String[] failure = new String[1];
        // Копирование битмапа — не на главном потоке (нельзя грузить UI).
        try {
            takeScreenshot(Display.DEFAULT_DISPLAY, SCREENSHOT_EXECUTOR, new TakeScreenshotCallback() {
                @Override
                public void onSuccess(ScreenshotResult result) {
                    HardwareBuffer buffer = result.getHardwareBuffer();
                    try {
                        ColorSpace space = result.getColorSpace();
                        Bitmap hardware = Bitmap.wrapHardwareBuffer(buffer, space);
                        if (hardware != null) {
                            holder[0] = hardware.copy(Bitmap.Config.ARGB_8888, false);
                            hardware.recycle();
                        }
                    } catch (Throwable t) {
                        failure[0] = "Не удалось прочитать скриншот: " + t.getClass().getSimpleName();
                    } finally {
                        buffer.close();
                        latch.countDown();
                    }
                }

                @Override
                public void onFailure(int errorCode) {
                    failure[0] = "Система отказала в скриншоте (код " + errorCode
                            + (errorCode == ERROR_TAKE_SCREENSHOT_INTERVAL_TIME_SHORT ? ", слишком часто" : "") + ")";
                    latch.countDown();
                }
            });
        } catch (Throwable t) {
            error[0] = "Служба доступности не смогла сделать скриншот: " + t.getClass().getSimpleName();
            return null;
        }
        try {
            if (!latch.await(6, TimeUnit.SECONDS)) {
                error[0] = "Тайм-аут скриншота";
                return null;
            }
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            error[0] = "Прервано";
            return null;
        }
        Bitmap bitmap = holder[0];
        if (bitmap == null) {
            error[0] = failure[0] != null ? failure[0] : "Скриншот пуст (возможно, экран защищён от съёмки)";
            return null;
        }
        return ImageUtil.toJpeg(bitmap, 1280, 80);
    }
}
