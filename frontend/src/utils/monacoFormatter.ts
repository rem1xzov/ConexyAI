import type { MonacoApi } from './monacoLsp';
import { FORMATTER_LANGUAGES, formatCode, formatterSupports } from './formatter';

// FORMATTER: добавлено 2026-10-05 — регистрирует в Monaco провайдер форматирования документа
// (context menu → Format Document, и Command Palette) поверх Prettier. Shift+Alt+F дополнительно
// перехвачен в CodeEditor, чтобы Prettier гарантированно побеждал встроенный форматтер Monaco.

const registeredInstances = new WeakSet<object>();

function fileNameFromUri(uri: { toString(): string }): string {
  const raw = uri.toString();
  const last = raw.split('/').pop() ?? '';
  try {
    return decodeURIComponent(last);
  } catch {
    return last;
  }
}

interface FormatModel {
  uri: { toString(): string };
  getValue(): string;
  getFullModelRange(): unknown;
}

export function registerFormattingProvider(monaco: MonacoApi): void {
  if (registeredInstances.has(monaco)) return;
  registeredInstances.add(monaco);

  for (const language of FORMATTER_LANGUAGES) {
    monaco.languages.registerDocumentFormattingEditProvider(language, {
      provideDocumentFormattingEdits: async (model: FormatModel) => {
        const path = fileNameFromUri(model.uri);
        if (!formatterSupports(path)) return null;

        const source = model.getValue();
        const formatted = await formatCode(path, source);
        if (formatted == null || formatted === source) return [];

        return [{ range: model.getFullModelRange(), text: formatted }];
      },
    } as never);
  }
}
