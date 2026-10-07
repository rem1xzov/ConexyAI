using ConexyAI.Model;

namespace ConexyAI.Entity;

// GITHUB_OAUTH: добавлено 2026-09-19
/// <summary>
/// A registered user. Password credentials are optional and only populated for future
/// email/password accounts; pure OAuth (GitHub) users have <see cref="GitHubId"/> set and
/// leave <see cref="PasswordHash"/>/<see cref="Email"/> null when GitHub exposes no email.
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string? Email { get; set; }

    public bool EmailConfirmed { get; set; }

    public string? PasswordHash { get; set; }

    /// <summary>GitHub numeric user id (as a string) — the stable OAuth identity key.</summary>
    public string? GitHubId { get; set; }

    public string? GitHubUsername { get; set; }

    public SubscriptionTier SubscriptionTier { get; set; } = SubscriptionTier.Free;

    // YOOKASSA: добавлено 2026-09-27
    /// <summary>
    /// До какого момента (UTC) действует оплаченный тариф. NULL — бессрочно (тариф выставлен вручную
    /// или администратором). Автопродления нет: каждый платёж — разовый, успешная оплата ПРОДЛЕВАЕТ
    /// доступ от текущей даты окончания (или от сейчас, если срок уже истёк).
    /// </summary>
    public DateTime? SubscriptionExpiresAt { get; set; }

    // TOKEN_TOPUP: добавлено 2026-10-06 — РАЗОВЫЕ докупки токенов («Token», 199 ₽ за 1 000 000).
    // Здесь хранится КУПЛЕННЫЙ объём (сумма всех покупок). Живёт на пользователе, а не в счётчике:
    // счётчик обнуляется при смене тарифа и по окну, а купленное не должно пропадать. Лимит пула =
    // бюджет тарифа + это пополнение (см. SubscriptionService). Израсходованная часть ведётся в
    // UserUsageCounterEntity.CoderTopUpUsed/CoworkTopUpUsed и НЕ сбрасывается — покупка конечна.
    public long CoderTokenTopUp { get; set; }
    public long CoworkTokenTopUp { get; set; }

    // EMAIL_AUTH: добавлено 2026-09-19
    /// <summary>Whether the user is an administrator (granted via AdminAccounts on login).</summary>
    public bool IsAdmin { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;

    // --- PRIVACY_POLICY: добавлено 2026-09-25 ---
    /// <summary>
    /// UTC moment the user accepted the Personal Data Processing Policy during sign-up. NULL for
    /// accounts that predate the consent gate (and for GitHub sign-ups, which have no form); those
    /// users keep their access and are never asked to re-accept retroactively.
    /// </summary>
    public DateTime? PolicyAcceptedAt { get; set; }

    /// <summary>
    /// Version of the policy the user accepted (see <c>LegalPolicy.CurrentVersion</c>), so that after
    /// a wording change it stays possible to tell which text a given account agreed to.
    /// </summary>
    public string? PolicyVersion { get; set; }
    // --- /PRIVACY_POLICY ---

    // --- TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19) ---
    /// <summary>
    /// Version of the user's sessions. Every JWT carries it in the <c>tv</c> claim and a request is
    /// rejected when the claim differs from this value, so bumping it (logout, admin revoke)
    /// invalidates every token issued before. Change it ONLY through
    /// <c>IUserRepository.BumpTokenVersionAsync</c>: generic updates never write this column.
    /// </summary>
    public int TokenVersion { get; set; }
    // --- /TOKEN_REVOCATION ---
}
