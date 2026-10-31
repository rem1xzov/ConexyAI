using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using ConexyAI.Configuration;
using ConexyAI.Service;
using ConexyAI.Service.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ConexyAI.Extensions;

// AUTH_RATE_LIMIT: добавлено 2026-09-24 (ревью M20, L7)
/// <summary>Names of the rate-limit policies applied with <c>[EnableRateLimiting]</c>.</summary>
public static class AuthRateLimitPolicies
{
    /// <summary>POST /api/auth/login — per client IP.</summary>
    public const string Login = "auth-login";

    /// <summary>POST /api/auth/register — per client IP.</summary>
    public const string Register = "auth-register";

    // EMAIL_VERIFICATION: добавлено 2026-09-24
    /// <summary>POST /api/auth/verify-email — per client IP; the code is also attempt-limited per row.</summary>
    public const string VerifyEmail = "auth-verify-email";

    /// <summary>POST /api/auth/resend-code — per client IP; the 60s cooldown is enforced per address too.</summary>
    public const string ResendCode = "auth-resend-code";

    // PASSWORD_RESET: добавлено 2026-09-26
    /// <summary>POST /api/auth/forgot-password — per client IP; it sends mail, so it is the tighter one.</summary>
    public const string ForgotPassword = "auth-forgot-password";

    /// <summary>POST /api/auth/reset-password — per client IP; the code is also attempt-limited per row.</summary>
    public const string ResetPassword = "auth-reset-password";

    /// <summary>GET /api/auth/github/login + /callback — per client IP.</summary>
    public const string GitHubOAuth = "auth-github";

    /// <summary>POST /api/device/token — per client IP (обмен ключа десктопа на JWT).</summary>
    public const string DeviceToken = "device-token";

    /// <summary>POST /api/speech/synthesize — per user (paid upstream).</summary>
    public const string Speech = "speech";
}

/// <summary>
/// Wiring for the auth hardening of 2026-09-24: JWT revocation (M19), rate limits and the
/// per-account lockout (M20), trusted forwarded headers. Program.cs only calls into this class.
/// </summary>
public static class AuthSecurityExtensions
{
    /// <summary>Stable error code of every 429 answer (rate limit and per-account lockout).</summary>
    public const string TooManyAttemptsError = "TOO_MANY_ATTEMPTS";

    /// <summary>
    /// Proxies whose <c>X-Forwarded-*</c> headers are always trusted: loopback only. The nginx
    /// frontend container is added explicitly via the <see cref="TrustedProxiesConfigKey"/> setting.
    /// </summary>
    /// <remarks>
    /// SEC_FORWARDED_HEADERS: раньше доверяли всем 10/8, 172.16/12, 192.168/16. Но бэкенд подключён к сети
    /// preview, где крутится КОД ПОЛЬЗОВАТЕЛЕЙ (start_service): оттуда можно было стучаться напрямую в
    /// backend:8080 с любым X-Forwarded-For и обходить лимиты по IP. Теперь доверяем только фиксированному
    /// IP nginx (docker-compose.prod.yml задаёт его и передаёт в TrustedProxies).
    /// </remarks>
    public static readonly IReadOnlyList<string> TrustedProxyNetworks = new[]
    {
        "127.0.0.0/8", "::1/128"
    };

    /// <summary>Comma-separated IPs or CIDRs of reverse proxies (the nginx container), e.g. <c>172.30.250.10</c>.</summary>
    public const string TrustedProxiesConfigKey = "TrustedProxies";

    /// <summary>Registers the revocation check, the login lockout, forwarded headers and rate limits.</summary>
    public static WebApplicationBuilder AddConexyAuthSecurity(this WebApplicationBuilder builder)
    {
        AddConexyAuthSecurity(builder.Services);
        return builder;
    }

