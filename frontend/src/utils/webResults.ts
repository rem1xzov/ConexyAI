// DEEPSEEK_SEARCH_UI: источники для шторки «Результаты поиска».
//
// Сервер отдаёт выдачу web_search текстом для модели (см. DeepSeekSearchService / JsonSeoSearchService):
//   1. Заголовок
//      URL: https://…
//      Date: 2026-06-01T00:00:00+00:00   (необязательно)
//      Выжимка…
// Отсюда и собираем карточки; формат держим совместимым с обоими провайдерами.

export interface WebResult {
  title: string;
  url: string;
  host: string;
  date?: string;
  snippet: string;
}

export function hostOf(url: string): string {
  try {
    return new URL(url).host.replace(/^www\./, '');
  } catch {
    return url;
  }
}

/** Разбирает текстовую выдачу одного web_search. Нераспознанный текст даёт пустой список. */
export function parseSearchOutput(output: string | undefined): WebResult[] {
  if (!output) return [];
  const results: WebResult[] = [];
  let current: WebResult | null = null;
  for (const raw of output.split(/\r?\n/)) {
    const line = raw.trim();
    const head = /^(\d+)\.\s+(.*)$/.exec(line);
    if (head && !raw.startsWith(' ')) {
      if (current?.url) results.push(current);
      current = { title: head[2], url: '', host: '', snippet: '' };
      continue;
    }
    if (!current || !line) continue;
    const url = /^URL:\s*(\S+)/.exec(line);
    if (url) {
      current.url = url[1];
      current.host = hostOf(url[1]);
      continue;
    }
    const date = /^Date:\s*(.+)$/.exec(line);
    if (date) {
      current.date = date[1];
      continue;
    }
    current.snippet = current.snippet ? `${current.snippet} ${line}` : line;
  }
  if (current?.url) results.push(current);
  return results;
}

/** Все источники группы поисков без повторов, в порядке появления. */
export function collectResults(outputs: (string | undefined)[]): WebResult[] {
  const seen = new Set<string>();
  const all: WebResult[] = [];
  for (const output of outputs) {
    for (const result of parseSearchOutput(output)) {
      const key = result.url.replace(/[#/]+$/, '').toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      all.push(result);
    }
  }
  return all;
}

/** "2026-06-01T00:00:00+00:00" -> "2026/06/01"; непонятная дата — как есть. */
export function shortDate(value: string | undefined): string | undefined {
  if (!value) return undefined;
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  return match ? `${match[1]}/${match[2]}/${match[3]}` : value;
}
