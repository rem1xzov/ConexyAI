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

    /// <summary>GET /api/auth/github/login + /callback — per client IP.</summary>
    public const string GitHubOAuth = "auth-github";

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
    /// Proxies whose <c>X-Forwarded-For</c> is trusted: loopback + private ranges (the nginx
    /// container reaches the backend over a docker bridge network). Anything else connecting
    /// directly cannot spoof its client IP.
    /// </summary>
    public static readonly IReadOnlyList<string> TrustedProxyNetworks = new[]
    {
        "127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"
    };

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

        services.Configure<ForwardedHeadersOptions>(ConfigureForwardedHeaders);
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

    private static void ConfigureForwardedHeaders(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
        // nginx APPENDS the address it saw ($proxy_add_x_forwarded_for), so only the right-most
        // entry is trustworthy; everything to its left is whatever the client sent.
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownNetworks.Clear();
        foreach (var cidr in TrustedProxyNetworks)
        {
            var slash = cidr.IndexOf('/');
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(
                IPAddress.Parse(cidr[..slash]),
                int.Parse(cidr[(slash + 1)..], CultureInfo.InvariantCulture)));
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
        options.AddPolicy(AuthRateLimitPolicies.GitHubOAuth,
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
