package conexy.app;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.ClipData;
import android.content.Context;
import android.content.Intent;

import android.net.Uri;
import android.util.Log;

import androidx.activity.result.ActivityResult;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.ActivityCallback;
import com.getcapacitor.annotation.CapacitorPlugin;
import com.google.firebase.FirebaseApp;
import com.google.firebase.messaging.FirebaseMessaging;

import org.json.JSONException;
import org.json.JSONObject;

import java.lang.ref.WeakReference;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.RejectedExecutionException;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;

/**
 * Агент телефона: JS-API для инструментов {@code phone_*}, разрешений, уведомлений и push-токена.
 * Сами инструменты реализованы в {@code PhoneTools*}; плагин только маршрутизирует вызовы.
 * Контракт — frontend/PHONE_AGENT.md.
 */
@CapacitorPlugin(name = "PhoneAgent")
public class PhoneAgentPlugin extends Plugin {
    private static final String TAG = "PhoneAgent";
    private static final String PREFS = "conexy_phone_agent";
    private static final String KEY_PUSH_TOKEN = "push_token";
    /**
     * NATIVE_AGENT_SWITCH: выключатель «Агент телефона» хранится нативно — проверка только в JS обходилась
     * прямым вызовом execute. Ключа нет (старый веб-клиент ещё не вызывал setEnabled) — считаем
     * включённым, как и дефолт в JS; новый клиент синхронизирует флаг при старте.
     */
    private static final String KEY_AGENT_ENABLED = "agent_enabled";

    private static volatile WeakReference<PhoneAgentPlugin> live;

    /** Обычный инструмент: дольше самых медленных (удаление приложения, очистка кэша) с запасом. */
    private static final long TOOL_TIMEOUT_MS = 120_000;
    /** Диалоги с участием пользователя (разрешения, выбор фото). */
    private static final long USER_TIMEOUT_MS = 330_000;
    private static final long REQUEST_ALL_TIMEOUT_MS = 20L * 60_000;

    private static final class Pending {
        final ScheduledFuture<?> timeout;
        final Runnable fallback;

        Pending(ScheduledFuture<?> timeout, Runnable fallback) {
            this.timeout = timeout;
            this.fallback = fallback;
        }
    }

    private final ExecutorService executor = Executors.newCachedThreadPool();
    private final ScheduledExecutorService timer = Executors.newSingleThreadScheduledExecutor(r -> {
        Thread thread = new Thread(r, "conexy-phone-timeout");
        thread.setDaemon(true);
        return thread;
    });
    private final Map<PluginCall, Pending> pending = new ConcurrentHashMap<>();
    private PhonePermissionRequester requester;
    private JSObject launchNotification;
    private String lastPermissions;

    // ---------------------------------------------------------------- жизненный цикл

    @Override
    public void load() {
        live = new WeakReference<>(this);
        requester = new PhonePermissionRequester(new PhonePermissionRequester.ActivitySource() {
            @Override
            public Activity activity() {
                return getActivity();
            }

            @Override
            public Context context() {
                return getContext();
            }
        });
        NotificationHelper.ensureChannels(getContext());

        Activity activity = getActivity();
        if (activity != null) {
            JSObject data = notificationData(activity.getIntent());
            if (data != null) {
                launchNotification = data;
                notifyListeners("notificationOpened", data, true);
            }
        }
        executor.execute(() -> lastPermissions = requester.all().toString());
    }

    @Override
    protected void handleOnNewIntent(Intent intent) {
        JSObject data = notificationData(intent);
        if (data != null) {
            notifyListeners("notificationOpened", data, true);
        }
    }

    @Override
    protected void handleOnResume() {
        if (requester != null) {
            requester.onResume();
            executor.execute(this::checkPermissionsChanged);
        }
    }

    @Override
    protected void handleOnDestroy() {
        WeakReference<PhoneAgentPlugin> ref = live;
        if (ref != null && ref.get() == this) {
            live = null;
        }
        // Activity учиттожена: все незавершённые вызовы закрываем ошибкой, а не оставляем висеть.
        for (Pending item : new ArrayList<>(pending.values())) {
            try {
                item.fallback.run();
            } catch (Throwable ignored) {
                // вызов уже не доставить
            }
        }
        pending.clear();
        timer.shutdownNow();
        super.handleOnDestroy();
    }

    // ---------------------------------------------------------------- гарантия ответа

