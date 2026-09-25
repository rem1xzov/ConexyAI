import { useEffect, useState, type FormEvent } from 'react';
import { useTranslation } from 'react-i18next';
import type { VerificationChallenge } from '../api/conexyApi';
import { CloseIcon, GitHubIcon } from './Icons';

export type AuthMode = 'login' | 'register';

interface AuthModalProps {
  mode: AuthMode;
  /**
   * Login, or the FIRST step of a sign-up. A sign-up resolves with the verification challenge
   * instead of a session: the password is stored server-side and a 6-digit code is on its way to the
   * address, so the form moves on to the code step (see EMAIL_VERIFICATION).
   */
  onSubmit: (email: string, password: string) => Promise<VerificationChallenge | void>;
  /** Confirms the emailed code — this is where the session actually appears. */
  onConfirmCode: (email: string, code: string) => Promise<void>;
  /** Asks for a fresh code; resolves with the cooldown the server enforces, in seconds. */
  onResendCode: (email: string) => Promise<number>;
  onSwitchMode: () => void;
  onClose: () => void;
}

function messageOf(err: unknown, fallback: string): string {
  return err instanceof Error ? err.message : fallback;
}

// EMAIL_AUTH: добавлено 2026-09-19
/**
 * Email/password login and registration with a GitHub OAuth fallback. Errors from the backend
 * ({ code, message }) are surfaced inline, not just logged to the console.
 *
 * EMAIL_VERIFICATION: registration runs in two steps — address + password, then the code that was
 * mailed to it. Until that code is confirmed there is no account, so this form is the only place the
 * second half of the sign-up can happen.
 */
