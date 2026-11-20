import http from './client';

// GOAL: добавлено 2026-11-20 — долгоживущая цель чата (раунды запускает сервер, агент закрывает её).
export type AgentGoalStatus = 'active' | 'paused' | 'blocked' | 'complete';

export interface AgentGoal {
  id: string;
  chatId: string;
  objective: string;
  status: AgentGoalStatus;
  roundsStarted: number;
  maxRounds: number;
  blockedReason?: string | null;
  lastNote?: string | null;
  createdAt: string;
  updatedAt: string;
}

/** Активная цель чата; null — цели нет (сервер отвечает 404). */
export async function getActiveGoal(chatId: string): Promise<AgentGoal | null> {
  try {
    const { data } = await http.get<AgentGoal>(`/goals/${chatId}`);
    return data;
  } catch (e) {
    if ((e as { response?: { status?: number } }).response?.status === 404) return null;
    throw e;
  }
}

export async function startGoal(chatId: string, objective: string, maxRounds: number): Promise<AgentGoal> {
  const { data } = await http.post<AgentGoal>(`/goals/${chatId}`, { objective, maxRounds });
  return data;
}

export async function setGoalStatus(
  chatId: string,
  status: AgentGoalStatus,
  reason?: string,
): Promise<AgentGoal> {
  const { data } = await http.patch<AgentGoal>(`/goals/${chatId}`, { status, reason });
  return data;
}

export async function deleteGoal(chatId: string): Promise<void> {
  await http.delete(`/goals/${chatId}`);
}
