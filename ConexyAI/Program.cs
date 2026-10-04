using System.Text;
using ConexyAI.Configuration;
using ConexyAI.DbContext;
using ConexyAI.Extensions;
using ConexyAI.Hub;
using ConexyAI.Repository;
using ConexyAI.Service;
using ConexyAI.Service.Auth;
// EMAIL_VERIFICATION: добавлено 2026-09-24
using ConexyAI.Service.Email;
// YOOKASSA: добавлено 2026-09-27 — приём платежей.
using ConexyAI.Service.Payments;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ATTACHMENT_SIZE_LIMIT: добавлено 2026-09-22 — base64-вложения едут в JSON-теле /api/conexy/run,
// поэтому несколько фото упираются в дефолтные 30MB Kestrel раньше, чем сработает
// [RequestSizeLimit] на экшене. Держать в синхроне с атрибутом контроллера (55MB) и с
// client_max_body_size в frontend/nginx.conf.
const long MaxUploadBodyBytes = 60_000_000;
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MaxUploadBodyBytes;
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
// SIGNALR_KEEPALIVE: добавлено 2026-09-22 — параметры заданы явно и согласованы с таймаутами
// прокси (nginx.conf: proxy_read_timeout 3600s для /hubs/, Cloudflare). Причина: во время долгой
// задачи агента соединение рвалось само по себе. Две конкретные причины:
//   * дефолтный ClientTimeoutInterval = 30s — если вкладка браузера в фоне, JS-таймеры, включая
//     клиентский keep-alive, троттлятся до ~1 раза в минуту, и сервер считал клиента мёртвым;
//   * 15s keep-alive гарантирует, что ни nginx, ни Cloudflare не увидят idle-соединение.
builder.Services.AddSignalR(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    // Must stay >= 2x KeepAliveInterval; 120s tolerates background-tab timer throttling.
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(120);
});
// STREAM_SCOPE_TAG: добавлено 2026-09-24 — ревью H6: каждое событие группы task_{id} несёт {id}
// последним аргументом, чтобы клиент раскладывал поток по своим ходам (см. ScopeTaggingHubContext).
builder.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IHubContext<ConexyHub>, ScopeTaggingHubContext>();

// CORS: the production frontend is served from https://conexyai.ru (and www) by nginx and
// calls the API same-origin via the /api + /hubs proxy, so CORS mainly matters for the local
// Vite dev server. We intentionally avoid AllowAnyOrigin() + AllowCredentials() (insecure).
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
        {
            // Production origins.
            if (origin == "https://conexyai.ru" || origin == "https://www.conexyai.ru")
                return true;

            // Local development only: allow any localhost/127.0.0.1 origin (Vite dev server).
            return builder.Environment.IsDevelopment()
                && (origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase)
                    || origin.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase));
        })
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
    });
});

// Swagger (OpenAPI) with JWT bearer support for interactive testing.
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "ConexyAI API",
        Version = "v1",
        Description = "Autonomous AI agent platform. Authenticate via the Authorize button using a JWT from /api/auth/dev-token (development only)."
    });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter 'Bearer' [space] and then your token."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddDbContext<DbConexy>(options =>
        options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Options bound from appsettings.json (single source of truth for upstream mapping).
