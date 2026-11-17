// LIMIT_LOCK: добавлено 2026-11-17 — какой именно пул лимитов относится к выбранной модели.
// Flash/Pro расходуют запросы, Coder — токены агента, Cowork — свой пул запросов. Экран блокировки
// должен говорить о том пуле, в который упёрся пользователь, а не показывать общий «лимит».
import type { ConexyModel, SubscriptionUsage } from '../types/api';

export type LimitPoolKey = 'flash' | 'pro' | 'coder' | 'cowork';

export interface LimitPoolState {
  key: LimitPoolKey;
  used: number;
  limit: number;
  resetsAt: string;
}

// ADMIN_UNLIMITED: бэкенд отдаёт админам лимит как long.MaxValue. В JS такое число теряет точность,
// поэтому всё, что выше 1e15, считаем бесконечностью.
export const INFINITE_LIMIT = 1e15;

export function isInfiniteLimit(limit: number): boolean {
  return limit >= INFINITE_LIMIT;
}

/** Пул лимитов, из которого тратится выбранная модель. */
export function limitPoolForModel(
  usage: SubscriptionUsage | null,
  model: ConexyModel,
): LimitPoolState | null {
  if (!usage) return null;
  switch (model) {
    case 'ConexyV1-flash':
      return { key: 'flash', used: usage.flashUsed, limit: usage.flashLimit, resetsAt: usage.flashResetsAt };
    case 'ConexyV1-pro':
      return { key: 'pro', used: usage.proUsed, limit: usage.proLimit, resetsAt: usage.proResetsAt };
    case 'conexy-cowork':
      return { key: 'cowork', used: usage.coworkUsed, limit: usage.coworkLimit, resetsAt: usage.coworkResetsAt };
    case 'conexy-coder':
    default:
      return { key: 'coder', used: usage.agentUsed, limit: usage.agentLimit, resetsAt: usage.agentResetsAt };
  }
}

/**
 * True, когда пул входит в тариф и выбран полностью. Пул с нулевым лимитом означает, что режим не
 * входит в тариф (Cowork на Free) — это не «кончился лимит», а другой сценарий (предложение тарифа).
 */
export function isLimitPoolExhausted(pool: LimitPoolState | null): boolean {
  if (!pool) return false;
  if (pool.limit <= 0 || isInfiniteLimit(pool.limit)) return false;
  return pool.used >= pool.limit;
}