    /** Если вызов не завершился за {@code ms}, выполняется {@code fallback} (он обязан вызвать settle). */
    private void arm(final PluginCall call, long ms, final Runnable fallback) {
        try {
            ScheduledFuture<?> future = timer.schedule(() -> {
                try {
                    fallback.run();
                } catch (Throwable error) {
                    Log.w(TAG, "Тайм-аут вызова: " + error.getClass().getSimpleName());
                }
            }, ms, TimeUnit.MILLISECONDS);
            pending.put(call, new Pending(future, fallback));
        } catch (RejectedExecutionException error) {
            fallback.run();
        }
    }

    /** Единственный путь ответа в JS: повторный вызов после завершения/тайм-аута игнорируется. */
    private void settle(PluginCall call, JSObject ret) {
        Pending item = pending.remove(call);
        if (item != null) {
            item.timeout.cancel(false);
        }
        synchronized (call) {
            if (call.isReleased()) {
                return;
            }
            try {
                call.resolve(ret);
            } catch (Throwable error) {
                Log.w(TAG, "Не удалось ответить в JS: " + error.getClass().getSimpleName());
            }
        }
    }

    /** Из {@link PhoneFirebaseService}: токен FCM обновился. */
    static void dispatchPushToken(String token) {
        if (token == null || token.isEmpty()) {
            return;
        }
        WeakReference<PhoneAgentPlugin> ref = live;
        PhoneAgentPlugin plugin = ref == null ? null : ref.get();
        if (plugin != null) {
            plugin.getContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                    .edit().putString(KEY_PUSH_TOKEN, token).apply();
            plugin.notifyListeners("pushToken", new JSObject().put("token", token));
        }
    }

    private synchronized void checkPermissionsChanged() {
        JSONObject current = requester.all();
        String serialized = current.toString();
        if (lastPermissions != null && !lastPermissions.equals(serialized)) {
            lastPermissions = serialized;
            notifyListeners("permissionsChanged", permissionsPayload(current));
        } else {
            lastPermissions = serialized;
        }
    }

    private static JSObject permissionsPayload(JSONObject permissions) {
        try {
            JSObject payload = new JSObject();
            payload.put("permissions", new JSObject(permissions.toString()));
            return payload;
        } catch (JSONException error) {
            return new JSObject();
        }
    }

    private JSObject notificationData(Intent intent) {
        if (intent == null || !intent.getBooleanExtra(NotificationHelper.EXTRA_FROM_NOTIFICATION, false)) {
            return null;
        }
        intent.removeExtra(NotificationHelper.EXTRA_FROM_NOTIFICATION);
        JSObject data = new JSObject();
        putIfPresent(data, "type", intent.getStringExtra(NotificationHelper.EXTRA_TYPE));
        putIfPresent(data, "taskId", intent.getStringExtra(NotificationHelper.EXTRA_TASK_ID));
        putIfPresent(data, "chatId", intent.getStringExtra(NotificationHelper.EXTRA_CHAT_ID));
        putIfPresent(data, "actionId", intent.getStringExtra(NotificationHelper.EXTRA_ACTION_ID));
        data.put("dangerous", intent.getBooleanExtra(NotificationHelper.EXTRA_DANGEROUS, false));
        return data;
    }

    private static void putIfPresent(JSObject target, String key, String value) {
        if (value != null && !value.isEmpty()) {
            target.put(key, value);
        }
    }

    // ---------------------------------------------------------------- execute

    @PluginMethod
    public void execute(final PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        if (!isAgentEnabled()) {
            resolveTool(call, ToolResult.err("Агент телефона выключен в настройках приложения"));
            return;
        }
        try {
            executeInner(call);
        } catch (Throwable error) {
            resolveTool(call, ToolResult.err("Сбой запуска инструмента: " + error.getClass().getSimpleName()));
        }
    }