builder.Services.Configure<AiUpstreamOptions>(builder.Configuration.GetSection(AiUpstreamOptions.SectionName));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<GitHubOptions>(builder.Configuration.GetSection(GitHubOptions.SectionName));
// GITHUB_OAUTH: добавлено 2026-09-19
builder.Services.Configure<GitHubOAuthOptions>(options =>
{
    builder.Configuration.GetSection(GitHubOAuthOptions.SectionName).Bind(options);
    // Client credentials are injected via environment variables (never committed).
    options.ClientId = builder.Configuration[GitHubOAuthOptions.ClientIdEnvVar] ?? options.ClientId;
    options.ClientSecret = builder.Configuration[GitHubOAuthOptions.ClientSecretEnvVar] ?? options.ClientSecret;
});
builder.Services.Configure<WorkspaceOptions>(builder.Configuration.GetSection(WorkspaceOptions.SectionName));
builder.Services.Configure<WebSearchOptions>(builder.Configuration.GetSection(WebSearchOptions.SectionName));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<SpeechKitOptions>(builder.Configuration.GetSection(SpeechKitOptions.SectionName));
// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
builder.Services.Configure<DangerousCommandOptions>(builder.Configuration.GetSection(DangerousCommandOptions.SectionName));
// SUBSCRIPTION_TIERS: добавлено 2026-09-17
builder.Services.Configure<SubscriptionLimitsOptions>(builder.Configuration.GetSection(SubscriptionLimitsOptions.SectionName));
builder.Services.Configure<MemoryOptions>(builder.Configuration.GetSection(MemoryOptions.SectionName));
// CONVERSATION_SERVICE: добавлено 2026-09-23 — глубина истории — одна настройка на все пути.
builder.Services.Configure<ConversationOptions>(builder.Configuration.GetSection(ConversationOptions.SectionName));
// RAG: добавлено 2026-09-17
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));
// SANDBOX: добавлено 2026-09-17
builder.Services.Configure<SandboxOptions>(builder.Configuration.GetSection(SandboxOptions.SectionName));
// EMAIL_VERIFICATION: добавлено 2026-09-24 — SMTP для писем с кодом подтверждения. Пароль
// приложения приходит ТОЛЬКО из окружения (SMTP_PASSWORD), в appsettings.json его нет и не должно
// быть: конфиг лежит в репозитории и в образе.
builder.Services.Configure<SmtpOptions>(options =>
{
    builder.Configuration.GetSection(SmtpOptions.SectionName).Bind(options);
    options.Password = builder.Configuration[SmtpOptions.PasswordEnvVar] ?? "";
});
// LEGAL_DOCS: добавлено 2026-09-26 — реквизиты оператора для публичных правовых документов.
// Приходят из окружения (Operator__Name, Operator__Inn, …), чтобы не попадать в репозиторий, образ
// и JS-бандл сайта; в appsettings.json лежат только пустые значения.
builder.Services.Configure<OperatorSettings>(builder.Configuration.GetSection(OperatorSettings.SectionName));
// LEGAL_DOCS: даты публикации/обновления документов не являются персональными данными, поэтому пустые
// Operator__PublishedAt / Operator__UpdatedAt заполняем версией документов (LegalPolicy.CurrentVersion):
// сайт всегда показывает актуальную дату, а на сервере не нужно держать ещё две переменные. Явно
// заданное в окружении значение по-прежнему имеет приоритет и при пересборке правовых текстов
// должно быть синхронизировано с LegalPolicy.CurrentVersion.
builder.Services.PostConfigure<OperatorSettings>(options =>
{
    if (string.IsNullOrWhiteSpace(options.PublishedAt)) options.PublishedAt = LegalPolicy.CurrentVersion;
    if (string.IsNullOrWhiteSpace(options.UpdatedAt)) options.UpdatedAt = LegalPolicy.CurrentVersion;
});
// YOOKASSA: добавлено 2026-09-27 — приём платежей. shopId и секретный ключ приходят из окружения
// (YooKassa__ShopId / YooKassa__SecretKey); в appsettings.json они пустые, потому что конфиг лежит
// в репозитории и в образе. Каталог тарифов и цены — наоборот, серверный: сумму платежа клиент
// прислать не может.
builder.Services.Configure<YooKassaSettings>(builder.Configuration.GetSection(YooKassaSettings.SectionName));
builder.Services.Configure<PaymentPlansOptions>(builder.Configuration.GetSection(PaymentPlansOptions.SectionName));
// EMAIL_AUTH: добавлено 2026-09-19
builder.Services.Configure<AdminAccountsOptions>(options =>
{
    var raw = builder.Configuration[AdminAccountsOptions.EnvVar];
    if (string.IsNullOrWhiteSpace(raw)) return;
    foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        options.Accounts.Add(item);
    }
});

// JWT Bearer authentication. The user id is always taken from the token claims.
var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
// JWT_KEY_GUARD (L12) + TOKEN_REVOCATION (M19) + AUTH_RATE_LIMIT (M20): 2026-09-24, see AuthSecurityExtensions.
var signingKey = JwtSigningKeyGuard.Validate(jwtSection["SigningKey"], builder.Environment);
builder.AddConexyAuthSecurity();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSection["Issuer"],
            ValidAudience = jwtSection["Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey))
        };

        // SignalR clients send the token via the query string ("access_token").
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs/conexy"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = AuthSecurityExtensions.OnTokenValidatedAsync
        };
    });

builder.Services.AddAuthorization();

