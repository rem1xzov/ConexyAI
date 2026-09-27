using System.Net;
using System.Net.Sockets;

namespace ConexyAI.Configuration;

// YOOKASSA: добавлено 2026-09-27
/// <summary>
/// Настройки приёма платежей через ЮKassa API (v3).
/// <para>
/// <see cref="ShopId"/> и <see cref="SecretKey"/> приходят ИЗ ОКРУЖЕНИЯ (<c>YooKassa__ShopId</c>,
/// <c>YooKassa__SecretKey</c>) и намеренно пустые в <c>appsettings.json</c>: это секреты магазина,
/// а конфиг лежит в репозитории и попадает в образ. На сервере они задаются в локальном <c>.env</c>
/// и прокидываются в контейнер через <c>docker-compose.prod.yml</c>. Образец — <c>.env.example</c>.
/// </para>
/// </summary>
public class YooKassaSettings
{
    public const string SectionName = "YooKassa";

    /// <summary>shopId магазина из личного кабинета ЮKassa.</summary>
    public string ShopId { get; set; } = string.Empty;

    /// <summary>Секретный ключ магазина (используется как пароль в Basic Auth).</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Базовый адрес API. Меняется только для тестов/песочницы.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.yookassa.ru/v3";

    /// <summary>
    /// База для <c>return_url</c>: куда ЮKassa вернёт пользователя после оплаты. Ссылка на оплату
    /// открывается в том же браузере, поэтому это должен быть адрес сайта (со слэшем на конце).
    /// </summary>
    public string ReturnUrlBase { get; set; } = "https://conexyai.ru/";

    /// <summary>
    /// Подсети ЮKassa, с которых приходят уведомления (вебхуки). Формат CIDR, например
    /// <c>185.32.187.0/24</c>; можно несколько через запятую. Список ведёт ЮKassa и он может
    /// меняться — держите его в окружении (<c>YooKassa__NotificationIpRanges</c>). Пустое значение
    /// отключает проверку по IP (остаётся проверка через API, см. <c>PaymentService</c>), о чём в
    /// логах пишется предупреждение.
    /// </summary>
    public string NotificationIpRanges { get; set; } = string.Empty;

    /// <summary>Таймаут обращения к API ЮKassa.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ShopId) && !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>
    /// Проверяет, что адрес уведомления входит в доверенные подсети ЮKassa. Если список не задан,
    /// возвращает <c>true</c> (проверка по IP выключена) — вызывающий код обязан в этом случае
    /// опираться на подтверждение статуса через API.
    /// </summary>
    public bool IsNotificationIpAllowed(IPAddress? address)
    {
        if (address is null) return false;
        var ranges = ParseRanges(NotificationIpRanges);
        if (ranges.Count == 0) return true;

        // IPv4-mapped IPv6 (::ffff:1.2.3.4) приходит, когда Kestrel слушает dual-stack.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        foreach (var range in ranges)
        {
            if (range.Contains(address)) return true;
        }
        return false;
    }

    private static List<CidrRange> ParseRanges(string raw)
    {
        var result = new List<CidrRange>();
        foreach (var part in (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (CidrRange.TryParse(part, out var range)) result.Add(range);
        }
        return result;
    }

    /// <summary>Одна подсеть в нотации CIDR (или одиночный адрес без «/»).</summary>
    private readonly struct CidrRange
    {
        private readonly byte[] _network;
        private readonly int _prefixBits;

        private CidrRange(byte[] network, int prefixBits)
        {
            _network = network;
            _prefixBits = prefixBits;
        }

        public bool Contains(IPAddress address)
        {
            var bytes = address.AddressFamily == AddressFamily.InterNetworkV6
                ? address.MapToIPv4().GetAddressBytes()
                : address.GetAddressBytes();
            if (bytes.Length != _network.Length) return false;

            var fullBytes = _prefixBits / 8;
            for (var i = 0; i < fullBytes; i++)
            {
                if (bytes[i] != _network[i]) return false;
            }

            var remaining = _prefixBits % 8;
            if (remaining == 0) return true;
            var mask = (byte)(0xFF << (8 - remaining));
            return (bytes[fullBytes] & mask) == (_network[fullBytes] & mask);
        }

        public static bool TryParse(string value, out CidrRange range)
        {
            range = default;
            var text = value.Trim();
            if (text.Length == 0) return false;

            var slash = text.IndexOf('/');
            var addressPart = slash < 0 ? text : text[..slash];
            if (!IPAddress.TryParse(addressPart, out var address)) return false;

            var bytes = address.AddressFamily == AddressFamily.InterNetworkV6
                ? address.MapToIPv4().GetAddressBytes()
                : address.GetAddressBytes();
            if (bytes.Length != 4) return false;

            var prefix = bytes.Length * 8;
            if (slash >= 0)
            {
                if (!int.TryParse(text[(slash + 1)..], out prefix) || prefix < 0 || prefix > bytes.Length * 8)
                    return false;
            }

            range = new CidrRange(bytes, prefix);
            return true;
        }
    }
}
