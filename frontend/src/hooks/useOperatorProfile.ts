import { useCallback, useEffect, useState } from 'react';
import { getOperatorProfile } from '../api/legalApi';
import type { OperatorProfile } from '../types/api';

// LEGAL_DOCS: добавлено 2026-09-26
/**
 * Реквизиты оператора для страниц `#/privacy`, `#/offer` и `#/refund`.
 *
 * Кэш живёт на уровне модуля: документы открывают по одному за раз (часто вообще в новой вкладке),
 * но при переходе между ними повторный запрос не нужен. Ошибка кэш НЕ заполняет — её можно повторить
 * кнопкой, и тогда запрос уйдёт заново.
 */
let cache: OperatorProfile | null = null;
let inFlight: Promise<OperatorProfile> | null = null;

function load(): Promise<OperatorProfile> {
  if (cache) return Promise.resolve(cache);

  // Одновременный запрос двух страниц не должен превращаться в два запроса.
  inFlight ??= getOperatorProfile()
    .then((profile) => {
      cache = profile;
      return profile;
    })
    .finally(() => {
      inFlight = null;
    });

  return inFlight;
}

export function useOperatorProfile() {
  const [operator, setOperator] = useState<OperatorProfile | null>(cache);
  const [failed, setFailed] = useState(false);
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let cancelled = false;
    setFailed(false);

    load()
      .then((profile) => {
        if (!cancelled) setOperator(profile);
      })
      .catch(() => {
        if (!cancelled) setFailed(true);
      });

    return () => {
      cancelled = true;
    };
  }, [attempt]);

  const reload = useCallback(() => {
    cache = null;
    setAttempt((value) => value + 1);
  }, []);

  return { operator, failed, reload };
}
