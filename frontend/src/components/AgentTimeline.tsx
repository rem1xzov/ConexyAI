import { useTranslation } from 'react-i18next';
import type { AgentStep } from '../types/chat';
import type { CommandDecisionHandler, ToolActionEvent } from '../types/signalr';
import { ActionStepLine, type ActionKind, type ActionState } from './ActionStepLine';
import { CommandConfirmCard } from './CommandConfirmCard';
import { ThinkingStep } from './ThinkingStep';

// AGENT_FEED_ZED: переписано 2026-09-23
// Формат ленты повторяет Zed AI:
//   * размышление — лёгкий сворачиваемый заголовок «Размышления» с лампочкой, без счётчика;
//   * действия (поиск, чтение, правка файлов) — ОДНА плотная строка с иконкой, без рамки;
//   * полноразмерный блок остался только у команд, которым нужно решение пользователя, — там
//     реально есть что скрывать (команда и её вывод), поэтому там рамка и уместна.
// Раньше каждая команда получала свою полноразмерную карточку, и лента превращалась в столбик
// одинаковых блоков.

const MAX_VISIBLE = 40;

interface RowUnit {
  kind: 'row';
  id: string;
  firstActionIndex: number;
  action: ActionKind;
  label: string;
  path?: string;
  state: ActionState;
  detail?: string;
  errorLine?: string;
  // READABLE_RESULT: краткий итог (exit code) прямо в строке, чтобы связка «команда → результат»
  // читалась без раскрытия, как в терминальном блоке.
  meta?: string;
}

interface StepUnit {
  kind: 'step';
  id: string;
  label: string;
  /** Reasoning text produced during this step (falls back to the backend label). */
  text: string;
  startedAt: number;
  endedAt?: number;
}

interface CardUnit {
  kind: 'card';
  id: string;
  firstActionIndex: number;
  events: ToolActionEvent[];
}

type Unit = RowUnit | StepUnit | CardUnit;

type Translate = (key: string, options?: Record<string, unknown>) => string;

interface AgentTimelineProps {
  actions: ToolActionEvent[];
  steps?: AgentStep[];
  /** Full reasoning stream of the message; each step shows its own slice of it. */
  thinking?: string;
  /** Inline decision handler for commands that require approval; omit for a read-only view. */
  onCommandDecision?: CommandDecisionHandler;
  /** Live status shown when no tool row or card covers the current moment. */
  statusPill?: { label: string } | null;
  /** Opens a path from an action row in the workspace editor. */
  onOpenPath?: (path: string) => void;
}

/** Events sharing a pending action id are one command awaiting the user's decision. */
function isApprovalCommand(event: ToolActionEvent): boolean {
  return event.toolName === 'bash' && event.pendingActionId != null;
}

function pairKey(event: ToolActionEvent): string {
  return `${event.toolName}|${event.path}|${event.command}`;
}

function stateFrom(event: ToolActionEvent): ActionState {
  if (event.status === 'completed') return 'completed';
  if (event.status === 'failed') return 'failed';
  if (event.status === 'rejected') return 'rejected';
  return 'running';
}

interface ActionDescriptor {
  action: ActionKind;
  /** The short line itself, e.g. "Читаю" or "Поиск: StudentsMode". */
  label: string;
  /** File the action touched, when there is one worth linking. */
  path?: string;
}

/** Host of a URL ("https://www.example.com/a" -> "example.com"); the raw text when it is not a URL. */
function urlHost(value: string): string {
  try {
    const host = new URL(value).host;
    return host.replace(/^www\./, '') || value;
  } catch {
    return value;
  }
}

/**
 * Turns a raw tool invocation into the short description the reference timeline uses
 * ("Read ConexyAgentRunner.cs" instead of the whole shell line where that is obvious).
 */
function describeAction(event: ToolActionEvent, t: Translate): ActionDescriptor {
  const command = (event.command ?? '').trim();

  if (event.toolName === 'str_replace_editor') {
    const read = command === 'view';
    return {
      action: read ? 'read_file' : 'edit_file',
      label: read ? t('toolPill.readFile') : t('toolPill.editFile'),
      path: event.path,
    };
  }

  if (event.toolName === 'search_documents') {
    return { action: 'search', label: `${t('toolPill.searchDocs')}: ${command}` };
  }

  if (event.toolName === 'web_search') {
    return { action: 'web', label: `${t('toolPill.searchWeb')}: ${command}` };
  }

  // DEEP_RESEARCH: добавлено 2026-09-24 — агент читает страницу из выдачи (command = URL);
  // в строке показываем только хост, полный адрес длинный и ничего не добавляет.
  if (event.toolName === 'fetch_web_page') {
    return { action: 'web', label: t('render.toolFetchPage', { host: urlHost(command) }) };
  }

  // CHAT_SEARCH: добавлено 2026-09-24 — поиск по прошлым диалогам пользователя (command = запрос).
  if (event.toolName === 'search_user_chats') {
    return { action: 'search', label: t('render.toolSearchChats', { query: command }) };
  }

  if (event.toolName === 'bash' || event.toolName === 'terminal_exec') {
    const read = /^(?:cat|head|tail|bat)\s+(?:-[^\s]+\s+)*(\S+)\s*$/.exec(command);
    if (read) return { action: 'read_file', label: t('toolPill.readFile'), path: read[1] };
    if (/^(?:ls|dir|tree)\b/.test(command))
      return { action: 'list', label: `${t('toolPill.listFiles')}: ${command}` };
    if (/^(?:grep|rg|ag|find|fd)\b/.test(command))
      return { action: 'search', label: `${t('toolPill.search')}: ${command}` };
    return { action: 'command', label: command };
  }

  return { action: 'command', label: event.summary || `${event.toolName} ${command}`.trim() };
}

