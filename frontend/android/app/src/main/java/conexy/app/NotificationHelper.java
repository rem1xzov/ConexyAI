package conexy.app;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.Bundle;
import android.service.notification.StatusBarNotification;

import androidx.core.app.NotificationCompat;
import androidx.core.app.NotificationManagerCompat;

/** Каналы и уведомления агента: подтверждения, «задача готова», «нужен телефон». */
final class NotificationHelper {
    static final String CHANNEL_TASKS = "conexy_tasks";
    static final String CHANNEL_DONE = "conexy_done";

    static final String ACTION_APPROVE = "conexy.app.action.APPROVE";
    static final String ACTION_DENY = "conexy.app.action.DENY";
    static final String ACTION_OPEN = "conexy.app.action.NOTIFICATION_OPEN";

    static final String EXTRA_FROM_NOTIFICATION = "conexy_from_notification";
    static final String EXTRA_TYPE = "conexy_type";
    static final String EXTRA_TASK_ID = "conexy_task_id";
    static final String EXTRA_CHAT_ID = "conexy_chat_id";
    static final String EXTRA_ACTION_ID = "conexy_action_id";
    static final String EXTRA_DANGEROUS = "conexy_dangerous";
    static final String EXTRA_NOTIF_TAG = "conexy_notif_tag";
    static final String EXTRA_NOTIF_ID = "conexy_notif_id";

    private NotificationHelper() {}

    static void ensureChannels(Context context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            return;
        }
        NotificationManager manager = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
        if (manager == null) {
            return;
        }
        NotificationChannel tasks = new NotificationChannel(
                CHANNEL_TASKS,
                context.getString(R.string.channel_tasks_name),
                NotificationManager.IMPORTANCE_HIGH);
        tasks.setDescription(context.getString(R.string.channel_tasks_description));
        manager.createNotificationChannel(tasks);

