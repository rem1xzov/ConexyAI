import { useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import type { VerificationChallenge } from '../api/conexyApi';
// LEGAL_DOCS: добавлено 2026-09-25 — путь документа не дублируется строкой, а берётся из общего места.
import { LEGAL_PATHS } from './legal/LegalPage';
import { CloseIcon, EyeIcon, EyeOffIcon, GitHubIcon } from './Icons';

export type AuthMode = 'login' | 'register';

interface AuthModalProps {
  mode: AuthMode;
  /**
   * Login, or the FIRST step of a sign-up. A sign-up resolves with the verification challenge
   * instead of a session: the password is stored server-side and a 6-digit code is on its way to the
   * address, so the form moves on to the code step (see EMAIL_VERIFICATION).
   *
   * PRIVACY_POLICY: <paramref name="acceptedPolicy"/> carries the consent tick; the backend refuses
   * to start a sign-up without it, so the flag has to travel with the credentials.
   */
  onSubmit: (email: string, password: string, acceptedPolicy: boolean) => Promise<VerificationChallenge | void>;
  /** Confirms the emailed code — this is where the session actually appears. */
  onConfirmCode: (email: string, code: string) => Promise<void>;
  /** Asks for a fresh code; resolves with the cooldown the server enforces, in seconds. */
  onResendCode: (email: string) => Promise<number>;
  // PASSWORD_RESET: добавлено 2026-09-26
  /** Starts a reset: checks the address exists and mails a code. Resolves with the resend cooldown. */
  onRequestReset: (email: string) => Promise<number>;
  /** Confirms the reset code together with the new password; the session appears here. */
  onResetPassword: (email: string, code: string, newPassword: string) => Promise<void>;
  onSwitchMode: () => void;
  onClose: () => void;
}

/** Which step of the modal is on screen. The sign-up code step is tracked separately (see `challenge`). */
type View = 'credentials' | 'forgot' | 'reset';

function messageOf(err: unknown, fallback: string): string {
  return err instanceof Error ? err.message : fallback;
}

// PASSWORD_EYE: добавлено 2026-09-26
/**
 * A password input with a show/hide button. The eye is there on phones as much as on desktops: on an
 * on-screen keyboard a typo in a masked password is almost impossible to spot, and the whole form
 * fails because of one wrong character.
 */
function PasswordField({
  value,
  onChange,
  placeholder,
  autoComplete,
  visible,
  onToggle,
  autoFocus,
}: {
  value: string;
  onChange: (value: string) => void;
  placeholder: string;
  autoComplete: string;
  visible: boolean;
  onToggle: () => void;
  autoFocus?: boolean;
}) {
  const { t } = useTranslation();

  return (
    <div className="password-field">
      <input
        className="dialog-input password-field__input"
        type={visible ? 'text' : 'password'}
        placeholder={placeholder}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        autoComplete={autoComplete}
        autoFocus={autoFocus}
      />
      <button
        className="password-field__toggle"
        // type="button": внутри <form> кнопка по умолчанию отправляет её, а эта только переключает видимость.
        type="button"
        onClick={onToggle}
        aria-label={visible ? t('auth.hidePassword') : t('auth.showPassword')}
        aria-pressed={visible}
        title={visible ? t('auth.hidePassword') : t('auth.showPassword')}
      >
        {visible ? <EyeOffIcon size={16} /> : <EyeIcon size={16} />}
      </button>
    </div>
  );
}

// EMAIL_AUTH: добавлено 2026-09-19
/**
 * Email/password login and registration with a GitHub OAuth fallback. Errors from the backend
 * ({ code, message }) are surfaced inline, not just logged to the console.
 *
 * EMAIL_VERIFICATION: registration runs in two steps — address + password, then the code that was
 * mailed to it. Until that code is confirmed there is no account, so this form is the only place the
 * second half of the sign-up can happen.
 *
 * PASSWORD_RESET: the forgot-password flow lives here too, as two more steps of the same form —
 * address first (the server says at once whether it knows it), then the code together with the new
 * password, so the mail can be in flight while the password is being typed.
 */
export function AuthModal({
  mode,
  onSubmit,
  onConfirmCode,
  onResendCode,
  onRequestReset,
  onResetPassword,
  onSwitchMode,
  onClose,
}: AuthModalProps) {
  const { t } = useTranslation();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  // PRIVACY_POLICY: согласие с Политикой обработки персональных данных. Обязательно при регистрации:
  // без галочки кнопка неактивна и форма не уходит на сервер.
  const [accepted, setAccepted] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  // PASSWORD_EYE: видимость каждого пароля переключается отдельно — «показать» одно поле не должно
  // раскрывать соседнее.
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);
  const [showNewPassword, setShowNewPassword] = useState(false);

  // PASSWORD_RESET: шаги «забыли пароль» и «новый пароль».
  const [view, setView] = useState<View>('credentials');
  const [forgotEmail, setForgotEmail] = useState('');
  const [resetEmail, setResetEmail] = useState('');
  const [resetCode, setResetCode] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [newConfirm, setNewConfirm] = useState('');

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

    // PRIVACY_POLICY: сервер не знает про галочку, поэтому это последний барьер перед запросом.
    if (isRegister && !accepted) {
      setError(t('auth.policyRequired'));
      return;
    }

    setLoading(true);
    try {
      const result = await onSubmit(email, password, accepted);
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

  async function resend(target: string) {
    if (cooldown > 0 || resending) return;
    setError(null);
    setResending(true);
    try {
      setCooldown(await onResendCode(target));
    } catch (err) {
      setError(messageOf(err, t('errors.default')));
    } finally {
      setResending(false);
    }
  }

  // PASSWORD_RESET: шаг 1 — адрес. Сервер сразу говорит, есть ли такой аккаунт, поэтому опечатку
  // видно до того, как пользователь придумает новый пароль.
  async function handleForgot(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setLoading(true);
    try {
      const seconds = await onRequestReset(forgotEmail);
      setResetEmail(forgotEmail);
      setResetCode('');
      setNewPassword('');
      setNewConfirm('');
      setShowNewPassword(false);
      setCooldown(seconds);
      setView('reset');
    } catch (err) {
      setError(messageOf(err, t('errors.default')));
    } finally {
      setLoading(false);
    }
  }

  // PASSWORD_RESET: шаг 2 — код и новый пароль вместе, пароль применяется на сервере.
  async function handleReset(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (newPassword !== newConfirm) {
      setError(t('auth.resetMismatch'));
      return;
    }

    if (resetCode.length !== 6) {
      setError(t('auth.codeIncomplete'));
      return;
    }

    setLoading(true);
    try {
      await onResetPassword(resetEmail, resetCode, newPassword);
      // The password is changed and a session was issued — the modal has done its job.
      onClose();
    } catch (err) {
      setError(messageOf(err, t('errors.default')));
      // A spent code is dead; the password stays so the user does not retype it after requesting a new one.
      setResetCode('');
    } finally {
      setLoading(false);
    }
  }

  const overlay = (children: ReactNode) => (
    <div className="dialog-overlay dialog-overlay--auth" onMouseDown={onClose}>
      <div className="dialog-card auth-modal" role="dialog" aria-modal="true" onMouseDown={(e) => e.stopPropagation()}>
        {children}
      </div>
    </div>
  );

  // Шаг 2 регистрации: код из письма.
  if (challenge) {
    return overlay(
      <>
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
          {cooldown > 0 ? t('auth.spamHint', { seconds: cooldown }) : t('auth.spamHintReady')}
        </p>

        <div className="auth-modal__actions">
          <button
            className="dialog-btn"
            type="button"
            onClick={() => void resend(challenge.email)}
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
      </>
    );
  }

  // PASSWORD_RESET: шаг 1 — ввод адреса.
  if (view === 'forgot') {
    return overlay(
      <>
        <div className="auth-modal__head">
          <div className="dialog-title">{t('auth.forgotTitle')}</div>
          <button className="icon-btn" onClick={onClose} aria-label={t('common.close')} type="button">
            <CloseIcon size={18} />
          </button>
        </div>

        <div className="auth-modal__subtitle">{t('auth.forgotSubtitle')}</div>

        <form className="auth-modal__form" onSubmit={handleForgot}>
          <input
            className="dialog-input"
            type="email"
            placeholder={t('auth.email')}
            value={forgotEmail}
            onChange={(e) => setForgotEmail(e.target.value)}
            autoFocus
            autoComplete="email"
          />

          {error && <div className="auth-modal__error">{error}</div>}

          <button
            className="dialog-btn dialog-btn--primary auth-modal__submit"
            type="submit"
            disabled={loading || !forgotEmail.trim()}
          >
            {loading ? t('auth.waiting') : t('auth.forgotSubmit')}
          </button>
        </form>

        <button
          className="auth-modal__switch"
          type="button"
          onClick={() => {
            setView('credentials');
            setError(null);
          }}
        >
          {t('auth.backToLogin')}
        </button>
      </>
    );
  }

  // PASSWORD_RESET: шаг 2 — код из письма и новый пароль.
  if (view === 'reset') {
    return overlay(
      <>
        <div className="auth-modal__head">
          <div className="dialog-title">{t('auth.resetTitle')}</div>
          <button className="icon-btn" onClick={onClose} aria-label={t('common.close')} type="button">
            <CloseIcon size={18} />
          </button>
        </div>

        <div className="auth-modal__subtitle">{t('auth.resetSubtitle', { email: resetEmail })}</div>

        <form className="auth-modal__form" onSubmit={handleReset}>
          <input
            className="dialog-input auth-code__input"
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={6}
            value={resetCode}
            onChange={(e) => setResetCode(e.target.value.replace(/\D/g, '').slice(0, 6))}
            aria-label={t('auth.codeLabel')}
            placeholder="••••••"
          />
          <PasswordField
            value={newPassword}
            onChange={setNewPassword}
            placeholder={t('auth.newPassword')}
            autoComplete="new-password"
            visible={showNewPassword}
            onToggle={() => setShowNewPassword((v) => !v)}
          />
          <PasswordField
            value={newConfirm}
            onChange={setNewConfirm}
            placeholder={t('auth.newPasswordConfirm')}
            autoComplete="new-password"
            visible={showConfirm}
            onToggle={() => setShowConfirm((v) => !v)}
          />

          {error && <div className="auth-modal__error">{error}</div>}

          <button
            className="dialog-btn dialog-btn--primary auth-modal__submit"
            type="submit"
            disabled={loading || resetCode.length !== 6 || !newPassword || !newConfirm}
          >
            {loading ? t('auth.resetting') : t('auth.resetButton')}
          </button>
        </form>

        <p className="auth-modal__hint">
          {cooldown > 0 ? t('auth.spamHint', { seconds: cooldown }) : t('auth.spamHintReady')}
        </p>

        <div className="auth-modal__actions">
          <button
            className="dialog-btn"
            type="button"
            onClick={() => void resend(resetEmail)}
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
              setView('forgot');
              setResetCode('');
              setNewPassword('');
              setNewConfirm('');
              setError(null);
              setCooldown(0);
            }}
          >
            {t('auth.changeEmail')}
          </button>
        </div>
      </>
    );
  }

  // Шаг 1: адрес и пароль.
  return overlay(
    <>
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
        <PasswordField
          value={password}
          onChange={setPassword}
          placeholder={t('auth.password')}
          autoComplete={isRegister ? 'new-password' : 'current-password'}
          visible={showPassword}
          onToggle={() => setShowPassword((v) => !v)}
        />
        {isRegister && (
          <PasswordField
            value={confirm}
            onChange={setConfirm}
            placeholder={t('auth.confirmPassword')}
            autoComplete="new-password"
            visible={showConfirm}
            onToggle={() => setShowConfirm((v) => !v)}
          />
        )}

        {/* PASSWORD_RESET: вход — единственный экран, где «забыли пароль» уместен. */}
        {!isRegister && (
          <button
            className="auth-modal__forgot"
            type="button"
            onClick={() => {
              setForgotEmail(email);
              setView('forgot');
              setError(null);
            }}
          >
            {t('auth.forgotPassword')}
          </button>
        )}

        {/* PRIVACY_POLICY: добавлено 2026-09-25 — обязательное согласие при регистрации. Ссылка
            открывается в новой вкладке, чтобы не потерять уже введённые email и пароль. */}
        {isRegister && (
          <label className="auth-modal__consent">
            <input
              type="checkbox"
              checked={accepted}
              onChange={(e) => {
                setAccepted(e.target.checked);
                if (e.target.checked) setError(null);
              }}
              aria-label={t('auth.policyAria')}
            />
            <span>
              {t('auth.policyPrefix')}{' '}
              <a
                className="auth-modal__consent-link"
                href={LEGAL_PATHS.privacy}
                target="_blank"
                rel="noopener noreferrer"
              >
                {t('auth.policyLink')}
              </a>{' '}
              {t('auth.policySuffix')}
            </span>
          </label>
        )}

        {error && <div className="auth-modal__error">{error}</div>}

        <button
          className="dialog-btn dialog-btn--primary auth-modal__submit"
          type="submit"
          disabled={loading || !email.trim() || !password || (isRegister && !accepted)}
        >
          {loading ? t('auth.waiting') : isRegister ? t('auth.registerButton') : t('auth.loginButton')}
        </button>
      </form>

      <div className="auth-modal__divider">
        <span>{t('auth.or')}</span>
      </div>

      {/* PRIVACY_POLICY: вход через GitHub создаёт учётную запись в обход формы, поэтому в
          режиме регистрации ссылка активна только после согласия с Политикой. */}
      {isRegister && !accepted ? (
        <div className="auth-modal__github auth-modal__github--locked" aria-disabled="true">
          <GitHubIcon size={18} /> {t('auth.loginWithGithub')}
        </div>
      ) : (
        <a className="auth-modal__github" href="/api/auth/github/login">
          <GitHubIcon size={18} /> {t('auth.loginWithGithub')}
        </a>
      )}

      <button className="auth-modal__switch" onClick={onSwitchMode} type="button">
        {isRegister ? t('auth.switchToLogin') : t('auth.switchToRegister')}
      </button>
    </>
  );
}