// Application services.
builder.Services.AddScoped<IConexyRepository, ConexyRepository>();
builder.Services.AddScoped<IChatHistoryRepository, ChatHistoryRepository>();
// GITHUB_OAUTH: добавлено 2026-09-19
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IGitHubOAuthService, GitHubOAuthService>();
// EMAIL_AUTH: добавлено 2026-09-19
builder.Services.AddScoped<IEmailAuthService, EmailAuthService>();
// EMAIL_VERIFICATION: добавлено 2026-09-24
builder.Services.AddScoped<IEmailVerificationRepository, EmailVerificationRepository>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<IEmailVerificationService, EmailVerificationService>();
// EMAIL_VERIFICATION: код живёт по своему таймеру (срок годности и кулдаун повторной отправки),
// поэтому часы берём из DI — в тестах их подменяет ManualTime.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IConexyService, ConexyService>();
builder.Services.AddScoped<IConexyAgentRunner, ConexyAgentRunner>();
// CONVERSATION_SERVICE: добавлено 2026-09-23 — единая сборка контекста и запись хода.
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<IConexyEditorService, ConexyEditorService>();
builder.Services.AddScoped<IConexyBashService, ConexyBashService>();
// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
builder.Services.AddScoped<IPendingActionRepository, PendingActionRepository>();
builder.Services.AddSingleton<IDangerousCommandClassifier, DangerousCommandClassifier>();
// COMMAND_APPROVAL: добавлено 2026-09-22 — read-only команды выполняются без подтверждения.
builder.Services.AddSingleton<ICommandApprovalClassifier, CommandApprovalClassifier>();
builder.Services.AddSingleton<IPendingActionService, PendingActionService>();
// SUBSCRIPTION_TIERS: добавлено 2026-09-17
builder.Services.AddScoped<IUserMemoryRepository, UserMemoryRepository>();
builder.Services.AddScoped<IUsageRepository, UsageRepository>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
// YOOKASSA: добавлено 2026-09-27 — платежи: локальные записи, создание платежа и выдача тарифа.
builder.Services.AddScoped<IPaymentRepository, PaymentRepository>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IUserMemoryService, UserMemoryService>();
builder.Services.AddSingleton<IMemoryExtractionQueue, MemoryExtractionQueue>();
// CHAT_TITLE_TOPIC: очередь названий чатов (генерация идёт воркером ниже).
builder.Services.AddSingleton<IChatTitleQueue, ChatTitleQueue>();
// INCOGNITO_CHAT: добавлено 2026-09-20
builder.Services.AddSingleton<IIncognitoChatStore, IncognitoChatStore>();
// INCOGNITO_CHAT: добавлено 2026-09-24 — ревью M7: файлы просроченных инкогнито-тредов удаляются.
builder.Services.AddHostedService<IncognitoCleanupService>();
// CHAT_OWNERSHIP: добавлено 2026-09-24 — ревью C1: владелец каждого chat id.
builder.Services.AddScoped<IChatAccessService, ChatAccessService>();
// USER_PREFERENCES: добавлено 2026-09-24 — пользовательские инструкции и переключатель памяти.
builder.Services.AddScoped<IUserPreferencesService, UserPreferencesService>();
// USER_DATA_CLEANUP: добавлено 2026-09-24 — ревью M9: файлы пользователя удаляются вместе с ним.
builder.Services.AddScoped<IUserDataCleanupService, UserDataCleanupService>();
// RAG: добавлено 2026-09-17
builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
// SANDBOX: добавлено 2026-09-17
builder.Services.AddSingleton<IDockerSandboxRunner, DockerSandboxRunner>();
// SANDBOX_SESSIONS: добавлено 2026-09-23 — состояние песочницы на сессию + автоочистка по простою.
builder.Services.AddSingleton<ISandboxSessionStore, SandboxSessionStore>();
builder.Services.AddHostedService<SandboxIdleCleanupService>();
// SUPPORT: добавлено 2026-09-19
builder.Services.AddScoped<ISupportRepository, SupportRepository>();
builder.Services.AddScoped<ISupportService, SupportService>();