    private void executeInner(final PluginCall call) {
        final String tool = call.getString("tool", "");
        final JSONObject args;
        try {
            Object raw = call.getData().opt("args");
            if (raw instanceof JSONObject) {
                args = (JSONObject) raw;
            } else if (raw instanceof String && !((String) raw).trim().isEmpty()) {
                args = new JSONObject((String) raw);
            } else {
                args = new JSONObject();
            }
        } catch (JSONException error) {
            resolveTool(call, ToolResult.err("Аргументы инструмента — некорректный JSON"));
            return;
        }

        if ("phone_gallery_pick".equals(tool)) {
            startPick(call, args);
            return;
        }
        if ("phone_request_permission".equals(tool)) {
            requestPermissionTool(call, args);
            return;
        }
        arm(call, TOOL_TIMEOUT_MS, () -> resolveTool(call, ToolResult.err("Тайм-аут: инструмент " + tool
                + " не ответил за " + (TOOL_TIMEOUT_MS / 1000) + " с (приложение закрыто или операция зависла)")));
        try {
            executor.execute(() -> {
                ToolResult result;
                try {
                    result = dispatch(tool, args);
                } catch (Throwable error) {
                    Log.w(TAG, "Сбой инструмента " + tool + ": " + error.getClass().getSimpleName());
                    result = ToolResult.err("Внутренняя ошибка инструмента " + tool + ": " + error.getClass().getSimpleName()
                            + (error.getMessage() == null ? "" : " — " + error.getMessage()));
                }
                resolveTool(call, result == null ? ToolResult.err("Инструмент " + tool + " не вернул результат") : result);
            });
        } catch (RejectedExecutionException error) {
            resolveTool(call, ToolResult.err("Приложение закрывается, инструмент не запущен"));
        }
    }

    private ToolResult dispatch(String tool, JSONObject args) {
        Context context = getContext();
        switch (tool) {
            case "phone_device_info":
                return PhoneToolsSystem.deviceInfo(context);
            case "phone_permissions":
                return permissionsTool();
            case "phone_apps_list":
                return PhoneToolsSystem.appsList(context, args);
            case "phone_battery_report":
                return PhoneToolsSystem.batteryReport(context);
            case "phone_storage_report":
                return PhoneToolsSystem.storageReport(context);
            case "phone_open_settings":
                return PhoneToolsSystem.openSettings(context, args);
            case "phone_launch_app":
                return PhoneToolsSystem.launchApp(context, args);
            case "phone_open_url":
                return PhoneToolsSystem.openUrl(context, args);
            case "phone_uninstall_app":
                return PhoneToolsSystem.uninstallApp(context, args);
            case "phone_clear_cache":
                return PhoneToolsSystem.clearCache(context, args);
            case "phone_force_stop_app":
                return PhoneToolsSystem.forceStopApp(context, args);

            case "phone_screen_read":
                return PhoneToolsScreen.readScreen(context);
            case "phone_screenshot":
                return PhoneToolsScreen.screenshot(context);
            case "phone_tap":
                return PhoneToolsScreen.tap(context, args);
            case "phone_swipe":
                return PhoneToolsScreen.swipe(context, args);
            case "phone_type_text":
                return PhoneToolsScreen.typeText(context, args);
            case "phone_global_action":
                return PhoneToolsScreen.globalAction(context, args);

            case "phone_gallery_list":
                return PhoneToolsMedia.galleryList(context, args);
            case "phone_image_view":
                return PhoneToolsMedia.imageView(context, args);
            case "phone_widget_photo":
                return PhoneToolsMedia.widgetPhoto(context, args);
            case "phone_set_wallpaper":
                return PhoneToolsMedia.setWallpaper(context, args);

            case "phone_file_list":
                return PhoneToolsFiles.list(context, args);
            case "phone_file_read":
                return PhoneToolsFiles.read(context, args);
            case "phone_file_search":
                return PhoneToolsFiles.search(context, args);
            case "phone_file_write":
                return PhoneToolsFiles.write(context, args);
            case "phone_file_mkdir":
                return PhoneToolsFiles.mkdir(context, args);
            case "phone_file_copy":
                return PhoneToolsFiles.copy(context, args);
            case "phone_file_move":
                return PhoneToolsFiles.move(context, args);
            case "phone_file_delete":
                return PhoneToolsFiles.delete(context, args);
            case "phone_share":
                return PhoneToolsFiles.share(context, args);
            case "phone_clipboard_set":
                return PhoneToolsFiles.clipboardSet(context, args);
            default:
                return ToolResult.err("Неизвестный инструмент телефона: " + tool);
        }
    }

    private ToolResult permissionsTool() {
        try {
            return ToolResult.ok(requester.all().toString(2)
                    + "\ngranted — выдано, prompt — можно запросить (phone_request_permission), "
                    + "denied — отказано (выдать можно только в настройках: phone_open_settings app_details), "
                    + "unavailable — недоступно на этом устройстве.");
        } catch (JSONException error) {
            return ToolResult.err("Не удалось сформировать список разрешений");
        }
    }

    private void resolveTool(PluginCall call, ToolResult result) {
        JSObject ret = new JSObject();
        ret.put("output", result.output);
        ret.put("isError", result.isError);
        settle(call, ret);
    }

    // ---------------------------------------------------------------- phone_request_permission

