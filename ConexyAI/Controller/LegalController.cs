using ConexyAI.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ConexyAI.Controller;

// LEGAL_DOCS: добавлено 2026-09-26
/// <summary>
/// Публичные данные правовых документов. Реквизиты оператора вынесены в конфигурацию окружения и
/// отдаются фронтенду этим эндпоинтом, чтобы они не попадали в JS-бандл: на сервере их можно менять
/// без пересборки фронта, а в репозитории они не хранятся вовсе.
/// </summary>
[ApiController]
[Route("api/legal")]
[AllowAnonymous]
public class LegalController : ControllerBase
{
    private readonly IOptions<OperatorSettings> _operator;

    public LegalController(IOptions<OperatorSettings> options)
    {
        _operator = options;
    }

    /// <summary>
    /// Реквизиты оператора для политики обработки ПДн, оферты и политики возврата. Без авторизации:
    /// эти сведения и так опубликованы на сайте.
    /// </summary>
    [HttpGet("operator")]
    public IActionResult GetOperator()
    {
        var settings = _operator.Value;

        // Явный DTO вместо самой модели настроек: так к публичному ответу не прилипнет случайное
        // новое поле, если оно когда-нибудь появится в OperatorSettings (в том числе секретное).
        return Ok(new
        {
            name = settings.Name,
            inn = settings.Inn,
            address = settings.Address,
            email = settings.Email,
            phone = settings.Phone,
            publishedAt = settings.PublishedAt,
            updatedAt = settings.UpdatedAt,
            transferCountries = settings.TransferCountries,
            site = settings.Site,
        });
    }
}