// WORKSPACE_JAIL: добавлено 2026-09-24 — ревью C2: общий слот команды песочницы на воркспейс.
builder.Services.AddSingleton<ISandboxActivity, SandboxActivity>();
builder.Services.AddSingleton<IConexyWorkspaceService, ConexyWorkspaceService>();
// SANDBOX_TERMINAL: добавлено 2026-09-24 — ТЗ 2, §6: построчный терминал в песочнице.
builder.Services.AddSingleton<ISandboxTerminalService, SandboxTerminalService>();
builder.Services.AddSingleton<IWorkspacePathValidator, WorkspacePathValidator>();
builder.Services.AddSingleton<IConexyEditorStateService, ConexyEditorStateService>();
builder.Services.AddSingleton<IIdeTerminalService, IdeTerminalService>();
builder.Services.AddSingleton<IIdeFileService, IdeFileService>();
builder.Services.AddSingleton<IConexyRunService, ConexyRunService>();
builder.Services.AddSingleton<IConexyVisionService, ConexyVisionService>();
// BROWSER_AUTOMATION: один Chromium на процесс (скриншоты + браузерные инструменты) и DOM-автоматизация.
builder.Services.AddSingleton<IChromiumHost, ChromiumHost>();
builder.Services.AddSingleton<IConexyBrowserService, ConexyBrowserService>();
builder.Services.AddSingleton<IConexyGitHubService, ConexyGitHubService>();
builder.Services.AddSingleton<IConexyQueue, ConexyQueue>();
builder.Services.AddSingleton<IConexyQueueGuard, ConexyQueueGuard>();
builder.Services.AddSingleton<IConexyCancellationRegistry, ConexyCancellationRegistry>();
builder.Services.AddSingleton<IConexyTodoService, ConexyTodoService>();
builder.Services.AddSingleton<ITokenService, JwtTokenService>();

builder.Services.AddHostedService<ConexyBackgroundWorker>();
// SUBSCRIPTION_TIERS: добавлено 2026-09-17
builder.Services.AddHostedService<MemoryExtractionWorker>();
// CHAT_TITLE_TOPIC: фоновое название чата по теме (flash-модель, лимиты пользователя не тратит).
builder.Services.AddHostedService<ChatTitleWorker>();
builder.Services.AddHttpClient<IConexyLlmClient, ConexyLlmClient>();
builder.Services.AddHttpClient<IWebSearchService, JsonSeoSearchService>();
// AGENT_WEB_TOOLS: добавлено 2026-09-24 — начало блока агентских инструментов (ws/be-agent).
// SSRF-гард (DNS-резолвер как шов для тестов) нужен fetch_web_page и take_screenshot; у
// WebPageFetcher свой SocketsHttpHandler с пиннингом проверенного IP, поэтому не AddHttpClient.
builder.Services.AddSingleton<ConexyAI.Service.Web.IHostAddressResolver, ConexyAI.Service.Web.DnsHostAddressResolver>();
builder.Services.AddSingleton<ConexyAI.Service.Web.PublicUrlGuard>();
builder.Services.AddSingleton<ConexyAI.Service.Web.IWebPageFetcher, ConexyAI.Service.Web.WebPageFetcher>();
// SEARCH_USER_CHATS: поиск по прошлым чатам пользователя для search_user_chats (только чтение).
builder.Services.AddScoped<IChatSearchRepository, ChatSearchRepository>();
// AGENT_WEB_TOOLS: конец блока.
builder.Services.AddHttpClient<ISpeechKitService, SpeechKitService>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<SpeechKitOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 30);
});
// YOOKASSA: таймаут обращения к API платёжного провайдера.
builder.Services.AddHttpClient<IYooKassaClient, YooKassaClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<YooKassaSettings>>().Value;
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 30);
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<DbConexy>();
    await dbContext.Database.MigrateAsync();
}

// LEGAL_DOCS: добавлено 2026-09-26 — реквизиты оператора приходят из окружения, и пустые значения
// не ломают старт, а просто превращают политику, оферту и политику возврата в документы без ФИО и
// ИНН. Это легко не заметить на живой странице, поэтому о пропущенных настройках предупреждаем в логах.
var operatorSettings = app.Services.GetRequiredService<IOptions<OperatorSettings>>().Value;
if (string.IsNullOrWhiteSpace(operatorSettings.Name) || string.IsNullOrWhiteSpace(operatorSettings.Inn))
{
    app.Logger.LogWarning(
        "Legal pages: Operator__Name / Operator__Inn (и остальные Operator__*) не заданы — " +
        "правовые документы будут опубликованы без реквизитов оператора.");
}

// LEGAL_POLICY_VERSION: версия согласия (LegalPolicy.CurrentVersion) должна совпадать с датой
// последней редакции документов (Operator__UpdatedAt). Если .env остался на прошлой редакции, а
// версия в коде выросла, пользователи соглашаются с одной редакцией, а на сайте показана другая.
// Расхождение легко пропустить, поэтому предупреждаем на старте.
if (!string.Equals(operatorSettings.UpdatedAt, LegalPolicy.CurrentVersion, StringComparison.Ordinal))
{
    app.Logger.LogWarning(
        "Legal pages: Operator__UpdatedAt ({Updated}) не совпадает с LegalPolicy.CurrentVersion ({Version}) — " +
        "версия согласия и дата редакции документов разошлись.",
        string.IsNullOrWhiteSpace(operatorSettings.UpdatedAt) ? "не задана" : operatorSettings.UpdatedAt,
        LegalPolicy.CurrentVersion);
}

