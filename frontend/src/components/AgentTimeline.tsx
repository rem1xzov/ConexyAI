import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { AgentStep } from '../types/chat';
import type { CommandDecisionHandler, ToolActionEvent } from '../types/signalr';
import { CommandConfirmCard } from './CommandConfirmCard';
import { DurationBadge } from './StepDuration';

// AGENT_TIMELINE: добавлено 2026-09-22
// Раньше лента агента была столбиком одинаковых полноразмерных карточек «Запуск команды» — по
// одной на КАЖДУЮ команду, — между которыми терялся текст рассуждений. Теперь:
//   * read-only команды (их считает бэкенд) вообще не требуют подтверждения и рисуются одной
//     компактной строкой таймлайна;
//   * каждый шаг размышления — отдельная компактная строка со своим таймером;
//   * полноразмерная карточка с «Разрешить/Отклонить» осталась только для команд, которым
//     действительно нужно решение пользователя (мутирующие, установка, удаление, долгие).

const MAX_VISIBLE = 40;

type RowStatus = 'running' | 'completed' | 'failed' | 'rejected';

interface RowUnit {
  kind: 'row';
  id: string;
  firstActionIndex: number;
  icon: string;
  label: string;
  status: RowStatus;
  detail?: string;
  errorLine?: string;
  // READABLE_RESULT: добавлено 2026-09-22 — краткий итог (exit code) прямо в строке, чтобы связка
  // «команда → результат» читалась без раскрытия, как в терминальном блоке.
  meta?: string;
}

interface StepUnit {
  kind: 'step';
  id: string;
  label: string;
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
  /** Inline decision handler for commands that require approval; omit for a read-only view. */
  onCommandDecision?: CommandDecisionHandler;
  /** Live status shown when no tool row or card covers the current moment. */
  statusPill?: { label: string } | null;
}

/** Events sharing a pending action id are one command awaiting the user's decision. */
function isApprovalCommand(event: ToolActionEvent): boolean {
  return event.toolName === 'bash' && event.pendingActionId != null;
}

function pairKey(event: ToolActionEvent): string {
  return `${event.toolName}|${event.path}|${event.command}`;
}

function statusFrom(event: ToolActionEvent): RowStatus {
  if (event.status === 'completed') return 'completed';
  if (event.status === 'failed') return 'failed';
  if (event.status === 'rejected') return 'rejected';
  return 'running';
}

function toolIcon(toolName: string, command: string): string {
  if (toolName === 'bash' || toolName === 'terminal_exec') return '⚡';
  if (toolName === 'search_documents') return '🔍';
  if (toolName === 'web_search') return '🌐';
  if (toolName === 'str_replace_editor') {
    switch (command) {
      case 'view':
        return '👁';
      case 'create':
        return '📄';
      case 'str_replace':
      case 'insert':
        return '🔧';
      case 'undo':
        return '↩';
      default:
        return '🔧';
    }
  }
  return '🔧';
}

/**
 * Turns a raw tool invocation into the short human description the reference timeline uses
 * ("Read ConexyAgentRunner.cs" instead of the whole shell line where that is obvious).
 */
function rowLabel(event: ToolActionEvent, t: Translate): string {
  const command = (event.command ?? '').trim();

  if (event.toolName === 'str_replace_editor') {
    const verb = command === 'view' ? t('toolPill.readFile') : t('toolPill.editFile');
    return `${verb} ${event.path}`;
  }

  if (event.toolName === 'search_documents') return `${t('toolPill.searchDocs')}: ${command}`;
  if (event.toolName === 'web_search') return `${t('toolPill.searchWeb')}: ${command}`;

  if (event.toolName === 'bash' || event.toolName === 'terminal_exec') {
    const read = /^(?:cat|head|tail|bat)\s+(?:-[^\s]+\s+)*(\S+)\s*$/.exec(command);
    if (read) return `${t('toolPill.readFile')} ${read[1]}`;
    if (/^(?:ls|dir|tree)\b/.test(command)) return `${t('toolPill.listFiles')}: ${command}`;
    if (/^(?:grep|rg|ag|find|fd)\b/.test(command)) return `${t('toolPill.search')}: ${command}`;
    return command;
  }

  return event.summary || `${event.toolName} ${command}`.trim();
}

