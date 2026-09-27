namespace ConexyAI.Configuration;

// LEGAL_DOCS: добавлено 2026-09-26
/// <summary>
/// Реквизиты оператора персональных данных / исполнителя для публичных правовых документов Сервиса
/// (политика обработки ПДн, публичная оферта, политика возврата).
/// <para>
/// Значения приходят ИЗ ОКРУЖЕНИЯ (<c>Operator__Name</c>, <c>Operator__Inn</c>, …) и намеренно не
/// лежат в <c>appsettings.json</c>: это персональные данные, а конфиг хранится в репозитории и
/// попадает в образ. На сервере они задаются в локальном <c>.env</c> (он в <c>.gitignore</c>) и
/// прокидываются в контейнер через <c>docker-compose.prod.yml</c>. Образец — <c>.env.example</c>.
/// </para>
/// </summary>
public class OperatorSettings
{
    public const string SectionName = "Operator";

    /// <summary>ФИО самозанятого. Например: «Иванов Иван Иванович».</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>ИНН самозанятого.</summary>
    public string Inn { get; set; } = string.Empty;

    /// <summary>Адрес регистрации / адрес для направления письменных обращений.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Email для обращений: поддержка, возвраты, персональные данные, отзыв согласия.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Контактный телефон. Используется в оферте и политике возврата.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>Дата публикации оферты. У остальных документов показывается только обновление.</summary>
    public string PublishedAt { get; set; } = string.Empty;

    /// <summary>Дата последней редакции документа.</summary>
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>
    /// Государства, в которые передаются тексты запросов к моделям ИИ (раздел 8 политики обработки
    /// персональных данных).
    /// </summary>
    public string TransferCountries { get; set; } = string.Empty;

    /// <summary>Домен Сервиса — упоминается в текстах документов.</summary>
    public string Site { get; set; } = "conexyai.ru";
}
