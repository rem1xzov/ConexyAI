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
        // LEGAL_CACHE: добавлено 2026-09-26 — реквизиты меняются только вместе с .env и рестартом
        // бэкенда, поэтому страница не должна дергать сервер на каждое открытие:
        //   max-age=300        — 5 минут в браузере: лишнего трафика нет, а правка реквизитов не
        //                        «зависает» надолго (перезагрузка страницы тоже обновит ответ);
        //   public             — эндпоинт анонимный и без пользовательских данных, поэтому его можно
        //                        кэшировать и в общем прокси. Это же слово явно разрешает общий кэш
        //                        для ответа на запрос с кредами, когда клиент шлёт cookie сессии;
        //                        без него прокси вправе проигнорировать ответ.
        Response.Headers.CacheControl = "public, max-age=300";

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