function statusMark(status: RowStatus) {
  if (status === 'completed') return <span className="tl-row__mark chat-ok">✓</span>;
  if (status === 'rejected') return <span className="tl-row__mark chat-danger">⊘</span>;
  if (status === 'failed') return <span className="tl-row__mark chat-danger">✕</span>;
  return <span className="tl-row__spinner" aria-hidden="true" />;
}

/**
 * Builds the ordered timeline: reasoning steps and tool rows interleaved by how many tool events
 * had already been emitted when each step started, so the order always matches what really
 * happened instead of grouping all reasoning at the top.
 */
function buildUnits(actions: ToolActionEvent[], steps: AgentStep[], t: Translate): Unit[] {
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
      const row: RowUnit = {
        kind: 'row',
        id: `${key}#${actionIndex}`,
        firstActionIndex: actionIndex,
        icon: toolIcon(action.toolName, action.command),
        label: rowLabel(action, t),
        status: 'running',
      };
      units.push(row);
      openRows.set(key, row);
      return;
    }

    const open = openRows.get(key);
    if (open) {
      open.status = statusFrom(action);
      open.detail = action.output ?? open.detail;
      open.errorLine = open.status === 'failed' ? action.summary : undefined;
      open.meta = open.status === 'completed' ? action.summary : undefined;
      openRows.delete(key);
      return;
    }

    // A completion whose "started" twin fell outside the visible tail.
    units.push({
      kind: 'row',
      id: `${key}#${actionIndex}`,
      firstActionIndex: actionIndex,
      icon: toolIcon(action.toolName, action.command),
      label: rowLabel(action, t),
      status: statusFrom(action),
      detail: action.output,
      errorLine: action.status === 'failed' ? action.summary : undefined,
      meta: action.status === 'completed' ? action.summary : undefined,
    });
  });

  for (const step of steps) {
    const stepUnit: StepUnit = {
      kind: 'step',
      id: `step-${step.id}`,
      label: step.label,
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

export function AgentTimeline({ actions, steps = [], onCommandDecision, statusPill }: AgentTimelineProps) {
  const { t } = useTranslation();
  const [openRows, setOpenRows] = useState<Record<string, boolean>>({});

  const units = buildUnits(actions, steps, t);

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
          return (
            <div key={unit.id} className={`tl-step ${running ? 'tl-step--running' : ''}`}>
              {running ? <span className="tl-step__pulse" aria-hidden="true" /> : <span className="tl-step__dot" aria-hidden="true" />}
              <span className="tl-step__label">{unit.label}</span>
              <DurationBadge startedAt={unit.startedAt} endedAt={unit.endedAt} />
            </div>
          );
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

        const open = Boolean(openRows[unit.id]);
        const expandable = Boolean(unit.detail);
        const content = (
          <>
            <span className="tl-row__icon" aria-hidden="true">{unit.icon}</span>
            <span className="tl-row__label" title={unit.label}>{unit.label}</span>
            {unit.errorLine && <span className="tl-row__error">{unit.errorLine}</span>}
            {unit.meta && <span className="tl-row__meta">{unit.meta}</span>}
            {statusMark(unit.status)}
            {expandable && <span className={`tl-row__chevron ${open ? 'tl-row__chevron--open' : ''}`}>▾</span>}
          </>
        );

        return (
          <div key={unit.id} className={`tl-row tl-row--${unit.status}`}>
            {expandable ? (
              <button
                type="button"
                className="tl-row__head"
                onClick={() => setOpenRows((prev) => ({ ...prev, [unit.id]: !prev[unit.id] }))}
                aria-expanded={open}
              >
                {content}
              </button>
            ) : (
              <div className="tl-row__head tl-row__head--static">{content}</div>
            )}
            {expandable && open && <pre className="tl-row__body">{unit.detail}</pre>}
          </div>
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
    if (u.kind === 'row') return u.status === 'running';
    if (u.kind === 'card') return !u.events.some((e) => e.status === 'completed' || e.status === 'failed' || e.status === 'rejected');
    return u.endedAt === undefined;
  });
}
