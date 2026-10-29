package conexy.app;

/** Минимальный колбэк (java.util.function недоступен на minSdk 23 без desugaring). */
interface Callback<T> {
    void on(T value);
}
