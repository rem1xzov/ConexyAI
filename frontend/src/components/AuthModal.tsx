import { useState, type FormEvent } from 'react';
import { CloseIcon, GitHubIcon } from './Icons';

export type AuthMode = 'login' | 'register';

interface AuthModalProps {
  mode: AuthMode;
  onSubmit: (email: string, password: string) => Promise<void>;
  onSwitchMode: () => void;
  onClose: () => void;
}

// EMAIL_AUTH: добавлено 2026-09-19
/**
 * Email/password login/registration modal with a GitHub OAuth fallback. Errors from the
 * backend ({ code, message }) are surfaced inline, not just logged to the console.
 */
export function AuthModal({ mode, onSubmit, onSwitchMode, onClose }: AuthModalProps) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const isRegister = mode === 'register';

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (isRegister && password !== confirm) {
      setError('Пароли не совпадают');
      return;
    }

    setLoading(true);
    try {
      await onSubmit(email, password);
      // On success the parent closes the modal.
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Ошибка авторизации');
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="dialog-overlay" onMouseDown={onClose}>
      <div className="dialog-card auth-modal" role="dialog" aria-modal="true" onMouseDown={(e) => e.stopPropagation()}>
        <div className="auth-modal__head">
          <div className="dialog-title">{isRegister ? 'Регистрация' : 'Вход'}</div>
          <button className="icon-btn" onClick={onClose} aria-label="Закрыть" type="button">
            <CloseIcon size={18} />
          </button>
        </div>

        <form className="auth-modal__form" onSubmit={handleSubmit}>
          <input
            className="dialog-input"
            type="email"
            placeholder="Email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoFocus
            autoComplete="email"
          />
          <input
            className="dialog-input"
            type="password"
            placeholder="Пароль"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete={isRegister ? 'new-password' : 'current-password'}
          />
          {isRegister && (
            <input
              className="dialog-input"
              type="password"
              placeholder="Подтвердите пароль"
              value={confirm}
              onChange={(e) => setConfirm(e.target.value)}
              autoComplete="new-password"
            />
          )}

          {error && <div className="auth-modal__error">{error}</div>}

          <button
            className="dialog-btn dialog-btn--primary auth-modal__submit"
            type="submit"
            disabled={loading || !email.trim() || !password}
          >
            {loading ? 'Подождите…' : isRegister ? 'Зарегистрироваться' : 'Войти'}
          </button>
        </form>

        <div className="auth-modal__divider">
          <span>или</span>
        </div>

        <a className="auth-modal__github" href="/api/auth/github/login">
          <GitHubIcon size={18} /> Войти через GitHub
        </a>

        <button className="auth-modal__switch" onClick={onSwitchMode} type="button">
          {isRegister ? 'Уже есть аккаунт? Войти' : 'Нет аккаунта? Зарегистрироваться'}
        </button>
      </div>
    </div>
  );
}
