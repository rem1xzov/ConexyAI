using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Security.Cryptography;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.Controller;
using ConexyAI.Entity;
using ConexyAI.Extensions;
using ConexyAI.Repository;
using ConexyAI.Service;
using ConexyAI.Service.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// VISUAL_DESKTOP: проверяется то, на чём держится безопасность входа в окно визуального десктопа:
// код выпускается только по валидному JWT вызывающего клиента, обменивается без PKCE, живёт две
// минуты и сжигается при первом использовании, а мобильный handoff при этом по-прежнему требует
// PKCE-verifier. Контроллер собирается поверх фейковых зависимостей — без БД и без сети.
internal static class DesktopHandoffTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add(
            "visual desktop: handoff is minted for the signed-in user and exchanged once without PKCE",
            DesktopFlowAsync);
        TestRegistry.Add(
            "visual desktop: mobile handoff still requires the PKCE verifier",
            MobilePkceStillRequiredAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static async Task DesktopFlowAsync()
    {
        // Контракт эндпоинта: POST /api/auth/desktop-handoff, только по JWT, лимит — как у обмена
        // десктопного ключа.
        var method = typeof(AuthController).GetMethod(nameof(AuthController.DesktopHandoff))!;
        Assert(method.GetCustomAttribute<AuthorizeAttribute>() is not null, "the desktop handoff requires a JWT");
        Assert(method.GetCustomAttribute<HttpPostAttribute>()?.Template == "desktop-handoff",
            "the route is POST desktop-handoff");
        Assert(method.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName == AuthRateLimitPolicies.DeviceToken,
            "it is rate-limited by the device-token policy");

        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = "desktop@example.com" };
        var issued = new TokenResponse("desktop-jwt", DateTime.UtcNow.AddHours(1), 60);
        var users = new FakeUserRepository(user);
        var controller = Controller(new FakeTokenService(issued), users, userId);

        var minted = await controller.DesktopHandoff(CancellationToken.None);
        var code = ReadString(minted, "code");
        Assert(minted is OkObjectResult, "an authenticated caller gets a code");
        Assert(code is { Length: 32 } && code.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"the code is 16 random bytes as lower-case hex, got '{code}'");
        Assert(controller.Response.Headers.CacheControl.ToString() == "no-store",
            "the code response must not be cached");
        Assert(users.LastRequestedId == userId, "the user comes from the JWT, not from the request body");

        // VISUAL_DESKTOP: Challenge пустой, поэтому обмен идёт без code_verifier.
        var exchanged = controller.ExchangeHandoff(new MobileHandoffRequest(code, null));
        Assert(exchanged is OkObjectResult { Value: TokenResponse token } && token == issued,
            "the desktop code is exchanged for the same JWT the client already has");
        var replay = controller.ExchangeHandoff(new MobileHandoffRequest(code, null));
        Assert(replay is BadRequestObjectResult && ErrorCode(replay) == "code_expired",
            "the desktop code burns on first use");

        // Мусорный verifier десктопному коду не мешает: PKCE к нему не применяется (осознанно).
        var second = await controller.DesktopHandoff(CancellationToken.None);
        Assert(controller.ExchangeHandoff(new MobileHandoffRequest(ReadString(second, "code"), new string('x', 43))) is OkObjectResult,
            "a desktop code does not need (or check) a PKCE verifier");

        var anonymous = Controller(new FakeTokenService(issued), users, userId: null);
        Assert(await anonymous.DesktopHandoff(CancellationToken.None) is UnauthorizedResult,
            "a token without a user id is refused");

        var ghost = Controller(new FakeTokenService(issued), new FakeUserRepository(null), Guid.NewGuid());
        Assert(await ghost.DesktopHandoff(CancellationToken.None) is UnauthorizedResult,
            "a token whose user no longer exists is refused");
    }

    private static async Task MobilePkceStillRequiredAsync()
    {
        var userId = Guid.NewGuid();
        var issued = new TokenResponse("mobile-jwt", DateTime.UtcNow.AddHours(1), 60);
        var controller = Controller(new FakeTokenService(issued), new FakeUserRepository(new User { Id = userId }), userId);

        // Настоящий мобильный код всегда записывается с S256-challenge (MobileReturn проверяет его до
        // записи), поэтому здесь воспроизводим ровно такую запись.
        var verifier = new string('v', 43);
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        Assert(AuthController.IsValidCodeChallenge(challenge), "the test challenge has the shape the app sends");

        var handoffs = Handoffs();
        var withoutVerifier = "mobile-" + Guid.NewGuid().ToString("N");
        handoffs[withoutVerifier] = (issued, challenge, DateTime.UtcNow.AddMinutes(2));

        var rejected = controller.ExchangeHandoff(new MobileHandoffRequest(withoutVerifier, null));
        Assert(rejected is BadRequestObjectResult, "a mobile code without the verifier is refused");
        Assert(ErrorCode(rejected) == "invalid_code_verifier", "the error stays invalid_code_verifier");
        var burnt = controller.ExchangeHandoff(new MobileHandoffRequest(withoutVerifier, verifier));
        Assert(burnt is BadRequestObjectResult && ErrorCode(burnt) == "code_expired",
            "the code was burned by the failed attempt, not left for a retry");

        var good = "mobile-" + Guid.NewGuid().ToString("N");
        handoffs[good] = (issued, challenge, DateTime.UtcNow.AddMinutes(2));
        var accepted = controller.ExchangeHandoff(new MobileHandoffRequest(good, verifier));
        Assert(accepted is OkObjectResult { Value: TokenResponse response } && response == issued,
            "the right verifier still exchanges the mobile code");
        Assert(controller.ExchangeHandoff(new MobileHandoffRequest(good, verifier)) is BadRequestObjectResult,
            "the mobile code is still one-time");
    }

    private static AuthController Controller(ITokenService tokens, IUserRepository users, Guid? userId)
    {
        var http = new DefaultHttpContext();
        if (userId is { } id)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()) }, "test"));
        }

        // Неиспользуемые в этих тестах зависимости контроллера не нужны: методы handoff их не трогают.
        return new AuthController(
            tokens,
            null!,
            null!,
            Options.Create(new GitHubOAuthOptions()),
            users,
            NullLogger<AuthController>.Instance,
            null!,
            null!,
            null!)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    // Тест видит приватный одноразовый склад кодов только через рефлексию: публичного шва к нему нет,
    // а выносить его из контроллера ради теста — лишний рефакторинг.
    private static ConcurrentDictionary<string, (TokenResponse Token, string Challenge, DateTime ExpiresUtc)> Handoffs()
    {
        var field = typeof(AuthController).GetField("MobileHandoffs", BindingFlags.NonPublic | BindingFlags.Static);
        Assert(field is not null, "the one-time code store is found by name");
        return (ConcurrentDictionary<string, (TokenResponse Token, string Challenge, DateTime ExpiresUtc)>)field!
            .GetValue(null)!;
    }

    private static string? ReadString(object? result, string property) =>
        result is ObjectResult { Value: { } value }
            ? value.GetType().GetProperty(property)?.GetValue(value) as string
            : null;

    private static string? ErrorCode(object? result) => ReadString(result, "error");

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class FakeTokenService(TokenResponse token) : ITokenService
    {
        public TokenResponse CreateToken(Guid userId, bool isAdmin, int tokenVersion) => token;

        public TokenResponse CreateToken(User user) => token;

        public TokenResponse CreateDeviceToken(User user) => token;

        public ValidatedToken? ValidateToken(string token) => null;
    }

    private sealed class FakeUserRepository(User? user) : IUserRepository
    {
        public Guid? LastRequestedId { get; private set; }

        public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            LastRequestedId = id;
            return Task.FromResult(user?.Id == id ? user : null);
        }

        public Task<User?> GetByGitHubIdAsync(string gitHubId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task AddAsync(User user, CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpdateAsync(User user, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<(IReadOnlyList<User> Items, int TotalCount)> GetUsersPaginatedAsync(
            int page, int pageSize, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task EnsureExistsAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<UserAuthState?> GetAuthStateAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> BumpTokenVersionAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> GetAdminUserIdsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
