import { Component, type ErrorInfo, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

// ADMIN_ERROR_BOUNDARY: добавлено 2026-09-23
/**
 * Catches render errors in a subtree and shows a readable screen instead of nothing.
 *
 * Why it exists: an uncaught render error makes React unmount the whole tree, which on a dark theme
 * looks exactly like "the page died" — that is the black screen reported on `/#/admin`. This
 * boundary turns any such crash into a message with a way out, and it prints the real error text, so
 * the next report names the failing component instead of just "black screen".
 *
 * It is deliberately a class component: only class components can implement
 * `getDerivedStateFromError`/`componentDidCatch` (React has no hook equivalent).
 */
interface ErrorBoundaryProps {
  children: ReactNode;
  /** Label for the escape button ("Back to chat", "Back", ...). */
  backLabel: string;
  onBack?: () => void;
}

interface ErrorBoundaryState {
  error: Error | null;
}

export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // Full stack stays in the console for diagnosis; the UI shows the message and the component hint.
    console.error('[ErrorBoundary] render failed:', error, info.componentStack);
  }

  render() {
    const { error } = this.state;
    if (!error) return this.props.children;

    return (
      <ErrorFallback
        error={error}
        backLabel={this.props.backLabel}
        onBack={this.props.onBack}
      />
    );
  }
}

function ErrorFallback({ error, backLabel, onBack }: { error: Error; backLabel: string; onBack?: () => void }) {
  const { t } = useTranslation();

  return (
    <div className="crash-screen">
      <div className="crash-screen__box">
        <h1 className="crash-screen__title">{t('errors.renderTitle')}</h1>
        <p className="crash-screen__hint">{t('errors.renderHint')}</p>
        <pre className="crash-screen__detail">{error.message}</pre>
        {onBack && (
          <button className="admin-btn" type="button" onClick={onBack}>
            {backLabel}
          </button>
        )}
      </div>
    </div>
  );
}