function rowFrom(event: ToolActionEvent, id: string, actionIndex: number, t: Translate): RowUnit {
  const { action, label, path } = describeAction(event, t);
  return {
    kind: 'row',
    id,
    firstActionIndex: actionIndex,
    action,
    label,
    path,
    state: stateFrom(event),
    detail: event.output,
    errorLine: event.status === 'failed' ? event.summary : undefined,
    meta: event.status === 'completed' ? event.summary : undefined,
  };
}

/**
 * Builds the ordered timeline: reasoning steps and tool rows interleaved by how many tool events
 * had already been emitted when each step started, so the order always matches what really
 * happened instead of grouping all reasoning at the top.
 */
function buildUnits(
  actions: ToolActionEvent[],
  steps: AgentStep[],
  thinking: string,
  t: Translate,
): Unit[] {
  const tail = actions.slice(-MAX_VISIBLE);
  const offset = actions.length - tail.length;

  const units: Unit[] = [];
  const openRows = new Map<string, RowUnit>();
  const cards = new Map<string, CardUnit>();

  tail.forEach((action, i) => {
    const actionIndex = offset + i;

    if (isApprovalCommand(action)) {
      const key = action.pendingActionId!;
      let card = cards.get(key);
      if (!card) {
        card = { kind: 'card', id: `card-${key}`, firstActionIndex: actionIndex, events: [] };
        cards.set(key, card);
        units.push(card);
      }
      card.events.push(action);
      return;
    }

    const key = pairKey(action);
    if (action.status === 'started') {
      const row = rowFrom(action, `${key}#${actionIndex}`, actionIndex, t);
      units.push(row);
      openRows.set(key, row);
      return;
    }

    const open = openRows.get(key);
    if (open) {
      open.state = stateFrom(action);
      open.detail = action.output ?? open.detail;
      open.errorLine = open.state === 'failed' ? action.summary : undefined;
      open.meta = open.state === 'completed' ? action.summary : undefined;
      openRows.delete(key);
      return;
    }

    // A completion whose "started" twin fell outside the visible tail.
    units.push(rowFrom(action, `${key}#${actionIndex}`, actionIndex, t));
  });

  for (const step of steps) {
    // AGENT_FEED_ZED: each step owns the slice of the reasoning stream that was produced while it
    // was the open step, so the expanded block shows real reasoning text instead of the generic
    // backend label. Older/restored steps without offsets fall back to the label.
    const from = step.reasoningFrom ?? 0;
    const slice = thinking.slice(from, step.reasoningTo);
    const stepUnit: StepUnit = {
      kind: 'step',
      id: `step-${step.id}`,
      label: step.label,
      text: slice.trim() || step.label,
      startedAt: step.startedAt,
      endedAt: step.endedAt,
    };
    const before = units.findIndex(
      (u) => u.kind !== 'step' && u.firstActionIndex >= step.afterToolCount,
    );
    if (before < 0) units.push(stepUnit);
    else units.splice(before, 0, stepUnit);
  }

  return units;
}

export function AgentTimeline({
  actions,
  steps = [],
  thinking = '',
  onCommandDecision,
  statusPill,
  onOpenPath,
}: AgentTimelineProps) {
  const { t } = useTranslation();

  const units = buildUnits(actions, steps, thinking, t);

  const lastUnit = units[units.length - 1];
  // The live status row is only useful when nothing else is already showing activity.
  const showStatusRow =
    Boolean(statusPill) &&
    loadingNothing(units) &&
    !(lastUnit && lastUnit.kind === 'step' && lastUnit.endedAt === undefined);

  if (units.length === 0 && !showStatusRow) return null;

  return (
    <div className="agent-timeline">
      {units.map((unit) => {
        if (unit.kind === 'step') {
          const running = unit.endedAt === undefined;
          return <ThinkingStep key={unit.id} text={unit.text} running={running} />;
        }

        if (unit.kind === 'card') {
          return (
            <CommandConfirmCard
              key={unit.id}
              events={unit.events}
              onDecision={onCommandDecision}
            />
          );
        }

        return (
          <ActionStepLine
            key={unit.id}
            kind={unit.action}
            label={unit.label}
            path={unit.path}
            onOpenPath={onOpenPath}
            state={unit.state}
            meta={unit.meta}
            errorLine={unit.errorLine}
            detail={unit.detail}
          />
        );
      })}

      {showStatusRow && statusPill && (
        <div className="tl-step tl-step--running" role="status">
          <span className="tl-step__pulse" aria-hidden="true" />
          <span className="tl-step__label">{statusPill.label}</span>
        </div>
      )}
    </div>
  );
}

/** True when no unit is currently running — used to avoid stacking two "working" indicators. */
function loadingNothing(units: Unit[]): boolean {
  return !units.some((u) => {
    if (u.kind === 'row') return u.state === 'running';
    if (u.kind === 'card')
      return !u.events.some(
        (e) => e.status === 'completed' || e.status === 'failed' || e.status === 'rejected',
      );
    return u.endedAt === undefined;
  });
}
