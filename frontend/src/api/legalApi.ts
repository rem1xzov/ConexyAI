import http from './client';
import type { OperatorProfile } from '../types/api';

// LEGAL_DOCS: добавлено 2026-09-26
/**
 * Реквизиты оператора для публичных правовых документов. Эндпоинт анонимный: те же данные и так
 * напечатаны на самих страницах политики, оферты и политики возврата.
 */
export async function getOperatorProfile(): Promise<OperatorProfile> {
  const { data } = await http.get<OperatorProfile>('/api/legal/operator');
  return data;
}
