package conexy.app;

/** Результат инструмента {@code phone_*}: текст для модели и признак ошибки. */
final class ToolResult {
    final String output;
    final boolean isError;

    private ToolResult(String output, boolean isError) {
        this.output = output == null ? "" : output;
        this.isError = isError;
    }

    static ToolResult ok(String output) {
        return new ToolResult(output, false);
    }

    static ToolResult err(String output) {
        return new ToolResult(output, true);
    }
}
