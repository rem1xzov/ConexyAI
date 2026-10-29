package conexy.app;

import android.content.Context;
import android.view.accessibility.AccessibilityNodeInfo;

import org.json.JSONObject;

/** Инструменты экрана поверх {@link ConexyAccessibilityService}. */
final class PhoneToolsScreen {
    static final String NOT_ENABLED =
            "Служба доступности ConexyAI выключена: включите службу доступности ConexyAI — "
                    + "вызовите phone_request_permission accessibility.";

    private PhoneToolsScreen() {}

    private static ConexyAccessibilityService service(Context context) {
        return ConexyAccessibilityService.instance();
    }

    private static ToolResult notReady(Context context) {
        if (PhonePermissions.accessibilityEnabled(context)) {
            return ToolResult.err("Служба доступности ConexyAI включена, но ещё не подключилась. "
                    + "Подождите пару секунд и повторите; если не помогает — выключите и включите её в настройках.");
        }
        return ToolResult.err(NOT_ENABLED);
    }

    static ToolResult readScreen(Context context) {
        ConexyAccessibilityService svc = service(context);
        if (svc == null) {
            return notReady(context);
        }
        String tree = svc.readScreen();
        if (tree == null) {
            return ToolResult.err("Не удалось получить содержимое экрана (окно защищено или недоступно). "
                    + "Попробуйте phone_screenshot.");
        }
        return ToolResult.ok(tree);
    }

    static ToolResult screenshot(Context context) {
        ConexyAccessibilityService svc = service(context);
        if (svc == null) {
            return notReady(context);
        }
        String[] error = new String[1];
        byte[] jpeg = svc.screenshotJpeg(error);
        if (jpeg == null) {
            return ToolResult.err(error[0] == null ? "Не удалось сделать скриншот" : error[0]);
        }
        return ToolResult.ok("IMAGE_BASE64:image/jpeg:" + ImageUtil.base64(jpeg)
                + "\nСкриншот экрана " + svc.screenWidth() + "x" + svc.screenHeight()
                + " (координаты для phone_tap — в пикселях исходного экрана; изображение уменьшено до ~1280 px).");
    }

    static ToolResult tap(Context context, JSONObject args) {
        ConexyAccessibilityService svc = service(context);
        if (svc == null) {
            return notReady(context);
        }
        boolean longPress = args.optBoolean("long_press", false);
        String nodeId = PhoneFmt.str(args, "node_id");
        String text = PhoneFmt.str(args, "text");
        if (nodeId != null && !nodeId.isEmpty()) {
            AccessibilityNodeInfo node = svc.findById(nodeId);
            if (node == null) {
                return ToolResult.err("Узел " + nodeId + " не найден (экран мог измениться — вызовите phone_screen_read)");
            }
            return svc.tapNode(node, longPress)
                    ? ToolResult.ok((longPress ? "Долгое нажатие" : "Нажатие") + " по узлу " + nodeId + " выполнено")
                    : ToolResult.err("Не удалось нажать узел " + nodeId);
        }
        if (text != null && !text.isEmpty()) {
            AccessibilityNodeInfo node = svc.findByText(text);
            if (node == null) {
                return ToolResult.err("Элемент с текстом «" + text + "» не найден на экране (phone_screen_read покажет, что есть)");
            }
            return svc.tapNode(node, longPress)
                    ? ToolResult.ok((longPress ? "Долгое нажатие" : "Нажатие") + " по «" + text + "» выполнено")
                    : ToolResult.err("Не удалось нажать «" + text + "»");
        }
        if (args.has("x") && args.has("y")) {
            double x = args.optDouble("x", Double.NaN);
            double y = args.optDouble("y", Double.NaN);
            if (Double.isNaN(x) || Double.isNaN(y) || x < 0 || y < 0 || x > svc.screenWidth() || y > svc.screenHeight()) {
                return ToolResult.err("Координаты вне экрана " + svc.screenWidth() + "x" + svc.screenHeight());
            }
            return svc.tapAt((float) x, (float) y, longPress)
                    ? ToolResult.ok((longPress ? "Долгое нажатие" : "Нажатие") + " в (" + Math.round(x) + ", " + Math.round(y) + ") выполнено")
                    : ToolResult.err("Жест нажатия отклонён или отменён системой");
        }
        return ToolResult.err("Укажите text, node_id или x и y");
    }

