import { useState } from 'react';
import type { SubscriptionUsage } from '../types/api';

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
function formatCount(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1).replace(/\.0$/, '')}M`;
  if (n >= 1_000) return `${Math.round(n / 1000)}k`;
  return String(n);
}

function formatDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' });
}

function clampPct(n: number): number {
  return Math.max(0, Math.min(100, n));
}

interface UsageIndicatorProps {
  usage: SubscriptionUsage | null;
}

/**
 * Circular (donut) usage indicator for the Agent tab, showing the agent token budget
 * (e.g. "27% · 268k/1M"). Clicking toggles a breakdown of all tier limits with reset dates.
 */
export function UsageIndicator({ usage }: UsageIndicatorProps) {
  const [open, setOpen] = useState(false);

  if (!usage) return null;

  // ADMIN_UNLIMITED: добавлено 2026-09-19 — admins have no limits; show a badge instead of
  // a percentage.
  if (usage.tier === 'Admin') {
    return (
      <div className="usage-indicator">
        <span className="usage-indicator__admin" title="Администратор — безлимит">
          ∞ Admin
        </span>
      </div>
    );
  }

  const pct = clampPct(usage.agentLimit > 0 ? (usage.agentUsed / usage.agentLimit) * 100 : 0);
  const radius = 15;
  const circumference = 2 * Math.PI * radius;
  const offset = circumference * (1 - pct / 100);

  const rows = [
    { label: 'Flash', used: usage.flashUsed, limit: usage.flashLimit, resetsAt: usage.flashResetsAt },
    { label: 'Pro', used: usage.proUsed, limit: usage.proLimit, resetsAt: usage.proResetsAt },
    { label: 'Agent', used: usage.agentUsed, limit: usage.agentLimit, resetsAt: usage.agentResetsAt },
  ];

  return (
    <div className="usage-indicator">
      <button
        className="usage-indicator__donut"
        onClick={() => setOpen((o) => !o)}
        title={`${formatCount(usage.agentUsed)} / ${formatCount(usage.agentLimit)} токенов`}
        aria-label="Использование лимитов"
        type="button"
      >
        <svg width="36" height="36" viewBox="0 0 36 36">
          <circle cx="18" cy="18" r={radius} fill="none" stroke="rgba(255,255,255,0.12)" strokeWidth="4" />
          <circle
            cx="18"
            cy="18"
            r={radius}
            fill="none"
            stroke="var(--accent)"
            strokeWidth="4"
            strokeLinecap="round"
            strokeDasharray={circumference}
            strokeDashoffset={offset}
            transform="rotate(-90 18 18)"
          />
        </svg>
        <span className="usage-indicator__text">{Math.round(pct)}%</span>
      </button>

      {open && (
        <div className="usage-indicator__popover">
          <div className="usage-indicator__tier">Тариф: {usage.tier}</div>
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
                  <div className="usage-indicator__bar-fill" style={{ width: `${rpct}%` }} />
                </div>
                <div className="usage-indicator__reset">Сброс: {formatDate(r.resetsAt)}</div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
