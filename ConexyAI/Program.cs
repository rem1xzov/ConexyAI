using System.Text;
using ConexyAI.Configuration;
using ConexyAI.DbContext;
using ConexyAI.Hub;
using ConexyAI.Repository;
using ConexyAI.Service;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSignalR();

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
// RAG: добавлено 2026-09-17
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));
// SANDBOX: добавлено 2026-09-17
builder.Services.Configure<SandboxOptions>(builder.Configuration.GetSection(SandboxOptions.SectionName));
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
var signingKey = jwtSection["SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is not configured.");

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
            }
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
builder.Services.AddScoped<IConexyService, ConexyService>();
builder.Services.AddScoped<IConexyAgentRunner, ConexyAgentRunner>();
builder.Services.AddScoped<IConexyEditorService, ConexyEditorService>();
builder.Services.AddScoped<IConexyBashService, ConexyBashService>();
// DANGEROUS_CMD_CONFIRM: добавлено 2026-09-17
builder.Services.AddScoped<IPendingActionRepository, PendingActionRepository>();
builder.Services.AddSingleton<IDangerousCommandClassifier, DangerousCommandClassifier>();
builder.Services.AddSingleton<IPendingActionService, PendingActionService>();
// SUBSCRIPTION_TIERS: добавлено 2026-09-17
builder.Services.AddScoped<IUserMemoryRepository, UserMemoryRepository>();
builder.Services.AddScoped<IUsageRepository, UsageRepository>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IUserMemoryService, UserMemoryService>();
builder.Services.AddSingleton<IMemoryExtractionQueue, MemoryExtractionQueue>();
// RAG: добавлено 2026-09-17
builder.Services.AddScoped<IDocumentRepository, DocumentRepository>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
// SANDBOX: добавлено 2026-09-17
builder.Services.AddSingleton<IDockerSandboxRunner, DockerSandboxRunner>();

builder.Services.AddSingleton<IConexyWorkspaceService, ConexyWorkspaceService>();
builder.Services.AddSingleton<IWorkspacePathValidator, WorkspacePathValidator>();
builder.Services.AddSingleton<IConexyEditorStateService, ConexyEditorStateService>();
builder.Services.AddSingleton<IIdeTerminalService, IdeTerminalService>();
builder.Services.AddSingleton<IIdeFileService, IdeFileService>();
builder.Services.AddSingleton<IConexyRunService, ConexyRunService>();
builder.Services.AddSingleton<IConexyVisionService, ConexyVisionService>();
builder.Services.AddSingleton<IConexyGitHubService, ConexyGitHubService>();
builder.Services.AddSingleton<IConexyQueue, ConexyQueue>();
builder.Services.AddSingleton<IConexyQueueGuard, ConexyQueueGuard>();
builder.Services.AddSingleton<IConexyCancellationRegistry, ConexyCancellationRegistry>();
builder.Services.AddSingleton<IConexyTodoService, ConexyTodoService>();
builder.Services.AddSingleton<ITokenService, JwtTokenService>();

builder.Services.AddHostedService<ConexyBackgroundWorker>();
// SUBSCRIPTION_TIERS: добавлено 2026-09-17
builder.Services.AddHostedService<MemoryExtractionWorker>();
builder.Services.AddHttpClient<IConexyLlmClient, ConexyLlmClient>();
builder.Services.AddHttpClient<IWebSearchService, JsonSeoSearchService>();
builder.Services.AddHttpClient<ISpeechKitService, SpeechKitService>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<SpeechKitOptions>>().Value;
    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 30);
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<DbConexy>();
    await dbContext.Database.MigrateAsync();
}

// Force the workspace service to initialize once at startup: it resolves the
// (isolated) root path, creates the directory, and migrates any legacy sandbox
// out of the repository. Its constructor logs the resolved absolute path.
_ = app.Services.GetRequiredService<IConexyWorkspaceService>();

LogEnvironmentPrerequisites(app);

app.UseHttpsRedirection();
app.UseCors("AllowFrontend");
app.UseAuthentication();
app.UseAuthorization();

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
    var configuration = app.Configuration;

    // GitHub PAT (env var or settings) — required for github_action workflows.
    var pat = configuration["GitHub:PersonalAccessToken"]
        ?? Environment.GetEnvironmentVariable("GITHUB_PAT")
        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");

    if (string.IsNullOrWhiteSpace(pat))
    {
        logger.LogWarning("GitHub PAT is not configured. Set GitHub:PersonalAccessToken in appsettings.json or the GITHUB_PAT environment variable to enable the github_action tool.");
    }

    // Playwright Chromium — required for take_screenshot.
    logger.LogInformation("Playwright Chromium is installed on first use, or manually with: npx playwright install chromium");
}