    static ToolResult swipe(Context context, JSONObject args) {
        ConexyAccessibilityService svc = service(context);
        if (svc == null) {
            return notReady(context);
        }
        int w = svc.screenWidth();
        int h = svc.screenHeight();
        long duration = Math.max(50, Math.min(args.optLong("duration_ms", 350), 5000));
        float x1;
        float y1;
        float x2;
        float y2;
        String direction = PhoneFmt.str(args, "direction");
        if (direction != null && !direction.isEmpty()) {
            float cx = w / 2f;
            float cy = h / 2f;
            switch (direction) {
                case "up":
                    x1 = cx; y1 = h * 0.75f; x2 = cx; y2 = h * 0.25f;
                    break;
                case "down":
                    x1 = cx; y1 = h * 0.25f; x2 = cx; y2 = h * 0.75f;
                    break;
                case "left":
                    x1 = w * 0.8f; y1 = cy; x2 = w * 0.2f; y2 = cy;
                    break;
                case "right":
                    x1 = w * 0.2f; y1 = cy; x2 = w * 0.8f; y2 = cy;
                    break;
                default:
                    return ToolResult.err("direction: up|down|left|right");
            }
        } else if (args.has("x1") && args.has("y1") && args.has("x2") && args.has("y2")) {
            x1 = (float) args.optDouble("x1");
            y1 = (float) args.optDouble("y1");
            x2 = (float) args.optDouble("x2");
            y2 = (float) args.optDouble("y2");
            if (outside(x1, y1, w, h) || outside(x2, y2, w, h)) {
                return ToolResult.err("Координаты вне экрана " + w + "x" + h);
            }
        } else {
            return ToolResult.err("Укажите direction или x1,y1,x2,y2");
        }
        return svc.swipe(x1, y1, x2, y2, duration)
                ? ToolResult.ok("Свайп выполнен (палец движется " + (direction != null ? direction : "по заданным координатам")
                        + "; up — палец вверх, содержимое прокручивается вниз)")
                : ToolResult.err("Жест свайпа отклонён или отменён системой");
    }

    private static boolean outside(float x, float y, int w, int h) {
        return Float.isNaN(x) || Float.isNaN(y) || x < 0 || y < 0 || x > w || y > h;
    }

    static ToolResult typeText(Context context, JSONObject args) {
        ConexyAccessibilityService svc = service(context);
        if (svc == null) {
            return notReady(context);
        }
        if (!args.has("text") || args.isNull("text")) {
            return ToolResult.err("Не передан text");
        }
        String text = args.optString("text", "");
        String error = svc.typeText(text, PhoneFmt.str(args, "node_id"));
        return error == null
                ? ToolResult.ok("Текст введён (" + text.length() + " симв.)")
                : ToolResult.err(error);
    }

    static ToolResult globalAction(Context context, JSONObject args) {
        ConexyAccessibilityService svc = service(context);
        if (svc == null) {
            return notReady(context);
        }
        String action = args.optString("action", "");
        Boolean result = svc.globalAction(action);
        if (result == null) {
            return ToolResult.err("action: back|home|recents|notifications|quick_settings|lock_screen");
        }
        return result
                ? ToolResult.ok("Действие «" + action + "» выполнено")
                : ToolResult.err("Система не выполнила действие «" + action + "»"
                        + ("lock_screen".equals(action) ? " (блокировка экрана доступна с Android 9)" : ""));
    }
}
