package conexy.app;

import android.Manifest;
import android.content.Intent;
import android.os.Bundle;
import android.speech.RecognitionListener;
import android.speech.RecognizerIntent;
import android.speech.SpeechRecognizer;
import android.util.Log;

import com.getcapacitor.JSObject;
import com.getcapacitor.PermissionState;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.CapacitorPlugin;
import com.getcapacitor.annotation.Permission;
import com.getcapacitor.annotation.PermissionCallback;

import java.util.ArrayList;

/**
 * VOICE_INPUT: в Android WebView нет Web Speech API — {@code window.SpeechRecognition} там
 * отсутствует, поэтому голосовой ввод в приложении отвечал «не поддерживается», сколько бы
 * разрешений ни выдал пользователь. Распознаём нативно через {@link SpeechRecognizer} и отдаём
 * текст в JS теми же событиями, что и браузерный распознаватель.
 *
 * <p>Одна сессия распознавания живёт до паузы в речи; склейку и перезапуск держит клиент
 * (InputBar), как и для браузерного API.
 */
@CapacitorPlugin(
        name = "SpeechInput",
        permissions = {
            @Permission(alias = "microphone", strings = { Manifest.permission.RECORD_AUDIO })
        })
public class SpeechInputPlugin extends Plugin {

    private static final String TAG = "SpeechInput";
    private static final String DEFAULT_LANG = "ru-RU";

    private SpeechRecognizer recognizer;
    private boolean destroyed;

    @PluginMethod
    public void start(PluginCall call) {
        if (getPermissionState("microphone") != PermissionState.GRANTED) {
            requestPermissionForAlias("microphone", call, "microphonePermission");
            return;
        }
        startRecognition(call);
    }

    @PermissionCallback
    private void microphonePermission(PluginCall call) {
        if (getPermissionState("microphone") != PermissionState.GRANTED) {
            call.reject("PERMISSION_DENIED", "Микрофон запрещён в настройках приложения");
            return;
        }
        startRecognition(call);
    }

    @PluginMethod
    public void stop(PluginCall call) {
        // stopListening доводит уже сказанное до onResults — так пользователь не теряет
        // последнюю фразу, когда сам выключает микрофон.
        runOnUiThread(() -> {
            if (recognizer == null) return;
            try {
                recognizer.stopListening();
            } catch (Exception error) {
                Log.w(TAG, "stopListening failed", error);
            }
        });
        call.resolve();
    }

    @PluginMethod
    public void cancel(PluginCall call) {
        runOnUiThread(this::destroyRecognizer);
        call.resolve();
    }

    @Override
    protected void handleOnDestroy() {
        runOnUiThread(this::destroyRecognizer);
        super.handleOnDestroy();
    }

    private void startRecognition(PluginCall call) {
        String lang = call.getString("lang", DEFAULT_LANG);
        runOnUiThread(() -> {
            if (!SpeechRecognizer.isRecognitionAvailable(getContext())) {
                call.reject("NO_RECOGNIZER", "На устройстве нет сервиса распознавания речи");
                return;
            }

            destroyRecognizer();
            destroyed = false;

            try {
                recognizer = SpeechRecognizer.createSpeechRecognizer(getContext());
                recognizer.setRecognitionListener(new Listener());

                Intent intent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH);
                intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM);
                intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE, lang);
                intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_PREFERENCE, lang);
                intent.putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true);
                intent.putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 1);
                intent.putExtra(RecognizerIntent.EXTRA_CALLING_PACKAGE, getContext().getPackageName());

                recognizer.startListening(intent);
                call.resolve();
            } catch (Exception error) {
                Log.w(TAG, "cannot start recognition", error);
                destroyRecognizer();
                call.reject("START_FAILED", "Не удалось запустить распознавание", error);
            }
        });
    }

    private void destroyRecognizer() {
        destroyed = true;
        if (recognizer == null) return;
        try {
            recognizer.cancel();
        } catch (Exception error) {
            Log.w(TAG, "cancel failed", error);
        }
        try {
            recognizer.destroy();
        } catch (Exception error) {
            Log.w(TAG, "destroy failed", error);
        }
        recognizer = null;
    }

    private void runOnUiThread(Runnable action) {
        if (getActivity() != null) {
            getActivity().runOnUiThread(action);
        } else {
            action.run();
        }
    }

    private void emitResult(Bundle bundle, boolean isFinal) {
        String text = bestResult(bundle);
        JSObject result = new JSObject();
        result.put("text", text);
        result.put("isFinal", isFinal);
        notifyListeners("result", result);
    }

    private static String bestResult(Bundle bundle) {
        if (bundle == null) return "";
        ArrayList<String> matches = bundle.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION);
        if (matches == null || matches.isEmpty()) return "";
        String best = matches.get(0);
        return best == null ? "" : best;
    }

    /** Коды ошибок в терминах Web Speech API: клиент уже умеет их показывать. */
    private static String errorCode(int error) {
        switch (error) {
            case SpeechRecognizer.ERROR_NO_MATCH:
            case SpeechRecognizer.ERROR_SPEECH_TIMEOUT:
                return "no-speech";
            case SpeechRecognizer.ERROR_INSUFFICIENT_PERMISSIONS:
                return "permission";
            case SpeechRecognizer.ERROR_AUDIO:
            case SpeechRecognizer.ERROR_RECOGNIZER_BUSY:
                return "busy";
            case SpeechRecognizer.ERROR_NETWORK:
            case SpeechRecognizer.ERROR_NETWORK_TIMEOUT:
                return "network";
            case SpeechRecognizer.ERROR_CLIENT:
                return "aborted";
            case SpeechRecognizer.ERROR_SERVER:
                return "server";
            default:
                return "failed";
        }
    }

    private class Listener implements RecognitionListener {

        @Override
        public void onPartialResults(Bundle partialResults) {
            if (destroyed) return;
            emitResult(partialResults, false);
        }

        @Override
        public void onResults(Bundle results) {
            emitResult(results, true);
            notifyListeners("end", new JSObject());
        }

        @Override
        public void onError(int error) {
            Log.w(TAG, "recognition error " + error);
            notifyListeners("error", new JSObject().put("code", errorCode(error)));
            notifyListeners("end", new JSObject());
        }

        @Override
        public void onReadyForSpeech(Bundle params) {}

        @Override
        public void onBeginningOfSpeech() {}

        @Override
        public void onRmsChanged(float rmsdB) {}

        @Override
        public void onBufferReceived(byte[] buffer) {}

        @Override
        public void onEndOfSpeech() {}

        @Override
        public void onEvent(int eventType, Bundle params) {}
    }
}
