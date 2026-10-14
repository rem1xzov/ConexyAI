using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ConexyAI.Service;

// PREVIEW: preview-gateway. Host {id}.{Preview:Domain} -> контейнер сервиса агента.
// Доступ только по токену: ?token=... один раз ставит cookie (host-scoped, HttpOnly) и убирается из URL.
// Ограничение: WebSocket (HMR) не проксируется — для превью используй собранный dist.
public sealed partial class PreviewGatewayMiddleware
{
    private const string CookieName = "conexy_preview";
    private const int MaxBodyBytes = 50 * 1024 * 1024;

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-authenticate", "proxy-authorization", "te", "trailer",
        "transfer-encoding", "upgrade", "host", "content-length",
    };

    private readonly RequestDelegate _next;
    private readonly IPreviewService _preview;
    private readonly IHttpClientFactory _http;

    public PreviewGatewayMiddleware(RequestDelegate next, IPreviewService preview, IHttpClientFactory http)
    {
        _next = next;
        _preview = preview;
        _http = http;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sub = _preview.ParseHost(context.Request.Host.Value ?? string.Empty);
        if (sub is null)
        {
            await _next(context);
            return;
        }

        var info = _preview.GetBySubdomain(sub);
        if (info is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsync("Preview not found or expired.");
            return;
        }

        var queryToken = context.Request.Query["token"].ToString();
        if (queryToken.Length > 0)
        {
            if (!TokenEquals(queryToken, info.Token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.Response.Cookies.Append(CookieName, info.Token, new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Path = "/",
            });
            var rest = context.Request.Query
                .Where(q => q.Key != "token")
                .Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value.ToString())}");
            var qs = string.Join('&', rest);
            context.Response.Redirect(context.Request.Path + (qs.Length > 0 ? "?" + qs : string.Empty));
            return;
        }

        if (!context.Request.Cookies.TryGetValue(CookieName, out var cookie) || !TokenEquals(cookie, info.Token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Preview token required. Open the link from the Preview tab.");
            return;
        }

        await ProxyAsync(context, info);
    }

    private async Task ProxyAsync(HttpContext context, PreviewInfo info)
    {
        var target = new Uri($"http://{info.Container}:{info.Port}{context.Request.Path}{context.Request.QueryString}");
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);

        foreach (var (key, value) in context.Request.Headers)
        {
            if (HopByHop.Contains(key)) continue;
            var v = value.ToString();
            if (key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                v = OwnCookie().Replace(v, string.Empty).Trim(' ', ';');
            if (v.Length == 0) continue;
            request.Headers.TryAddWithoutValidation(key, v);
        }

        request.Headers.Host = context.Request.Host.Value;

        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            if (context.Request.ContentLength > MaxBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } ct)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", ct);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.CreateClient("preview")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }
        catch (HttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync("The service is not responding.");
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var (key, values) in response.Headers.Concat(response.Content.Headers))
            {
                if (HopByHop.Contains(key)) continue;
                context.Response.Headers[key] = values.ToArray();
            }

            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    private static bool TokenEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    [GeneratedRegex(CookieName + "=[^;]*;?\\s*")]
    private static partial Regex OwnCookie();
}