        NotificationChannel done = new NotificationChannel(
                CHANNEL_DONE,
                context.getString(R.string.channel_done_name),
                NotificationManager.IMPORTANCE_DEFAULT);
        done.setDescription(context.getString(R.string.channel_done_description));
        manager.createNotificationChannel(done);
    }

    private static int flags() {
        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            flags |= PendingIntent.FLAG_IMMUTABLE;
        }
        return flags;
    }

    private static Intent openIntent(Context context, String type, String actionId, String taskId, String chatId,
            boolean dangerous) {
        Intent intent = new Intent(context, MainActivity.class);
        intent.setAction(ACTION_OPEN);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_SINGLE_TOP
                | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        intent.putExtra(EXTRA_FROM_NOTIFICATION, true);
        intent.putExtra(EXTRA_TYPE, type);
        if (actionId != null) {
            intent.putExtra(EXTRA_ACTION_ID, actionId);
        }
        if (taskId != null) {
            intent.putExtra(EXTRA_TASK_ID, taskId);
        }
        if (chatId != null) {
            intent.putExtra(EXTRA_CHAT_ID, chatId);
        }
        intent.putExtra(EXTRA_DANGEROUS, dangerous);
        return intent;
    }

    private static String nz(String value) {
        return value == null || value.trim().isEmpty() ? null : value;
    }

    static void show(Context context, Integer explicitId, String type, String title, String body,
            String actionId, String taskId, String chatId, boolean dangerous) {
        try {
            doShow(context.getApplicationContext(), explicitId, type, title, body, actionId, taskId, chatId,
                    dangerous);
        } catch (Throwable error) {
            android.util.Log.w("NotificationHelper", "Не удалось показать уведомление: " + error.getClass().getSimpleName());
        }
    }

    private static void doShow(Context context, Integer explicitId, String type, String title, String body,
            String actionId, String taskId, String chatId, boolean dangerous) {
        if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) {
            return;
        }
        NotificationManager manager = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
        if (manager == null) {
            return;
        }
        ensureChannels(context);

        if (type == null || !("confirm".equals(type) || "phone_needed".equals(type))) {
            type = "completed";
        }
        actionId = nz(actionId);
        taskId = nz(taskId);
        chatId = nz(chatId);
        boolean confirm = "confirm".equals(type) && actionId != null;

        String tag;
        if (actionId != null) {
            tag = "action:" + actionId;
        } else if (taskId != null) {
            tag = "task:" + type + ":" + taskId;
        } else {
            tag = "misc:" + type;
        }
        int id = explicitId != null ? explicitId : (tag.hashCode() & 0x7fffffff);

        String channel = "completed".equals(type) ? CHANNEL_DONE : CHANNEL_TASKS;
        String safeTitle = nz(title) != null ? title : context.getString(R.string.app_name);
        String safeBody = body == null ? "" : body;

        Bundle extras = new Bundle();
        if (actionId != null) {
            extras.putString(EXTRA_ACTION_ID, actionId);
        }
        if (taskId != null) {
            extras.putString(EXTRA_TASK_ID, taskId);
        }

        NotificationCompat.Builder builder = new NotificationCompat.Builder(context, channel)
                .setSmallIcon(R.drawable.ic_stat_conexy)
                .setContentTitle(safeTitle)
                .setContentText(safeBody)
                .setStyle(new NotificationCompat.BigTextStyle().bigText(safeBody))
                .setAutoCancel(true)
                .setCategory(confirm ? NotificationCompat.CATEGORY_MESSAGE : NotificationCompat.CATEGORY_STATUS)
                .setPriority("completed".equals(type)
                        ? NotificationCompat.PRIORITY_DEFAULT
                        : NotificationCompat.PRIORITY_HIGH)
                .addExtras(extras);

        PendingIntent content = PendingIntent.getActivity(
                context, id, openIntent(context, type, actionId, taskId, chatId, dangerous), flags());
        builder.setContentIntent(content);

        if (confirm) {
            // Опасное действие одним нажатием из шторки не одобряется: «Разрешить» открывает приложение.
            PendingIntent approve;
            if (dangerous) {
                approve = PendingIntent.getActivity(
                        context, id * 3 + 1, openIntent(context, type, actionId, taskId, chatId, true), flags());
            } else {
                approve = PendingIntent.getBroadcast(
                        context, id * 3 + 1, actionIntent(context, ACTION_APPROVE, actionId, tag, id), flags());
            }
            PendingIntent deny = PendingIntent.getBroadcast(
                    context, id * 3 + 2, actionIntent(context, ACTION_DENY, actionId, tag, id), flags());
            builder.addAction(0, context.getString(R.string.notif_allow), approve);
            builder.addAction(0, context.getString(R.string.notif_deny), deny);
        }

        Notification notification = builder.build();
        manager.notify(tag, id, notification);
    }

    private static Intent actionIntent(Context context, String action, String actionId, String tag, int id) {
        Intent intent = new Intent(context, ActionReceiver.class);
        intent.setAction(action);
        intent.putExtra(EXTRA_ACTION_ID, actionId);
        intent.putExtra(EXTRA_NOTIF_TAG, tag);
        intent.putExtra(EXTRA_NOTIF_ID, id);
        return intent;
    }

    /** Убирает уведомления по подтверждению и/или задаче. */
    static void cancel(Context context, String actionId, String taskId) {
        try {
            NotificationManager manager = (NotificationManager) context.getSystemService(Context.NOTIFICATION_SERVICE);
            if (manager == null) {
                return;
            }
            actionId = nz(actionId);
            taskId = nz(taskId);
            if (actionId == null && taskId == null) {
                return;
            }
            for (StatusBarNotification active : manager.getActiveNotifications()) {
                Bundle extras = active.getNotification().extras;
                if (extras == null) {
                    continue;
                }
                boolean match = (actionId != null && actionId.equals(extras.getString(EXTRA_ACTION_ID)))
                        || (taskId != null && taskId.equals(extras.getString(EXTRA_TASK_ID)));
                if (match) {
                    manager.cancel(active.getTag(), active.getId());
                }
            }
        } catch (Throwable ignored) {
            // Уведомление уже убрано или система не отдаёт список.
        }
    }
}