// YOOKASSA: добавлено 2026-09-27
var yooKassaSettings = app.Services.GetRequiredService<IOptions<YooKassaSettings>>().Value;
if (!yooKassaSettings.IsConfigured)
{
    app.Logger.LogWarning(
        "Payments: YooKassa__ShopId / YooKassa__SecretKey не заданы — кнопки покупки тарифов " +
        "будут отвечать ошибкой 503, оплата недоступна.");
}
else if (string.IsNullOrWhiteSpace(yooKassaSettings.NotificationIpRanges))
{
    app.Logger.LogWarning(
        "Payments: YooKassa__NotificationIpRanges не задан — проверка источника вебхука по IP " +
        "ОТКЛЮЧЕНА. Платёж всё равно подтверждается через API ЮKassa, но список подсетей лучше задать.");
}

// Force the workspace service to initialize once at startup: it resolves the
// (isolated) root path, creates the directory, and migrates any legacy sandbox
// out of the repository. Its constructor logs the resolved absolute path.
_ = app.Services.GetRequiredService<IConexyWorkspaceService>();

LogEnvironmentPrerequisites(app);

app.UseConexyForwardedHeaders();

// ATTACHMENT_SIZE_LIMIT: Kestrel обрывает чтение тела на лимите и бросает BadHttpRequestException
// со статусом 413. Без этого клиент видит только пустое "413 Request Entity Too Large", поэтому
// отдаём понятный JSON-маркер, по которому SPA показывает человеческое сообщение.
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (BadHttpRequestException ex)
        when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(
            new { error = "PAYLOAD_TOO_LARGE" }, context.RequestAborted);
    }
});

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseConexyRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapHub<ConexyHub>("/hubs/conexy");

app.Run();

static void LogEnvironmentPrerequisites(WebApplication app)
{
    var logger = app.Logger;

    // GITHUB_PAT_PER_USER: серверный PAT больше НЕ нужен для github_action — каждый пользователь
    // вводит свой токен в настройках, и коммиты идут от его аккаунта. Проверка/предупреждение убраны,
    // чтобы не путать: без личного токена инструмент просто откажет с подсказкой.

    // GITHUB_ACTION_HOST_GIT: github_action запускает git НА ХОСТЕ бэкенда (клонирование, ветки,
    // commit+push). Runtime-образ dotnet/aspnet не содержит git, а без него инструмент падал с
    // невнятным «Could not read the repository configuration» — это просто "git не найден".
    // Проверяем и говорим прямо, вместо того чтобы молча ломаться у каждого пользователя.
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var git = System.Diagnostics.Process.Start(psi);
        if (git is not null && git.WaitForExit(5000) && git.ExitCode == 0)
            logger.LogInformation("git is available on the backend host: github_action can clone, branch and push.");
        else
            logger.LogWarning("git is NOT available on the backend host: github_action will fail for every user. Install git in the runtime image (see ConexyAI/Dockerfile).");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "git is NOT available on the backend host: github_action will fail for every user. Install git in the runtime image (see ConexyAI/Dockerfile).");
    }

    // Playwright Chromium — required for take_screenshot.
    // AGENT_TOOL_FAILURES: добавлено 2026-09-23. Раньше здесь утверждалось, что Chromium «ставится при
    // первом использовании» через npx — но runtime-образ это dotnet/aspnet без node/npm/npx, то есть
    // в проде установить его изнутри контейнера невозможно. Теперь мы честно проверяем окружение и
    // сообщаем, будет ли инструмент вообще предложен агенту (см. ConexyAgentRunner.ResolveToolsAsync).
    try
    {
        var vision = app.Services.GetRequiredService<IConexyVisionService>();
        if (vision.IsAvailableAsync().GetAwaiter().GetResult())
        {
            logger.LogInformation("Playwright Chromium is available: take_screenshot is offered to the agent.");
        }
        else
        {
            logger.LogWarning(
                "Playwright Chromium is NOT available: take_screenshot and the browser_* tools are hidden " +
                "from the agent. Install a Chromium in the runtime image (see ConexyAI/Dockerfile) and point " +
                "PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH at it, or ship the Playwright browser.");
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not probe Playwright Chromium availability; take_screenshot stays hidden.");
    }
}
