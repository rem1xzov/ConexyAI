package conexy.app;

import androidx.annotation.NonNull;

import com.google.firebase.messaging.FirebaseMessagingService;
import com.google.firebase.messaging.RemoteMessage;

import java.util.Map;

/** FCM data-сообщения: {@code type} = confirm / completed / phone_needed / dismiss. */
public class PhoneFirebaseService extends FirebaseMessagingService {

    @Override
    public void onNewToken(@NonNull String token) {
        super.onNewToken(token);
        PhoneAgentPlugin.dispatchPushToken(token);
    }

    @Override
    public void onMessageReceived(@NonNull RemoteMessage message) {
        super.onMessageReceived(message);
        Map<String, String> data = message.getData();
        if (data == null || data.isEmpty()) {
            return;
        }
        String type = data.get("type");
        if ("dismiss".equals(type)) {
            NotificationHelper.cancel(getApplicationContext(), data.get("actionId"), null);
            return;
        }
        boolean dangerous = "true".equalsIgnoreCase(data.get("dangerous"));
        NotificationHelper.show(
                getApplicationContext(),
                null,
                type,
                data.get("title"),
                data.get("body"),
                data.get("actionId"),
                data.get("taskId"),
                data.get("chatId"),
                dangerous);
    }
}
