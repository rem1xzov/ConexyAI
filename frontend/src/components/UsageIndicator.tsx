import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { SubscriptionUsage } from '../types/api';

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
function formatCount(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1).replace(/\.0$/, '')}M`;
  if (n >= 1_000) return `${Math.round(n / 1000)}k`;
  return String(n);
}

function formatDate(iso: string, lang: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleDateString(lang === 'en' ? 'en-US' : 'ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' });
}

function clampPct(n: number): number {
  return Math.max(0, Math.min(100, n));
}

// TOKEN_RING: добавлено 2026-09-27 — пороги расхода. До 70% цвет темы, 70–90% янтарный,
// от 90% красный: цвет кольца должен читаться раньше цифры, потому что видно его издалека.
const WARN_PCT = 70;
const DANGER_PCT = 90;

function ringColor(pct: number): string {
  if (pct >= DANGER_PCT) return 'var(--danger)';
  if (pct >= WARN_PCT) return 'var(--warn)';
  return 'var(--accent)';
}

interface UsageIndicatorProps {
  usage: SubscriptionUsage | null;
}

/**
 * Token ring meter: the agent token budget of the current period as a ring plus a compact
 * `used / limit` label. Hovering shows the exact numbers and the reset date; clicking opens the
 * breakdown of every pool (flash, pro, agent, and cowork where the mode is included).
 */
export function UsageIndicator({ usage }: UsageIndicatorProps) {
  const { t, i18n } = useTranslation();
  const [open, setOpen] = useState(false);
  // USAGE_CLICK_OUTSIDE: поповер лимитов должен закрываться кликом/тапом в любом месте экрана, а не
  // только повторным нажатием на кольцо. Слушаем `pointerdown` (покрывает мышь и тач) и закрываем,
  // если цель вне блока с кольцом и поповером.
  const rootRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (e: PointerEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('pointerdown', onPointerDown);
    return () => document.removeEventListener('pointerdown', onPointerDown);
  }, [open]);

  if (!usage) return null;

  // ADMIN_UNLIMITED: добавлено 2026-09-19 — admins have no limits; show a badge instead of
  // a percentage.
  if (usage.tier === 'Admin') {
    return (
      <div className="usage-indicator">
        <span className="usage-indicator__admin" title={t('usage.adminTitle')}>
          {t('usage.admin')}
        </span>
      </div>
    );
  }

  const pct = clampPct(usage.agentLimit > 0 ? (usage.agentUsed / usage.agentLimit) * 100 : 0);
  // LIMIT_LOCK: бюджет выбран полностью — это состояние, а не просто «почти 100%».
  const exhausted = usage.agentLimit > 0 && usage.agentUsed >= usage.agentLimit;
  const color = ringColor(pct);
  const radius = 15;
  const circumference = 2 * Math.PI * radius;
  const offset = circumference * (1 - pct / 100);

  const rows = [
    { label: 'Flash', used: usage.flashUsed, limit: usage.flashLimit, resetsAt: usage.flashResetsAt },
    { label: 'Pro', used: usage.proUsed, limit: usage.proLimit, resetsAt: usage.proResetsAt },
    { label: 'Agent', used: usage.agentUsed, limit: usage.agentLimit, resetsAt: usage.agentResetsAt },
    // COWORK_BUDGET: строка появляется только там, где режим реально входит в тариф (limit > 0) —
    // на Free это была бы бессмысленная «0 / 0».
    ...(usage.coworkLimit > 0
      ? [{ label: 'Cowork', used: usage.coworkUsed, limit: usage.coworkLimit, resetsAt: usage.coworkResetsAt }]
      : []),
  ];

  return (
    <div className="usage-indicator" ref={rootRef}>
      <button
        className="usage-indicator__donut"
        onClick={() => setOpen((o) => !o)}
        // Точные числа и дата сброса — в подсказке, а не только в раскрытом списке.
        title={t('usage.tooltip', {
          used: usage.agentUsed.toLocaleString(i18n.language === 'en' ? 'en-US' : 'ru-RU'),
          limit: usage.agentLimit.toLocaleString(i18n.language === 'en' ? 'en-US' : 'ru-RU'),
          percent: Math.round(pct),
          date: formatDate(usage.agentResetsAt, i18n.language),
        })}
        aria-label={t('usage.aria')}
        aria-pressed={open}
        type="button"
      >
        <svg width="36" height="36" viewBox="0 0 36 36">
          <circle cx="18" cy="18" r={radius} fill="none" stroke="var(--border)" strokeWidth="4" />
          <circle
            cx="18"
            cy="18"
            r={radius}
            fill="none"
            stroke={color}
            strokeWidth="4"
            strokeLinecap="round"
            strokeDasharray={circumference}
            strokeDashoffset={offset}
            transform="rotate(-90 18 18)"
          />
        </svg>
        <span className={`usage-indicator__text ${exhausted ? 'usage-indicator__text--exhausted' : ''}`}>
          {exhausted ? '!' : `${Math.round(pct)}%`}
        </span>
      </button>

      {/* Компактная подпись рядом с кольцом: «140k / 200k». На узких экранах её скрывает CSS —
          там остаётся кольцо и подсказка. */}
      <span className="usage-indicator__amount" title={t('usage.tokens', { used: formatCount(usage.agentUsed), limit: formatCount(usage.agentLimit) })}>
        {exhausted
          ? t('usage.exhausted')
          : `${formatCount(usage.agentUsed)} / ${formatCount(usage.agentLimit)}`}
      </span>

      {open && (
        <div className="usage-indicator__popover">
          <div className="usage-indicator__tier">{t('usage.tier', { tier: usage.tier })}</div>
          {rows.map((r) => {
            const rpct = clampPct(r.limit > 0 ? (r.used / r.limit) * 100 : 0);
            return (
              <div key={r.label} className="usage-indicator__row">
                <div className="usage-indicator__row-head">
                  <span className="usage-indicator__label">{r.label}</span>
                  <span className="usage-indicator__value">
                    {formatCount(r.used)} / {formatCount(r.limit)}
                  </span>
                </div>
                <div className="usage-indicator__bar">
                  <div
                    className="usage-indicator__bar-fill"
                    style={{ width: `${rpct}%`, background: ringColor(rpct) }}
                  />
                </div>
                <div className="usage-indicator__reset">{t('usage.reset', { date: formatDate(r.resetsAt, i18n.language) })}</div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