    public static IServiceCollection AddConexyAuthSecurity(this IServiceCollection services)
    {
        // TOKEN_REVOCATION (M19)
        services.AddSingleton<IUserAuthStateCache, UserAuthStateCache>();
        services.AddScoped<ITokenRevocationValidator, TokenRevocationValidator>();
        // LOGIN_LOCKOUT (M20)
        services.AddSingleton<ILoginAttemptTracker>(_ => new LoginAttemptTracker(TimeProvider.System));

        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                ConfigureForwardedHeaders(options, configuration[TrustedProxiesConfigKey]));
        services.AddRateLimiter(ConfigureRateLimiter);
        return services;
    }

    /// <summary>
    /// Must run first in the pipeline so the rate limiter partitions by the real client IP
    /// (the one nginx put into <c>X-Forwarded-For</c>) instead of the nginx container's IP.
    /// </summary>
    public static IApplicationBuilder UseConexyForwardedHeaders(this IApplicationBuilder app) =>
        app.UseForwardedHeaders();

    /// <summary>
    /// Must run after routing (WebApplication adds UseRouting implicitly at the start of the
    /// pipeline, so endpoint <c>[EnableRateLimiting]</c> metadata is visible) and after
    /// authentication (the speech policy partitions by user id).
    /// </summary>
    public static IApplicationBuilder UseConexyRateLimiter(this IApplicationBuilder app) =>
        app.UseRateLimiter();

    /// <summary>
    /// SEC_DEVICE_SCOPE: a JWT obtained by exchanging a desktop key (<c>src=device</c>) is not a full
    /// browser session. Must run after authentication.
    /// </summary>
    /// <remarks>
    /// Утёкший ключ десктопа не должен давать админку и правку настроек аккаунта (MCP-серверы с
    /// токенами, GitHub-токен, память, инструкции). Десктоп (ConexyAI.Desktop/ServerClient.cs) ходит
    /// только в /api/conexy, /api/skills, /api/subscription/usage, /api/device/* и хаб — их не трогаем.
    /// GitHub-токен десктоп получает через /api/device/github-credential (нужен для git push на ПК).
    /// </remarks>
    public static IApplicationBuilder UseConexyDeviceSessionGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsDeviceSessionForbidden(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "DEVICE_SESSION_FORBIDDEN",
                    message = "Это действие доступно только на сайте, не из десктопной сессии."
                }, context.RequestAborted);
                return;
            }

            await next();
        });

    /// <summary>True when a device-key session calls an endpoint reserved for full sessions.</summary>
    public static bool IsDeviceSessionForbidden(HttpContext context)
    {
        if (context.User.FindFirst(ITokenService.DeviceSourceClaim)?.Value != ITokenService.DeviceSourceValue)
            return false;

        var path = context.Request.Path;
        if (path.StartsWithSegments("/api/admin"))
            return true;

        if (path.StartsWithSegments("/api/user"))
        {
            // MCP-серверы отдаются вместе с токенами — и чтение закрыто.
            return !HttpMethods.IsGet(context.Request.Method) || path.StartsWithSegments("/api/user/mcp");
        }

        return false;
    }

    // TOKEN_REVOCATION: добавлено 2026-09-24 (ревью M19)
    /// <summary>
    /// <c>JwtBearerEvents.OnTokenValidated</c> handler: rejects tokens of deleted users, tokens whose
    /// <c>tv</c> claim is stale or missing, and replaces the <c>isAdmin</c> claim with the database
    /// value. Runs for every bearer-authenticated request, including the SignalR connect that
    /// carries the token in <c>?access_token=</c>.
    /// </summary>
    public static async Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        if (context.Principal is null)
        {
            context.Fail("no_principal");
            return;
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<ITokenRevocationValidator>();
        var result = await validator.CheckAsync(context.Principal, context.HttpContext.RequestAborted);
        if (!result.IsValid)
        {
            context.Fail(result.FailureReason ?? "token_rejected");
            return;
        }

        TokenRevocationValidator.ApplyAdminClaim(context.Principal, result.State!.IsAdmin);
    }

    /// <summary>JSON body of every 429 answer; <c>code</c>/<c>message</c> match the auth form's error shape.</summary>
    public static object TooManyAttemptsBody(int retryAfterSeconds, string? message = null) => new
    {
        error = TooManyAttemptsError,
        retryAfterSeconds,
        code = AuthException.TooManyAttemptsCode,
        message = message ?? "Слишком много запросов. Попробуйте позже."
    };

    /// <summary>
    /// Partition key of the client: its IP, with IPv4-mapped IPv6 folded to IPv4 and IPv6 grouped
    /// by /64 (one subscriber usually owns a whole /64 and could rotate addresses inside it).
    /// </summary>
    public static string ClientPartitionKey(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null)
            return "ip:unknown";

        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return "ip6:" + new IPAddress(bytes);
        }

        return "ip:" + ip;
    }

    public static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, string? trustedProxies)
    {
        // SEC_FORWARDED_HEADERS: X-Forwarded-Proto тоже читаем — иначе за nginx Request.IsHttps всегда false.
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // nginx APPENDS the address it saw ($proxy_add_x_forwarded_for), so only the right-most
        // entry is trustworthy; everything to its left is whatever the client sent.
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();
        var entries = TrustedProxyNetworks.Concat(
            (trustedProxies ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var entry in entries)
        {
            var slash = entry.IndexOf('/');
            if (slash < 0)
            {
                if (IPAddress.TryParse(entry, out var single))
                    options.KnownProxies.Add(single);
                continue;
            }

            if (IPAddress.TryParse(entry[..slash], out var prefix)
                && int.TryParse(entry[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
            {
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
            }
        }
    }

    private static void ConfigureRateLimiter(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.OnRejected = OnRejectedAsync;

        // Логин: 10 попыток в минуту с одного IP (плюс блокировка аккаунта после 5 неудач).
        options.AddPolicy(AuthRateLimitPolicies.Login,
            ctx => FixedWindow(ClientPartitionKey(ctx), permits: 10, TimeSpan.FromMinutes(1)));
        // Регистрация: 10 за 10 минут с одного IP — хватает на опечатки, но не на ферму аккаунтов.
        options.AddPolicy(AuthRateLimitPolicies.Register,
            ctx => FixedWindow(ClientPartitionKey(ctx), permits: 10, TimeSpan.FromMinutes(10)));
        // EMAIL_VERIFICATION: проверка кода — 20 за 10 минут с одного IP. Перебор шестизначного кода
        // в любом случае ограничен пятью попытками на сам код в БД, здесь — защита от потока запросов.
        options.AddPolicy(AuthRateLimitPolicies.VerifyEmail,
            ctx => FixedWindow(ClientPartitionKey(ctx), permits: 20, TimeSpan.FromMinutes(10)));
        // Повторная отправка — 10 за 10 минут с одного IP (плюс кулдаун 60 сек на адрес и на IP).
        options.AddPolicy(AuthRateLimitPolicies.ResendCode,
            ctx => FixedWindow(ClientPartitionKey(ctx), permits: 10, TimeSpan.FromMinutes(10)));
        // PASSWORD_RESET: старт сброса шлёт письмо, поэтому лимит такой же, как у регистрации.
        options.AddPolicy(AuthRateLimitPolicies.ForgotPassword,
            ctx => FixedWindow(ClientPartitionKey(ctx), permits: 10, TimeSpan.FromMinutes(10)));
        // Проверка кода сброса — 20 за 10 минут (пять попыток на сам код остаются в БД).
        options.AddPolicy(AuthRateLimitPolicies.ResetPassword,
            ctx => FixedWindow(ClientPartitionKey(ctx), permits: 20, TimeSpan.FromMinutes(10)));
        options.AddPolicy(AuthRateLimitPolicies.GitHubOAuth,
            ctx => FixedWindow(ClientPartitionKey(ctx), permits: 20, TimeSpan.FromMinutes(1)));
        // DESKTOP_AGENT: 20 обменов ключа в минуту с IP — хватает на переподключения, не хватает на перебор.
        options.AddPolicy(AuthRateLimitPolicies.DeviceToken,
            ctx => FixedWindow(ClientPartitionKey(ctx), permits: 20, TimeSpan.FromMinutes(1)));
        // Синтез речи: платный апстрим — 10 запросов в минуту на пользователя.
        options.AddPolicy(AuthRateLimitPolicies.Speech,
            ctx => FixedWindow(
                ctx.User.TryGetUserId(out var userId) ? "user:" + userId : ClientPartitionKey(ctx),
                permits: 10,
                TimeSpan.FromMinutes(1)));
    }

    private static RateLimitPartition<string> FixedWindow(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true
        });

    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
            ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))
            : 60;
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        // The GitHub OAuth endpoints are top-level browser navigations: a JSON body would strand the
        // user on a raw page, so send them back to the SPA, which shows its GitHub-login error.
        if (http.Request.Path.StartsWithSegments("/api/auth/github"))
        {
            var callback = http.RequestServices.GetService<IOptions<GitHubOAuthOptions>>()?.Value.CallbackUrl;
            var origin = Uri.TryCreate(callback, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : string.Empty;
            http.Response.Redirect($"{origin}/?auth=error&message=too_many_attempts");
            return;
        }

        await http.Response.WriteAsJsonAsync(TooManyAttemptsBody(retryAfter), ct);
    }
}
