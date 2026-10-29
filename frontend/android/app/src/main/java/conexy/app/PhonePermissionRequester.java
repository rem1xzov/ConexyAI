package conexy.app;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;

import androidx.activity.ComponentActivity;
import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;

import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

/**
 * Последовательный запрос разрешений: системные диалоги (ActivityResultRegistry) и экраны настроек
 * с ожиданием возврата в приложение ({@link #onResume()}). Колбэки вызываются в главном потоке,
 * каждый запрос гарантированно завершается (есть тайм-ауты).
 */
final class PhonePermissionRequester {
    private static final long RUNTIME_TIMEOUT_MS = 120_000;
    private static final long SETTINGS_TIMEOUT_MS = 300_000;

    interface ActivitySource {
        Activity activity();

        Context context();
    }

    private final ActivitySource source;
    private final Handler main = new Handler(Looper.getMainLooper());
    private int counter;
    private Runnable resumeWaiter;

    PhonePermissionRequester(ActivitySource source) {
        this.source = source;
    }

    private Context stateContext() {
        Activity activity = source.activity();
        return activity != null ? activity : source.context();
    }

    String state(String id) {
        return PhonePermissions.state(stateContext(), id);
    }

    JSONObject all() {
        return PhonePermissions.all(stateContext());
    }

    /** Вызывать из handleOnResume плагина. */
    void onResume() {
        main.post(() -> {
            Runnable waiter = resumeWaiter;
            if (waiter != null) {
                resumeWaiter = null;
                // Небольшая задержка: система успевает обновить состояние (например, службу доступности).
                main.postDelayed(waiter, 500);
            }
        });
    }

    void request(final String id, final Callback<String> done) {
        main.post(() -> requestOnMain(id, done));
    }

    private void requestOnMain(final String id, final Callback<String> doneRaw) {
        final AtomicBoolean finished = new AtomicBoolean(false);
        final Callback<String> done = value -> {
            if (finished.compareAndSet(false, true)) {
                doneRaw.on(value);
            }
        };
        try {
            if (!PhonePermissions.isKnown(id)) {
                done.on("unavailable");
                return;
            }
            String current = state(id);
            if ("granted".equals(current) || "unavailable".equals(current)) {
                done.on(current);
                return;
            }
            String[] runtime = PhonePermissions.runtimePermissions(id);
            if (runtime != null) {
                launchRuntime(id, runtime, done);
            } else {
                openSettingsAndWait(id, done);
            }
        } catch (Throwable error) {
            done.on(state(id));
        }
    }

    private void launchRuntime(final String id, String[] permissions, final Callback<String> done) {
        Activity activity = source.activity();
        if (!(activity instanceof ComponentActivity)) {
            done.on(state(id));
            return;
        }
        ComponentActivity componentActivity = (ComponentActivity) activity;
        final String key = "conexy_phone_perm_" + (counter++);
        final AtomicReference<ActivityResultLauncher<String[]>> ref = new AtomicReference<>();
        final Runnable timeout = () -> {
            ActivityResultLauncher<String[]> launcher = ref.getAndSet(null);
            if (launcher != null) {
                launcher.unregister();
            }
            done.on(state(id));
        };
        ActivityResultLauncher<String[]> launcher = componentActivity.getActivityResultRegistry().register(
                key,
                new ActivityResultContracts.RequestMultiplePermissions(),
                result -> {
                    main.removeCallbacks(timeout);
                    ActivityResultLauncher<String[]> l = ref.getAndSet(null);
                    if (l != null) {
                        l.unregister();
                    }
                    PhonePermissions.markAsked(source.context(), id);
                    done.on(state(id));
                });
        ref.set(launcher);
        main.postDelayed(timeout, RUNTIME_TIMEOUT_MS);
        PhonePermissions.markAsked(source.context(), id);
        launcher.launch(permissions);
    }

    private void openSettingsAndWait(final String id, final Callback<String> done) {
        Context context = source.context();
        Intent intent = PhonePermissions.settingsIntent(context, id);
        if (intent == null) {
            done.on(state(id));
            return;
        }
        // Если предыдущий запрос ещё ждёт возврата — завершаем его, иначе его done никогда не вызовется.
        Runnable previous = resumeWaiter;
        resumeWaiter = null;
        if (previous != null) {
            previous.run();
        }
        final Runnable waiter = new Runnable() {
            @Override
            public void run() {
                main.removeCallbacks(this);
                done.on(state(id));
            }
        };
        resumeWaiter = waiter;
        // done имеет once-защиту, поэтому тайм-аут безопасен даже если waiter уже был вызван.
        main.postDelayed(() -> {
            if (resumeWaiter == waiter) {
                resumeWaiter = null;
            }
            waiter.run();
        }, SETTINGS_TIMEOUT_MS);
        try {
            context.startActivity(intent);
        } catch (Exception error) {
            if (!startFallback(context, id)) {
                if (resumeWaiter == waiter) {
                    resumeWaiter = null;
                }
                waiter.run();
            }
        }
    }

    /** Запасной экран, если нужной страницы у производителя нет. */
    private boolean startFallback(Context context, String id) {
        try {
            Intent fallback;
            if ("accessibility".equals(id) || "usage_access".equals(id)) {
                fallback = new Intent("accessibility".equals(id)
                        ? Settings.ACTION_ACCESSIBILITY_SETTINGS
                        : Settings.ACTION_USAGE_ACCESS_SETTINGS);
            } else {
                fallback = new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                        Uri.parse("package:" + context.getPackageName()));
            }
            fallback.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            context.startActivity(fallback);
            return true;
        } catch (Throwable error) {
            return false;
        }
    }

    /**
     * Последовательно запрашивает всё недостающее: сначала runtime-диалоги, затем экраны настроек.
     * Окончательно отказанные runtime-разрешения пропускаются, чтобы не зависать на безответных диалогах.
     */
    void requestAll(final Callback<JSONObject> done) {
        main.post(() -> {
            final List<String> queue = new ArrayList<>();
            for (String id : PhonePermissions.RUNTIME_IDS) {
                if ("prompt".equals(state(id))) {
                    queue.add(id);
                }
            }
            for (String id : PhonePermissions.SPECIAL_IDS) {
                if ("prompt".equals(state(id))) {
                    queue.add(id);
                }
            }
            runQueue(queue, 0, done);
        });
    }

    private void runQueue(final List<String> queue, final int index, final Callback<JSONObject> done) {
        if (index >= queue.size()) {
            done.on(all());
            return;
        }
        request(queue.get(index), value -> runQueue(queue, index + 1, done));
    }
}