    private void requestPermissionTool(final PluginCall call, JSONObject args) {
        final String id = PhoneFmt.str(args, "permission");
        if (id == null || !PhonePermissions.isKnown(id)) {
            resolveTool(call, ToolResult.err("Неизвестное разрешение: «" + id + "». Допустимо: camera, microphone, photos, "
                    + "videos, audio, notifications, contacts, location, usage_access, all_files, accessibility, "
                    + "battery_optimization, install_packages, overlay, write_settings"));
            return;
        }
        arm(call, USER_TIMEOUT_MS + 30_000, () -> resolveTool(call,
                ToolResult.err("Тайм-аут запроса разрешения " + id + ": пользователь не ответил")));
        requester.request(id, state -> {
            safeBackground(this::checkPermissionsChanged);
            StringBuilder out = new StringBuilder("permission=" + id + " state=" + state);
            if ("granted".equals(state)) {
                out.append(" — разрешение выдано.");
            } else if ("unavailable".equals(state)) {
                out.append(" — недоступно на этом устройстве.");
            } else {
                out.append(" — пользователь не выдал разрешение.");
                String hint = PhonePermissions.hintIfStillDenied(id);
                if (!hint.isEmpty()) {
                    out.append(' ').append(hint);
                } else if ("denied".equals(state)) {
                    out.append(" Системный диалог больше не показывается: откройте phone_open_settings app_details.");
                }
            }
            resolveTool(call, "unavailable".equals(state) ? ToolResult.err(out.toString()) : ToolResult.ok(out.toString()));
        });
    }

    // ---------------------------------------------------------------- phone_gallery_pick

    private void startPick(final PluginCall call, JSONObject args) {
        final String media = args.optString("media", "images");
        final boolean multiple = args.optBoolean("multiple", false);
        final Activity activity = getActivity();
        if (activity == null || activity.isFinishing() || activity.isDestroyed()) {
            resolveTool(call, ToolResult.err("Приложение не на экране: откройте ConexyAI, чтобы показать выбор фото"));
            return;
        }
        arm(call, USER_TIMEOUT_MS, () -> resolveTool(call,
                ToolResult.err("Тайм-аут: пользователь не завершил выбор фото (или приложение было закрыто)")));
        activity.runOnUiThread(() -> {
            try {
                startActivityForResult(call, PhoneToolsMedia.pickIntent(media, multiple, true), "pickResult");
            } catch (ActivityNotFoundException first) {
                try {
                    startActivityForResult(call, PhoneToolsMedia.pickIntent(media, multiple, false), "pickResult");
                } catch (Exception second) {
                    resolveTool(call, ToolResult.err("На устройстве нет приложения для выбора фото"));
                }
            } catch (Exception error) {
                resolveTool(call, ToolResult.err("Не удалось открыть выбор фото: " + error.getClass().getSimpleName()));
            }
        });
    }

    @ActivityCallback
    private void pickResult(PluginCall call, ActivityResult result) {
        if (call == null) {
            return;
        }
        Intent data = result.getData();
        if (result.getResultCode() != Activity.RESULT_OK || data == null) {
            resolveTool(call, ToolResult.ok("Пользователь отменил выбор — ничего не выбрано."));
            return;
        }
        final List<Uri> uris = new ArrayList<>();
        ClipData clip = data.getClipData();
        if (clip != null) {
            for (int i = 0; i < clip.getItemCount(); i++) {
                Uri uri = clip.getItemAt(i).getUri();
                if (uri != null) {
                    uris.add(uri);
                }
            }
        } else if (data.getData() != null) {
            uris.add(data.getData());
        }
        final Context context = getContext();
        try {
            executor.execute(() -> {
                ToolResult copied;
                try {
                    copied = PhoneToolsMedia.copyPicked(context, uris);
                } catch (Throwable error) {
                    copied = ToolResult.err("Не удалось сохранить выбранные файлы: " + error.getClass().getSimpleName());
                }
                resolveTool(call, copied);
            });
        } catch (RejectedExecutionException error) {
            resolveTool(call, ToolResult.err("Приложение закрывается"));
        }
    }

    private void safeBackground(Runnable task) {
        try {
            executor.execute(task);
        } catch (RejectedExecutionException ignored) {
            // плагин уже закрыт
        }
    }

    // ---------------------------------------------------------------- JS-методы

