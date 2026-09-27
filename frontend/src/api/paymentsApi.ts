import http from './client';
import type { CreatePaymentResponse, PaymentStatusResponse } from '../types/api';

// YOOKASSA: добавлено 2026-09-27
/**
 * Платежи за тарифы. Сумма берётся на сервере из каталога планов, поэтому клиент присылает только
 * идентификатор плана (Pro / ProMax / ProMaxAnnual / Go) — подделать цену нельзя.
 */

/** Создаёт платёж и отдаёт ссылку на оплату в ЮKassa. */
export async function createPayment(plan: string): Promise<CreatePaymentResponse> {
  const { data } = await http.post<CreatePaymentResponse>('/payments', { plan });
  return data;
}

/** Текущий статус своего платежа — для окна «платёж обрабатывается». */
export async function getPaymentStatus(paymentId: string): Promise<PaymentStatusResponse> {
  const { data } = await http.get<PaymentStatusResponse>(`/payments/${paymentId}`);
  return data;
}