export function AuthModal({ mode, onSubmit, onConfirmCode, onResendCode, onSwitchMode, onClose }: AuthModalProps) {
  const { t } = useTranslation();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  // EMAIL_VERIFICATION: шаг с кодом. Живёт здесь, а не в App: это часть формы, и при закрытии
  // модалки она должна исчезнуть вместе с ней.
  const [challenge, setChallenge] = useState<{ email: string } | null>(null);
  const [code, setCode] = useState('');
  const [cooldown, setCooldown] = useState(0);
  const [resending, setResending] = useState(false);

  const isRegister = mode === 'register';

  // Отсчёт до следующей отправки: сервер отвечает своим кулдауном, форма просто тикает.
  useEffect(() => {
    if (cooldown <= 0) return;
    const id = window.setTimeout(() => setCooldown((value) => value - 1), 1000);
    return () => window.clearTimeout(id);
  }, [cooldown]);

  async function handleSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (isRegister && password !== confirm) {
      setError(t('auth.passwordsMismatch'));
      return;
    }

    setLoading(true);
    try {
      const result = await onSubmit(email, password);
      if (result) {
        setChallenge({ email: result.email });
        setCode('');
        setCooldown(result.resendCooldownSeconds);
      }
      // A login has no second step: App closes the modal itself.
    } catch (err) {
      setError(messageOf(err, t('errors.default')));
    } finally {
      setLoading(false);
    }
  }

  async function submitCode(value: string) {
    if (!challenge || value.length !== 6) return;
    setError(null);
    setLoading(true);
    try {
      await onConfirmCode(challenge.email, value);
      // The session exists now — the modal has done its job.
      onClose();
    } catch (err) {
      // На неверный код сервер отдаёт, сколько попыток осталось; текст уже собран из этого числа.
      setError(messageOf(err, t('errors.default')));
      setCode('');
    } finally {
      setLoading(false);
    }
  }

  async function resend() {
    if (!challenge || cooldown > 0 || resending) return;
    setError(null);
    setResending(true);
    try {
      setCooldown(await onResendCode(challenge.email));
    } catch (err) {
      setError(messageOf(err, t('errors.default')));
    } finally {
      setResending(false);
    }
  }

  // Шаг 2: код из письма.
  if (challenge) {
    return (
      <div className="dialog-overlay" onMouseDown={onClose}>
        <div className="dialog-card auth-modal" role="dialog" aria-modal="true" onMouseDown={(e) => e.stopPropagation()}>
          <div className="auth-modal__head">
            <div className="dialog-title">{t('auth.verifyTitle')}</div>
            <button className="icon-btn" onClick={onClose} aria-label={t('common.close')} type="button">
              <CloseIcon size={18} />
            </button>
          </div>

          <div className="auth-modal__subtitle">{t('auth.verifySubtitle', { email: challenge.email })}</div>

          <form
            className="auth-modal__form"
            onSubmit={(e) => {
              e.preventDefault();
              void submitCode(code);
            }}
          >
            <input
              className="dialog-input auth-code__input"
              // Цифровая клавиатура на телефоне и подсказка «код из SMS/письма» в iOS/Android.
              inputMode="numeric"
              autoComplete="one-time-code"
              maxLength={6}
              value={code}
              onChange={(e) => {
                const next = e.target.value.replace(/\D/g, '').slice(0, 6);
                setCode(next);
                // Шесть цифр ввели — отправляем сами, кнопку жать не нужно.
                if (next.length === 6) void submitCode(next);
              }}
              autoFocus
              aria-label={t('auth.codeLabel')}
              placeholder="••••••"
            />

            {error && <div className="auth-modal__error">{error}</div>}

            <button className="dialog-btn dialog-btn--primary auth-modal__submit" type="submit" disabled={loading || code.length !== 6}>
              {loading ? t('auth.verifying') : t('auth.verifyButton')}
            </button>
          </form>

          {/* Обязательная подсказка: письмо чаще всего попадает в «Спам», и без неё пользователь
              ждёт код, который уже пришёл. */}
          <p className="auth-modal__hint">
            {cooldown > 0
              ? t('auth.spamHint', { seconds: cooldown })
              : t('auth.spamHintReady')}
          </p>

          <div className="auth-modal__actions">
            <button
              className="dialog-btn"
              type="button"
              onClick={() => void resend()}
              disabled={cooldown > 0 || resending || loading}
            >
              {cooldown > 0
                ? t('auth.resendIn', { seconds: cooldown })
                : resending
                  ? t('auth.resending')
                  : t('auth.resend')}
            </button>
            <button
              className="dialog-btn"
              type="button"
              onClick={() => {
                setChallenge(null);
                setCode('');
                setError(null);
                setCooldown(0);
              }}
            >
              {t('auth.changeEmail')}
            </button>
          </div>
        </div>
      </div>
    );
  }

  // Шаг 1: адрес и пароль.
  return (
    <div className="dialog-overlay" onMouseDown={onClose}>
      <div className="dialog-card auth-modal" role="dialog" aria-modal="true" onMouseDown={(e) => e.stopPropagation()}>
        <div className="auth-modal__head">
          <div className="dialog-title">{isRegister ? t('auth.registerTitle') : t('auth.loginTitle')}</div>
          <button className="icon-btn" onClick={onClose} aria-label={t('common.close')} type="button">
            <CloseIcon size={18} />
          </button>
        </div>

        <form className="auth-modal__form" onSubmit={handleSubmit}>
          <input
            className="dialog-input"
            type="email"
            placeholder={t('auth.email')}
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoFocus
            autoComplete="email"
          />
          <input
            className="dialog-input"
            type="password"
            placeholder={t('auth.password')}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete={isRegister ? 'new-password' : 'current-password'}
          />
          {isRegister && (
            <input
              className="dialog-input"
              type="password"
              placeholder={t('auth.confirmPassword')}
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
            {loading ? t('auth.waiting') : isRegister ? t('auth.registerButton') : t('auth.loginButton')}
          </button>
        </form>

        <div className="auth-modal__divider">
          <span>{t('auth.or')}</span>
        </div>

        <a className="auth-modal__github" href="/api/auth/github/login">
          <GitHubIcon size={18} /> {t('auth.loginWithGithub')}
        </a>

        <button className="auth-modal__switch" onClick={onSwitchMode} type="button">
          {isRegister ? t('auth.switchToLogin') : t('auth.switchToRegister')}
        </button>
      </div>
    </div>
  );
}