    private boolean isAgentEnabled() {
        return getContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_AGENT_ENABLED, true);
    }

    @PluginMethod
    public void setEnabled(PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        Boolean value = call.getBoolean("enabled");
        if (value == null) {
            call.reject("Не передан enabled", "ENABLED_REQUIRED");
            return;
        }
        getContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().putBoolean(KEY_AGENT_ENABLED, value).apply();
        call.resolve(new JSObject().put("enabled", value.booleanValue()));
    }

    @PluginMethod
    public void info(PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        try {
            call.resolve(new JSObject(PhoneToolsSystem.infoObject(getContext()).toString()));
        } catch (JSONException error) {
            call.resolve(new JSObject());
        }
    }

    @PluginMethod
    public void permissions(final PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        arm(call, 30_000, () -> settle(call, new JSObject()));
        safeBackground(() -> {
            JSObject payload;
            try {
                payload = permissionsPayload(requester.all());
            } catch (Throwable error) {
                payload = new JSObject();
            }
            settle(call, payload);
        });
    }

    @PluginMethod
    public void requestPermission(final PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        final String id = call.getString("permission", "");
        arm(call, USER_TIMEOUT_MS + 30_000, () -> settle(call, new JSObject().put("state", requester.state(id))));
        requester.request(id, state -> {
            safeBackground(this::checkPermissionsChanged);
            settle(call, new JSObject().put("state", state));
        });
    }

    @PluginMethod
    public void requestAll(final PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        arm(call, REQUEST_ALL_TIMEOUT_MS, () -> settle(call, permissionsPayload(requester.all())));
        requester.requestAll(permissions -> {
            safeBackground(this::checkPermissionsChanged);
            settle(call, permissionsPayload(permissions));
        });
    }

    @PluginMethod
    public void setAuth(final PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        // SERVER_ORIGIN: baseUrl из JS игнорируем — иначе токен можно было увести на чужой сервер.
        final String token = call.getString("token");
        final Context context = getContext();
        // EncryptedSharedPreferences/Keystore на главном потоке даёт рывки/ANR.
        arm(call, 20_000, () -> settle(call, new JSObject()));
        safeBackground(() -> {
            try {
                PhoneAuthStore.save(context, token);
            } catch (Throwable error) {
                Log.w(TAG, "Не удалось сохранить auth: " + error.getClass().getSimpleName());
            }
            settle(call, new JSObject());
        });
    }

    @PluginMethod
    public void getPushToken(final PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        final Context context = getContext();
        try {
            if (FirebaseApp.getApps(context).isEmpty()) {
                settle(call, tokenResult(null));
                return;
            }
            arm(call, 20_000, () -> settle(call, tokenResult(null)));
            FirebaseMessaging.getInstance().getToken().addOnCompleteListener(task -> {
                String token = null;
                try {
                    token = task.isSuccessful() ? task.getResult() : null;
                } catch (Throwable ignored) {
                    // токен не получен
                }
                if (token != null && !token.isEmpty()) {
                    context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                            .edit().putString(KEY_PUSH_TOKEN, token).apply();
                }
                settle(call, tokenResult(token));
            });
        } catch (Throwable error) {
            Log.w(TAG, "FCM недоступен: " + error.getClass().getSimpleName());
            settle(call, tokenResult(null));
        }
    }

    private static JSObject tokenResult(String token) {
        JSObject ret = new JSObject();
        ret.put("token", token == null ? (Object) JSONObject.NULL : token);
        return ret;
    }

    @PluginMethod
    public void showNotification(PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        Integer id = call.getInt("id");
        // NO_JS_CONFIRM: уведомления с кнопками «Разрешить/Отклонить» приходят только из FCM
        // (PhoneFirebaseService). Из JS — только информационные, чтобы нельзя было подделать запрос.
        String type = call.getString("type", "completed");
        if ("confirm".equals(type)) {
            type = "completed";
        }
        NotificationHelper.show(
                getContext(),
                id,
                type,
                call.getString("title", ""),
                call.getString("body", ""),
                null,
                call.getString("taskId"),
                call.getString("chatId"),
                Boolean.TRUE.equals(call.getBoolean("dangerous", false)));
        call.resolve();
    }

    @PluginMethod
    public void cancelNotification(PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        NotificationHelper.cancel(getContext(), call.getString("actionId"), call.getString("taskId"));
        call.resolve();
    }

    @PluginMethod
    public void getLaunchNotification(PluginCall call) {
        if (!OriginGuard.check(this, call)) {
            return;
        }
        JSObject data = launchNotification;
        launchNotification = null;
        call.resolve(data == null ? new JSObject() : data);
    }

}
