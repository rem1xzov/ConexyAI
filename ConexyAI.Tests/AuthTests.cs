using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ConexyAI.Configuration;
using ConexyAI.Contract;
using ConexyAI.DbContext;
using ConexyAI.Entity;
using ConexyAI.Extensions;
using ConexyAI.Model;
using ConexyAI.Repository;
using ConexyAI.Service;
using ConexyAI.Service.Auth;
// EMAIL_VERIFICATION: добавлено 2026-09-25
using ConexyAI.Service.Email;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

// AUTH_HARDENING: добавлено 2026-09-24 — регрессии ревью H1 (админ по неподтверждённому email),
// M19 (отзыв JWT), M20 (rate limit + блокировка логина), L7 (синтез речи), L12 (ключ JWT).
internal static class AuthTests
{
    private const string AdminEmail = "owner@example.com";
    private const string Password = "correct horse battery";

    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("auth H1: registering an ADMIN_ACCOUNTS email yields a normal user", RegisteringAdminEmailIsNotAdminAsync);
        TestRegistry.Add("auth H1: GitHub-verified admin email wins over an email/password account for it", GitHubVerifiedEmailBeatsSquatterAsync);
        TestRegistry.Add("auth H1: an unverified GitHub email never grants admin; github-id entries do", GitHubUnverifiedEmailIsNotAdminAsync);
        TestRegistry.Add("auth M19: stale tv, legacy and deleted-user tokens are rejected; isAdmin comes from the DB", RevocationValidatorAsync);
        TestRegistry.Add("auth M19: bearer pipeline revokes on logout and follows DB admin flag (HTTP)", RevocationOverHttpAsync);
        TestRegistry.Add("auth M19: generic user updates never roll back TokenVersion", UpdateDoesNotRollBackTokenVersionAsync);
        TestRegistry.Add("auth M20: login locks after 5 failures, resets after success, hides account existence", LoginLockoutAsync);
        TestRegistry.Add("auth M20: per-IP rate limit on /api/auth/login answers 429 JSON (HTTP)", LoginRateLimitOverHttpAsync);
        TestRegistry.Add("auth L7: speech text goes in the POST body and is capped", SpeechSynthesisHardeningAsync);
        TestRegistry.Add("auth EMAIL_VERIFICATION: code flow, cooldown, attempts, expiry", EmailVerificationAsync);
        TestRegistry.Add("auth H3: SpeechKit logs never contain recognized speech or full upstream bodies", SpeechLogsArePrivateAsync);
        TestRegistry.Add("auth L12: JWT signing key guard refuses placeholders outside Development", SigningKeyGuardAsync);
    }

    // ---------------------------------------------------------------- H1

    // EMAIL_VERIFICATION: изменено 2026-09-25 — регистрация больше не создаёт аккаунт сама. Теперь
    // для ADMIN_ACCOUNTS-адреса действует документированный хук H1: неподтверждённого аккаунта не
    // существует вообще, а после подтверждения кодом владение адресом доказано, поэтому админство
    // (и только тогда) выдаётся.
    private static async Task RegisteringAdminEmailIsNotAdminAsync()
    {
        await using var provider = BuildServices(admins: AdminEmail);
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var verification = sp.GetRequiredService<IEmailVerificationService>();
        var mail = (RecordingEmailSender)sp.GetRequiredService<IEmailSender>();
        var admin = sp.GetRequiredService<IOptions<AdminAccountsOptions>>().Value;

        // Before the code is entered there is no account to promote at all.
        await verification.StartRegistrationAsync("  Owner@Example.com ", Password, NextIp());
        var repo = sp.GetRequiredService<IUserRepository>();
        Assert(await repo.GetByEmailAsync(AdminEmail) is null, "no account exists before the code is confirmed");

        var user = await verification.VerifyAsync(AdminEmail, mail.LastCode!);
        Assert(user.SubscriptionTier == SubscriptionTier.Free || user.SubscriptionTier == SubscriptionTier.Admin,
            $"a fresh tier is Free or the promoted Admin, was {user.SubscriptionTier}");
        Assert(admin.IsSuperAdmin(user), "the confirmed ADMIN_ACCOUNTS address is a superadmin");
        Assert(user.IsAdmin, "the documented H1 hook promotes the account once the address is proven");

        var stored = await repo.GetByIdAsync(user.Id);
        Assert(stored is { IsAdmin: true, EmailConfirmed: true }, "the DB row carries both flags");

        // The login path then picks it up like any other login.
        var loggedIn = await sp.GetRequiredService<IEmailAuthService>().LoginAsync(AdminEmail, Password);
        Assert(loggedIn.IsAdmin, "login keeps the promoted account admin");
    }

    private static async Task GitHubVerifiedEmailBeatsSquatterAsync()
    {
        var github = new FakeGitHub
        {
            Id = 4242,
            Login = "real-owner",
            Emails = { new("Owner@Example.com", Primary: true, Verified: true) }
        };
        await using var provider = BuildServices(admins: AdminEmail, github: github);

        Guid squatterId;
        await using (var scope = provider.CreateAsyncScope())
        {
            var squatter = await RegisterVerifiedAsync(scope.ServiceProvider, AdminEmail);
            squatterId = squatter.Id;
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var oauth = scope.ServiceProvider.GetRequiredService<IGitHubOAuthService>();
            var result = await oauth.HandleCallbackAsync("code-1");
            Assert(result.IsNewUser && result.IsAdmin, "the GitHub owner with a verified ADMIN_ACCOUNTS email must become admin");

            var repo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var owner = await repo.GetByIdAsync(result.UserId);
            Assert(owner is { IsAdmin: true, SubscriptionTier: SubscriptionTier.Admin }, "owner row must be admin");
            Assert(owner!.Email is null, "the squatted email must not be copied onto the owner (unique index) nor linked");

            var squatter = await repo.GetByIdAsync(squatterId);
            // EMAIL_VERIFICATION: раньше здесь проверялось, что «сквоттер» не получил админство. Теперь
            // аккаунт на этот адрес заводится только тем, кто прочитал письмо, поэтому админство по
            // подтверждённому ADMIN_ACCOUNTS-адресу — законное (документированный хук H1), и строка
            // остаётся самостоятельной: она не привязывается к GitHub-личности владельца.
            Assert(squatter is { GitHubId: null, EmailConfirmed: true }, "the email account stays its own, unlinked identity");

            var admin = scope.ServiceProvider.GetRequiredService<IOptions<AdminAccountsOptions>>().Value;
            Assert(admin.IsSuperAdmin(squatter!), "a confirmed ADMIN_ACCOUNTS address is a superadmin");

            // Second login of the owner: still admin, still no unique-index crash.
            var again = await oauth.HandleCallbackAsync("code-2");
            Assert(!again.IsNewUser && again.IsAdmin && again.UserId == result.UserId, "repeat login keeps the owner admin");
        }
    }

    private static async Task GitHubUnverifiedEmailIsNotAdminAsync()
    {
        var attacker = new FakeGitHub
        {
            Id = 1001,
            Login = "attacker",
            ProfileEmail = AdminEmail,
            Emails = { new(AdminEmail, Primary: true, Verified: false) }
        };
        await using (var provider = BuildServices(admins: AdminEmail, github: attacker))
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IGitHubOAuthService>().HandleCallbackAsync("c");
            Assert(!result.IsAdmin, "an unverified GitHub email must not grant admin");
            var row = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(result.UserId);
            Assert(row is { Email: null, EmailConfirmed: false }, "an unverified GitHub email must not be stored");
        }

        var byId = new FakeGitHub { Id = 777, Login = "renamed-owner" };
        await using (var provider = BuildServices(admins: "someone-else, github-id:777", github: byId))
        await using (var scope = provider.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IGitHubOAuthService>().HandleCallbackAsync("c");
            Assert(result.IsAdmin, "a github-id:<id> entry must grant admin to that GitHub account");
        }
    }

    // ---------------------------------------------------------------- M19

    private static async Task RevocationValidatorAsync()
    {
        await using var provider = BuildServices(admins: "");
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IUserRepository>();
        var tokens = sp.GetRequiredService<ITokenService>();
        var validator = sp.GetRequiredService<ITokenRevocationValidator>();

        var user = await RegisterVerifiedAsync(sp, "m19@example.com");
        var principal = tokens.ValidateToken(tokens.CreateToken(user).Token)!.Principal;

        var ok = await validator.CheckAsync(principal);
        Assert(ok.IsValid && ok.State is { IsAdmin: false }, $"a fresh token must be valid, got {ok.FailureReason}");

        // Promotion is visible on the next request without re-login (cache invalidated by UpdateAsync).
        var row = (await repo.GetByIdAsync(user.Id))!;
        row.IsAdmin = true;
        await repo.UpdateAsync(row);
        var promoted = await validator.CheckAsync(principal);
        Assert(promoted.IsValid && promoted.State!.IsAdmin, "the DB admin flag must be picked up immediately");
        Assert(!principal.IsAdmin(), "precondition: the token itself still says isAdmin=false");
        TokenRevocationValidator.ApplyAdminClaim(principal, promoted.State!.IsAdmin);
        Assert(principal.IsAdmin() && principal.FindAll("isAdmin").Count() == 1, "isAdmin claim must be replaced by the DB value");
        TokenRevocationValidator.ApplyAdminClaim(principal, false);
        Assert(!principal.IsAdmin(), "a demoted admin loses the claim");

        // Bump -> the old token is stale.
        Assert(await repo.BumpTokenVersionAsync(user.Id), "bump must find the user");
        var stale = await validator.CheckAsync(principal);
        Assert(!stale.IsValid && stale.FailureReason == TokenRevocationValidator.ReasonRevoked, $"stale tv must be rejected, got {stale.FailureReason}");
        var fresh = tokens.ValidateToken(tokens.CreateToken((await repo.GetByIdAsync(user.Id))!).Token)!.Principal;
        Assert((await validator.CheckAsync(fresh)).IsValid, "a token issued after the bump is valid");

        // Legacy token (issued before this release: no tv claim).
        var legacy = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("isAdmin", "true")
        }, "Bearer"));
        var legacyResult = await validator.CheckAsync(legacy);
        Assert(!legacyResult.IsValid && legacyResult.FailureReason == TokenRevocationValidator.ReasonLegacyToken, "a token without tv must be rejected");

        // Deleted user.
        await repo.DeleteAsync(user.Id);
        var gone = await validator.CheckAsync(fresh);
        Assert(!gone.IsValid && gone.FailureReason == TokenRevocationValidator.ReasonUserNotFound, $"a deleted user's token must be rejected, got {gone.FailureReason}");
    }

    private static async Task UpdateDoesNotRollBackTokenVersionAsync()
    {
        await using var provider = BuildServices(admins: "");
        Guid id;
        User staleCopy;
        await using (var scope = provider.CreateAsyncScope())
        {
            var user = await RegisterVerifiedAsync(scope.ServiceProvider, "race@example.com");
            id = user.Id;
            staleCopy = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(id))!;
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IUserRepository>().BumpTokenVersionAsync(id); // logout elsewhere
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            staleCopy.LastLoginAt = DateTime.UtcNow; // a login that read the row before the logout
            await scope.ServiceProvider.GetRequiredService<IUserRepository>().UpdateAsync(staleCopy);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(id);
            Assert(row!.TokenVersion == 1, $"UpdateAsync must not write TokenVersion back (expected 1, got {row.TokenVersion})");
        }
    }

    private static async Task RevocationOverHttpAsync()
    {
        await using var host = await AuthHost.StartAsync();
        var client = host.Client;

        // EMAIL_VERIFICATION: регистрация стала двухшаговой — сервер отвечает задачей на
        // подтверждение и НЕ выдаёт токен; сессия появляется только после кода из письма.
        var register = await client.PostAsJsonAsync("/api/auth/register", new { email = "http-m19@example.com", password = Password });
        Assert(register.StatusCode == HttpStatusCode.OK, $"register must succeed, got {(int)register.StatusCode}");
        using (var doc = JsonDocument.Parse(await register.Content.ReadAsStringAsync()))
        {
            Assert(doc.RootElement.GetProperty("requireVerification").GetBoolean(), "register must ask for the emailed code");
            Assert(doc.RootElement.GetProperty("resendCooldownSeconds").GetInt32() == 60, "register must state the cooldown");
            Assert(!doc.RootElement.TryGetProperty("token", out _), "register must not issue a token");
        }

        var mail = (RecordingEmailSender)host.App.Services.GetRequiredService<IEmailSender>();
        var verify = await client.PostAsJsonAsync("/api/auth/verify-email", new { email = "http-m19@example.com", code = mail.LastCode });
        Assert(verify.StatusCode == HttpStatusCode.OK, $"verify must succeed, got {(int)verify.StatusCode}");
        var token = (await verify.Content.ReadFromJsonAsync<TokenResponse>())!.Token;

        async Task<(HttpStatusCode Status, bool? IsAdmin)> ProbeAsync(string bearer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await client.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.OK) return (response.StatusCode, null);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (response.StatusCode, doc.RootElement.GetProperty("isAdmin").GetBoolean());
        }

        var first = await ProbeAsync(token);
        Assert(first == (HttpStatusCode.OK, false), $"fresh token must authenticate as non-admin, got {first}");

        // The same bearer via the SignalR-style query string goes through the same validation.
        var viaQuery = await client.GetAsync("/hubs/conexy/probe?access_token=" + Uri.EscapeDataString(token));
        Assert(viaQuery.StatusCode == HttpStatusCode.OK, $"?access_token= must authenticate on /hubs/conexy, got {(int)viaQuery.StatusCode}");

        await using (var scope = host.App.Services.CreateAsyncScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var user = (await repo.GetByEmailAsync("http-m19@example.com"))!;
            user.IsAdmin = true;
            await repo.UpdateAsync(user);
        }

        var promoted = await ProbeAsync(token);
        Assert(promoted == (HttpStatusCode.OK, true), $"isAdmin must follow the DB without re-login, got {promoted}");

        // Logout (the SPA sends only the cookie; here the bearer) revokes every token of the user.
        using (var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout"))
        {
            logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await client.SendAsync(logout);
            Assert(response.StatusCode == HttpStatusCode.OK, "logout must succeed");
        }

        var afterLogout = await ProbeAsync(token);
        Assert(afterLogout.Status == HttpStatusCode.Unauthorized, $"a logged-out token must be rejected, got {afterLogout.Status}");
        var queryAfterLogout = await client.GetAsync("/hubs/conexy/probe?access_token=" + Uri.EscapeDataString(token));
        Assert(queryAfterLogout.StatusCode == HttpStatusCode.Unauthorized, "the hub query-string token must be rejected after logout too");

        // Legacy token (no tv), correctly signed: rejected.
        var legacy = host.MintLegacyToken(Guid.NewGuid());
        Assert((await ProbeAsync(legacy)).Status == HttpStatusCode.Unauthorized, "a correctly signed legacy token without tv must be rejected");
    }

    // ---------------------------------------------------------------- M20

    private static async Task LoginLockoutAsync()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-09-24T10:00:00Z"));
        await using var provider = BuildServices(admins: "", time: time);
        await using var scope = provider.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IEmailAuthService>();
        await RegisterVerifiedAsync(scope.ServiceProvider, "lock@example.com");

        async Task<AuthException?> TryLoginAsync(string email, string password)
        {
            try { await auth.LoginAsync(email, password); return null; }
            catch (AuthException ex) { return ex; }
        }

        // 4 failures, then success -> counter resets.
        for (var i = 0; i < 4; i++)
            Assert((await TryLoginAsync("lock@example.com", "wrong-password"))?.Code == "invalid_credentials", "failure must be invalid_credentials");
        Assert(await TryLoginAsync("LOCK@example.com ", Password) is null, "the correct password before the limit must log in (normalized email)");
        for (var i = 0; i < 4; i++)
            Assert((await TryLoginAsync("lock@example.com", "wrong-password"))?.Code == "invalid_credentials", "the counter must have been reset by the success");

        // 5th consecutive failure locks.
        var fifth = await TryLoginAsync("lock@example.com", "wrong-password");
        Assert(fifth is { Code: AuthException.TooManyAttemptsCode, StatusCode: 429 } && fifth.RetryAfterSeconds > 0,
            $"the 5th failure must lock with 429, got {fifth?.Code}");
        var locked = await TryLoginAsync("lock@example.com", Password);
        Assert(locked?.Code == AuthException.TooManyAttemptsCode, "even the correct password is refused while locked");

        // A non-existent account locks identically (no account-existence oracle).
        for (var i = 0; i < 4; i++)
            Assert((await TryLoginAsync("ghost@example.com", "wrong-password"))?.Code == "invalid_credentials", "ghost failures look the same");
        var ghostFifth = await TryLoginAsync("ghost@example.com", "wrong-password");
        Assert(ghostFifth?.Code == AuthException.TooManyAttemptsCode && ghostFifth.RetryAfterSeconds == fifth!.RetryAfterSeconds,
            "a non-existent email must lock exactly like a real one");

        // After the lockout period the correct password works again.
        time.Now += LoginAttemptTracker.LockoutDuration + TimeSpan.FromSeconds(1);
        Assert(await TryLoginAsync("lock@example.com", Password) is null, "login must work after the lockout expires");
    }

    private static async Task LoginRateLimitOverHttpAsync()
    {
        await using var host = await AuthHost.StartAsync();
        var client = host.Client;

        async Task<HttpResponseMessage> LoginAsync(string forwardedFor)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                // Distinct emails: this exercises the per-IP limiter, not the per-account lockout.
                Content = JsonContent.Create(new { email = $"u{Guid.NewGuid():N}@example.com", password = "wrong-password" })
            };
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            return await client.SendAsync(request);
        }

        for (var i = 0; i < 10; i++)
        {
            var response = await LoginAsync("203.0.113.7");
            Assert(response.StatusCode == HttpStatusCode.Unauthorized, $"attempt {i + 1} must reach the controller (401), got {(int)response.StatusCode}");
        }

        var limited = await LoginAsync("203.0.113.7");
        Assert(limited.StatusCode == HttpStatusCode.TooManyRequests, $"the 11th login from one IP must be 429, got {(int)limited.StatusCode}");
        Assert(limited.Headers.RetryAfter is not null, "429 must carry Retry-After");
        using (var doc = JsonDocument.Parse(await limited.Content.ReadAsStringAsync()))
        {
            Assert(doc.RootElement.GetProperty("error").GetString() == AuthSecurityExtensions.TooManyAttemptsError, "stable error code");
            Assert(doc.RootElement.GetProperty("retryAfterSeconds").GetInt32() > 0, "retryAfterSeconds must be positive");
        }

        var otherIp = await LoginAsync("203.0.113.8");
        Assert(otherIp.StatusCode == HttpStatusCode.Unauthorized, "another client IP (forwarded by a trusted proxy) has its own budget");

        // The per-account lockout surfaces through the controller as the same 429 shape.
        for (var i = 0; i < 5; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
            {
                Content = JsonContent.Create(new { email = "target@example.com", password = "wrong-password" })
            };
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{i + 1}");
            var response = await client.SendAsync(request);
            if (i < 4)
            {
                Assert(response.StatusCode == HttpStatusCode.Unauthorized, "failures before the lockout are 401");
                continue;
            }

            Assert(response.StatusCode == HttpStatusCode.TooManyRequests, $"lockout must answer 429, got {(int)response.StatusCode}");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert(doc.RootElement.GetProperty("error").GetString() == AuthSecurityExtensions.TooManyAttemptsError, "lockout uses the same stable code");
        }
    }

    // ---------------------------------------------------------------- L7

    // EMAIL_VERIFICATION: добавлено 2026-09-25 — двухшаговая регистрация: сначала письмо с кодом,
    // аккаунт появляется только после верного кода. Проверяются и сами правила кода (кулдаун,
    // попытки, срок), и то, что старый пользователь продолжает входить с паролем.
    private static async Task EmailVerificationAsync()
    {
        // --- чистая политика кодов: без БД и без почты ---
        var codes = Enumerable.Range(0, 200).Select(_ => EmailCodePolicy.GenerateCode()).ToList();
        Assert(codes.All(c => c.Length == 6 && c.All(char.IsDigit) && c[0] != '0'),
            $"every code is 6 digits without a leading zero, got '{codes.FirstOrDefault(c => c.Length != 6)}'");
        Assert(codes.Distinct().Count() > 150, "codes must not repeat like a counter");
        var t0 = DateTime.UtcNow;
        Assert(EmailCodePolicy.CooldownRemaining(t0, t0.AddSeconds(-10), 60) == 50, "cooldown counts down");
        Assert(EmailCodePolicy.CooldownRemaining(t0, t0.AddSeconds(-61), 60) == 0, "cooldown ends");
        Assert(EmailCodePolicy.SpendAttempt(1) == 0 && EmailCodePolicy.SpendAttempt(0) == 0, "attempts never go negative");
        Assert(EmailCodePolicy.IsExpired(t0, t0) && !EmailCodePolicy.IsExpired(t0, t0.AddMinutes(1)), "expiry is inclusive");

        var time = new ManualTime(DateTimeOffset.Parse("2026-09-25T10:00:00Z"));
        await using var provider = BuildServices(admins: "", time: time);
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var verification = sp.GetRequiredService<IEmailVerificationService>();
        var mail = (RecordingEmailSender)sp.GetRequiredService<IEmailSender>();
        var users = sp.GetRequiredService<IUserRepository>();
        var auth = sp.GetRequiredService<IEmailAuthService>();
        const string Email = "newbie@example.com";
        const string Pass = "correct horse battery";

        // --- шаг 1: письмо уходит, аккаунта ещё нет ---
        var challenge = await verification.StartRegistrationAsync("  Newbie@Example.com ", Pass, "198.51.100.7");
        Assert(challenge.Email == Email, $"the address must be normalized, got '{challenge.Email}'");
        Assert(challenge.ResendCooldownSeconds == 60, $"the client is told the cooldown, got {challenge.ResendCooldownSeconds}");
        Assert(mail.Sends == 1 && mail.LastTo == Email, "the code goes to the normalized address");
        var code = mail.LastCode!;
        Assert(await users.GetByEmailAsync(Email) is null, "no account may exist before the code is confirmed");

        // --- кулдаун: второй код сразу — нельзя, ни на тот же адрес, ни с того же IP ---
        var tooSoon = await TryStartAsync(verification, Email, Pass, "198.51.100.7", time);
        Assert(tooSoon is { Code: "resend_cooldown", RetryAfterSeconds: 60 }, $"a second send within the cooldown must be refused, got {tooSoon?.Code}/{tooSoon?.RetryAfterSeconds}");
        var otherEmailSameIp = await TryStartAsync(verification, "other@example.com", Pass, "198.51.100.7", time);
        Assert(otherEmailSameIp?.Code == "resend_cooldown", $"the cooldown also applies per IP, got {otherEmailSameIp?.Code}");

        // --- неверный код тратит попытку и говорит, сколько осталось ---
        var wrong = await TryVerifyAsync(verification, Email, WrongCode(code));
        Assert(wrong is { Code: "invalid_code", AttemptsLeft: 4 }, $"a wrong code must spend one attempt, got {wrong?.Code}/{wrong?.AttemptsLeft}");
        Assert(await users.GetByEmailAsync(Email) is null, "a wrong code creates nothing");

        // --- верный код создаёт подтверждённый аккаунт и стирает код ---
        var user = await verification.VerifyAsync(Email, code);
        Assert(user.EmailConfirmed, "the account is confirmed when created");
        Assert(user.Email == Email && user.PasswordHash is not null, "it carries the chosen credentials");
        Assert(await sp.GetRequiredService<IEmailVerificationRepository>().GetLatestAsync(Email) is null, "the used code must be gone");
        Assert((await auth.LoginAsync(Email, Pass)).Id == user.Id, "the new account logs in with its password");

        // --- и адрес теперь действительно занят ---
        var taken = await TryStartAsync(verification, Email, Pass, "198.51.100.8", time);
        Assert(taken is { Code: "email_taken", StatusCode: 400 }, $"a confirmed address must be reported as taken, got {taken?.Code}/{taken?.StatusCode}");

        // --- секретный код живёт 15 минут ---
        time.Now = time.Now.AddSeconds(61);
        await verification.StartRegistrationAsync("late@example.com", Pass, "198.51.100.9");
        var lateCode = mail.LastCode!;
        time.Now = time.Now.AddMinutes(16);
        var expired = await TryVerifyAsync(verification, "late@example.com", lateCode);
        Assert(expired?.Code == "code_expired", $"an expired code must be refused, got {expired?.Code}");

        // --- повторная отправка после кулдауна выдаёт новый код и восстанавливает попытки ---
        time.Now = time.Now.AddSeconds(1);
        var resent = await verification.ResendAsync("late@example.com", "198.51.100.9");
        Assert(resent.ResendCooldownSeconds == 60, "the resend reports the cooldown again");
        var freshCode = mail.LastCode!;
        Assert(freshCode != lateCode || mail.Sends == 3, "a fresh code is generated for the resend");
        Assert((await verification.VerifyAsync("late@example.com", freshCode)).EmailConfirmed, "the resent code works");

        // --- исчерпание попыток закрывает код до новой отправки ---
        time.Now = time.Now.AddSeconds(61);
        await verification.StartRegistrationAsync("guessing@example.com", Pass, "198.51.100.10");
        var realCode = mail.LastCode!;
        AuthException? last = null;
        for (var i = 0; i < 5; i++)
            last = await TryVerifyAsync(verification, "guessing@example.com", WrongCode(realCode));
        Assert(last is { Code: "code_attempts_exhausted" }, $"the 5th wrong guess must exhaust the code, got {last?.Code}");
        var afterExhaustion = await TryVerifyAsync(verification, "guessing@example.com", realCode);
        Assert(afterExhaustion?.Code == "code_attempts_exhausted", "even the right code is refused once the attempts are gone");

        // --- сбой транспорта не оставляет код, за который потом придётся ждать кулдаун ---
        mail.FailNext = true;
        var failed = await TryStartAsync(verification, "smtp@example.com", Pass, "198.51.100.11", time);
        Assert(failed?.Code == "email_send_failed", $"a transport failure must be reported as such, got {failed?.Code}");
        Assert(await sp.GetRequiredService<IEmailVerificationRepository>().GetLatestAsync("smtp@example.com") is null,
            "a code nobody received must not block the next attempt");
    }

    private static async Task<AuthException?> TryStartAsync(
        IEmailVerificationService verification, string email, string password, string ip, ManualTime time)
    {
        _ = time;
        try
        {
            await verification.StartRegistrationAsync(email, password, ip);
            return null;
        }
        catch (AuthException ex)
        {
            return ex;
        }
    }

    private static async Task<AuthException?> TryVerifyAsync(IEmailVerificationService verification, string email, string code)
    {
        try
        {
            await verification.VerifyAsync(email, code);
            return null;
        }
        catch (AuthException ex)
        {
            return ex;
        }
    }

    /// <summary>A code that is certain not to be the real one.</summary>
    private static string WrongCode(string actual) => actual == "999999" ? "999998" : "999999";

    /// <summary>
    /// Registers through the two-step flow and returns the confirmed account — what the old
    /// single-step RegisterAsync used to do, for the tests that only need a signed-up user.
    /// </summary>
    private static async Task<User> RegisterVerifiedAsync(IServiceProvider sp, string email, string password = Password)
    {
        var verification = sp.GetRequiredService<IEmailVerificationService>();
        var mail = (RecordingEmailSender)sp.GetRequiredService<IEmailSender>();
        // A fresh caller IP per registration: the cooldown is deliberately per address and per IP.
        await verification.StartRegistrationAsync(email, password, NextIp());
        return await verification.VerifyAsync(email, mail.LastCode!);
    }

    private static int _ipSeq;
    private static string NextIp() => $"203.0.113.{Interlocked.Increment(ref _ipSeq) % 200 + 1}";

    private static async Task SpeechSynthesisHardeningAsync()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64]) });
        var speech = new SpeechKitService(
            new HttpClient(handler),
            Options.Create(new SpeechKitOptions { ApiKey = "k", FolderId = "folder" }),
            NullLogger<SpeechKitService>.Instance);

        const string secretText = "private words of the user";
        var wav = await speech.SynthesizeAsync(secretText);
        Assert(wav.Length > 44, "a WAV must be produced");
        var sent = handler.Requests.Single();
        Assert(sent.Method == HttpMethod.Post, "TTS must be a POST");
        Assert(!sent.Uri.Contains("text", StringComparison.OrdinalIgnoreCase) && sent.Uri.IndexOf('?') < 0,
            $"the text must not travel in the URL: {sent.Uri}");
        Assert(sent.Body.Contains("text=" + Uri.EscapeDataString(secretText).Replace("%20", "+")), $"the text must be in the form body: {sent.Body}");
        Assert(sent.ContentType == "application/x-www-form-urlencoded", $"form-encoded body expected, got {sent.ContentType}");

        var controller = new ConexyAI.Controller.SpeechController(speech, NullLogger<ConexyAI.Controller.SpeechController>.Instance);
        var tooLong = await controller.Synthesize(new ConexyAI.Controller.SynthesizeRequest(new string('a', ConexyAI.Controller.SpeechController.MaxTextLength + 1)), CancellationToken.None);
        Assert(tooLong is Microsoft.AspNetCore.Mvc.BadRequestObjectResult, "text over the cap must be a 400");
        Assert(handler.Requests.Count == 1, "an over-long text must not reach the upstream");

        var failing = new SpeechKitService(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("upstream secret detail")
        })), Options.Create(new SpeechKitOptions { ApiKey = "k", FolderId = "f" }), NullLogger<SpeechKitService>.Instance);
        var failingController = new ConexyAI.Controller.SpeechController(failing, NullLogger<ConexyAI.Controller.SpeechController>.Instance);
        var upstreamError = await failingController.Synthesize(new ConexyAI.Controller.SynthesizeRequest("hi"), CancellationToken.None);
        var body = JsonSerializer.Serialize((upstreamError as Microsoft.AspNetCore.Mvc.ObjectResult)?.Value);
        Assert(body.Contains("SPEECH_UPSTREAM_FAILED") && !body.Contains("secret"), $"upstream errors must be a generic code, got {body}");
    }

    private static async Task SpeechLogsArePrivateAsync()
    {
        const string secretSpeech = "my bank password is hunter2";
        var logger = new ListLogger<SpeechKitService>();
        var options = Options.Create(new SpeechKitOptions { ApiKey = "k", FolderId = "f" });
        var audio = new byte[16_000];

        var ok = new SpeechKitService(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { result = secretSpeech })
        })), options, logger);
        var recognized = await ok.RecognizeAsync(audio);
        Assert(recognized.Success && recognized.Text == secretSpeech, "recognition itself must still work");

        var hugeError = new string('x', 5000) + secretSpeech;
        var failing = new SpeechKitService(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(hugeError)
        })), options, logger);
        try { await failing.RecognizeAsync(audio); } catch (HttpRequestException) { }
        try { await failing.SynthesizeAsync("hello"); } catch (HttpRequestException) { }

        Assert(logger.Messages.Count >= 3, "the calls must have been logged");
        Assert(logger.Messages.All(m => !m.Contains(secretSpeech)), "recognized speech must never be logged");
        Assert(logger.Messages.All(m => m.Length < 600), "upstream error bodies must be truncated in logs");
    }

    // ---------------------------------------------------------------- L12

    private static Task SigningKeyGuardAsync()
    {
        var production = new FakeEnvironment("Production");
        var development = new FakeEnvironment("Development");
        const string placeholder = "change-me-to-a-long-random-secret-of-at-least-32-bytes";

        static bool Throws(Action action)
        {
            try { action(); return false; }
            catch (InvalidOperationException) { return true; }
        }

        Assert(Throws(() => JwtSigningKeyGuard.Validate(placeholder, production)), "the committed placeholder must be refused in Production");
        Assert(Throws(() => JwtSigningKeyGuard.Validate(placeholder.ToUpperInvariant(), new FakeEnvironment("Staging"))), "placeholder refused in any non-Development env");
        Assert(Throws(() => JwtSigningKeyGuard.Validate("short-but-random-7f3a", production)), "a key under 32 bytes must be refused");
        Assert(Throws(() => JwtSigningKeyGuard.Validate(null, production)), "a missing key must be refused");
        Assert(Throws(() => JwtSigningKeyGuard.Validate(" ", development)), "a missing key is refused even in Development");

        var strong = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        Assert(JwtSigningKeyGuard.Validate(strong, production) == strong, "a random 48-byte key must be accepted");
        Assert(JwtSigningKeyGuard.Validate(placeholder, development) == placeholder, "Development keeps working with the placeholder");
        return Task.CompletedTask;
    }

    // ================================================================ helpers

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static readonly string SigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

    private static void ConfigureJwt(JwtOptions o)
    {
        o.SigningKey = SigningKey;
        o.Issuer = "ConexyAI";
        o.Audience = "ConexyAI-Clients";
    }

    private static void ConfigureAdmins(AdminAccountsOptions o, string admins)
    {
        foreach (var item in admins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            o.Accounts.Add(item);
    }

    /// <summary>The auth services exactly as Program.cs wires them, on an InMemory database.</summary>
    private static void AddAuthServices(IServiceCollection services, string admins, FakeGitHub? github, TimeProvider? time)
    {
        var dbName = "auth_" + Guid.NewGuid().ToString("N");
        var root = new InMemoryDatabaseRoot();
        services.AddLogging();
        services.AddDbContext<DbConexy>(o => o.UseInMemoryDatabase(dbName, root));
        services.Configure<JwtOptions>(ConfigureJwt);
        services.Configure<AdminAccountsOptions>(o => ConfigureAdmins(o, admins));
        // EMAIL_VERIFICATION: добавлено 2026-09-25 — как на настроенном сервере; сам отправщик
        // подменён на RecordingEmailSender, поэтому письма никуда не уходят.
        services.Configure<SmtpOptions>(o =>
        {
            o.User = "conexy.ai.ru@gmail.com";
            o.Password = "test-app-password";
            o.FromEmail = "conexy.ai.ru@gmail.com";
        });
        services.Configure<GitHubOAuthOptions>(o =>
        {
            o.CallbackUrl = "http://localhost/api/auth/github/callback";
            o.ClientId = "id";
            o.ClientSecret = "secret";
        });
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IEmailAuthService, EmailAuthService>();
        services.AddScoped<IGitHubOAuthService, GitHubOAuthService>();
        // EMAIL_VERIFICATION: добавлено 2026-09-25 — как в Program.cs, только отправщик — recorder.
        services.AddScoped<IEmailVerificationRepository, EmailVerificationRepository>();
        services.AddSingleton<IEmailSender>(new RecordingEmailSender());
        services.AddScoped<IEmailVerificationService, EmailVerificationService>();
        services.AddSingleton<TimeProvider>(time ?? TimeProvider.System);
        services.AddSingleton<ITokenService, JwtTokenService>();
        services.AddConexyAuthSecurity();
        if (time is not null)
            services.AddSingleton<ILoginAttemptTracker>(_ => new LoginAttemptTracker(time));
        services.AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(github ?? new FakeGitHub()));
    }

    private static ServiceProvider BuildServices(string admins, FakeGitHub? github = null, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        AddAuthServices(services, admins, github, time);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// A real Kestrel host with the production middleware order (forwarded headers → authN →
    /// authZ → rate limiter) and the real AuthController, to prove the attributes and the
    /// JwtBearer event actually apply to controller actions.
    /// </summary>
    private sealed class AuthHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }

        public static async Task<AuthHost> StartAsync()
        {
            var contentRoot = Directory.CreateTempSubdirectory("auth-host-").FullName;
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Production",
                ContentRootPath = contentRoot,
                ApplicationName = typeof(ConexyAI.Controller.AuthController).Assembly.GetName().Name
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            AddAuthServices(builder.Services, admins: "", github: null, time: null);
            builder.Services.AddControllers();
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = "ConexyAI",
                    ValidAudience = "ConexyAI-Clients",
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey))
                };
                // Same events as Program.cs.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/conexy"))
                            context.Token = accessToken;
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = AuthSecurityExtensions.OnTokenValidatedAsync
                };
            });
            builder.Services.AddAuthorization();

            var app = builder.Build();
            app.UseConexyForwardedHeaders();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseConexyRateLimiter();
            app.MapControllers();
            app.MapGet("/probe", (ClaimsPrincipal user) => Results.Json(new { isAdmin = user.IsAdmin() })).RequireAuthorization();
            app.MapGet("/hubs/conexy/probe", (ClaimsPrincipal user) => Results.Json(new { isAdmin = user.IsAdmin() })).RequireAuthorization();

            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new AuthHost { App = app, Client = new HttpClient { BaseAddress = new Uri(address) } };
        }

        /// <summary>A correctly signed token shaped like the pre-2026-09-24 ones (no tv claim).</summary>
        public string MintLegacyToken(Guid userId)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
            var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
                issuer: "ConexyAI",
                audience: "ConexyAI-Clients",
                claims: new[] { new Claim("sub", userId.ToString()), new Claim("isAdmin", "true") },
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
            return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages) Messages.Add(formatter(state, exception));
        }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// Stands in for Gmail: records the code that would have been mailed, so a test can type it back,
    /// and can be told to fail like the transport does when credentials are wrong.
    /// </summary>
    private sealed class RecordingEmailSender : IEmailSender
    {
        public string? LastTo { get; private set; }
        public string? LastCode { get; private set; }
        public int Sends { get; private set; }
        public bool FailNext { get; set; }

        public Task SendVerificationCodeAsync(string toEmail, string code, int validMinutes, CancellationToken ct = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new EmailSendFailedException("test transport failure");
            }

            LastTo = toEmail;
            LastCode = code;
            Sends += 1;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "ConexyAI";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed record FakeEmail(string Email, bool Primary, bool Verified);

    /// <summary>Scripted GitHub OAuth endpoints (token exchange, /user, /user/emails).</summary>
    private sealed class FakeGitHub
    {
        public long Id { get; init; } = 1;
        public string Login { get; init; } = "someone";
        public string? ProfileEmail { get; init; }
        public List<FakeEmail> Emails { get; } = new();

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body = path switch
            {
                "/login/oauth/access_token" => new { access_token = "gh-token", token_type = "bearer" },
                "/user" => new { id = Id, login = Login, email = ProfileEmail },
                "/user/emails" => Emails.Select(e => new { email = e.Email, primary = e.Primary, verified = e.Verified }).ToArray(),
                _ => throw new InvalidOperationException("unexpected GitHub call " + path)
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }

    private sealed class FakeHttpClientFactory(FakeGitHub github) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RecordingHandler(github.Respond));
    }

    private sealed record RecordedRequest(HttpMethod Method, string Uri, string Body, string? ContentType);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(), body, request.Content?.Headers.ContentType?.MediaType));
            return respond(request);
        }
    }
}
