// VOICE_DUP_FIX: добавлено 2026-09-27

/** Слово в виде, пригодном для сравнения: без регистра и окружающей пунктуации. */
function compareWord(word: string): string {
  return word.toLowerCase().replace(/^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$/gu, '');
}

/** Слова как их распознал движок — именно они попадают в поле ввода. */
function rawWords(text: string): string[] {
  return text.trim().split(/\s+/).filter(Boolean);
}

/**
 * Склеивает уже записанный текст с новым распознанным куском, убирая перекрытие.
 *
 * Речевой движок после перезапуска сессии часто расшифровывает ТУ ЖЕ фразу заново целиком, поэтому
 * начало нового куска совпадает с хвостом записанного. Простое дописывание давало «лесенку»
 * «Привет», «Привет как», «Привет как дела» — одну фразу, повторённую несколько раз.
 *
 * Три случая:
 *  • весь записанный текст — префикс нового куска → это его перерасшифровка, берём новый кусок;
 *  • новый кусок целиком уже есть в хвосте → не добавляем ничего;
 *  • частичное совпадение по словам → дописываем только неповторяющуюся часть.
 */
export function mergeTranscript(existing: string, addition: string): string {
  const existingWords = rawWords(existing);
  const additionWords = rawWords(addition);
  if (additionWords.length === 0) return existing.trim();
  if (existingWords.length === 0) return addition.trim();

  const a = existingWords.map(compareWord);
  const b = additionWords.map(compareWord);
  const max = Math.min(a.length, b.length);

  // Максимальное перекрытие: суффикс записанного == префикс нового куска.
  let overlap = 0;
  for (let size = max; size > 0; size--) {
    let matches = true;
    for (let i = 0; i < size; i++) {
      if (a[a.length - size + i] !== b[i]) {
        matches = false;
        break;
      }
    }
    if (matches) {
      overlap = size;
      break;
    }
  }

  if (overlap === 0) return `${existing.trim()} ${addition.trim()}`.trim();
  // Перерасшифровка той же фразы с начала: записанное полностью повторяется в новом куске.
  if (overlap === existingWords.length) return addition.trim();
  // Новый кусок целиком уже был в конце записанного — дубликат.
  if (overlap === additionWords.length) return existing.trim();
  return `${existing.trim()} ${additionWords.slice(overlap).join(' ')}`.trim();
}
