namespace ConexyAI.Configuration;

// PRIVACY_POLICY: добавлено 2026-09-25
/// <summary>
/// Version of the legal documents a user consents to at sign-up.
/// <para>
/// ЕДИНСТВЕННОЕ место, где задаётся версия: значение уходит в <c>User.PolicyVersion</c>, поэтому по
/// нему можно сказать, какой редакции текста согласился конкретный аккаунт. При любом изменении
/// «Политики обработки персональных данных» (frontend: components/legal/PrivacyPolicy.tsx) увеличьте
/// версию — тогда новые согласия будут помечены новой датой, а старые останутся привязаны к той,
/// которую пользователь реально видел.
/// </para>
/// </summary>
public static class LegalPolicy
{
    /// <summary>
    /// Держите в формате ISO-даты и синхронизируйте с датой последнего обновления документа
    /// (<c>OPERATOR.updatedAt</c> в <c>frontend/src/components/legal/operator.ts</c>).
    /// </summary>
    public const string CurrentVersion = "2026-10-01";
}
