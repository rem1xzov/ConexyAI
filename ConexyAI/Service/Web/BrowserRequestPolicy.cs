namespace ConexyAI.Service.Web;

/// <summary>What the screenshot browser may do with one request.</summary>
public enum BrowserRequestAction
{
    /// <summary>Abort the request.</summary>
    Block,

    /// <summary>Serve <see cref="BrowserRequestDecision.FilePath"/> from the chat workspace.</summary>
    ServeWorkspaceFile,

    /// <summary>Fetch it from the public internet (redirect hops are checked again).</summary>
    FetchPublic,

    /// <summary>Let the browser handle it (in-memory schemes such as <c>data:</c> and <c>blob:</c>).</summary>
    Continue,
}

public sealed record BrowserRequestDecision(BrowserRequestAction Action, string? FilePath = null, string? Reason = null);

/// <summary>Thrown when a screenshot target is refused; the message is meant for the model.</summary>
public sealed class ScreenshotTargetException : Exception
{
    public ScreenshotTargetException(string message) : base(message) { }
}

// AGENT_WEB_TOOLS / H9: добавлено 2026-09-24
/// <summary>
/// Network policy of the <c>take_screenshot</c> browser. Previously the tool rendered ANY URL or path:
/// <c>/proc/self/environ</c> or <c>http://docker-socket-proxy:2375/...</c> ended up in a screenshot, and
/// relative paths resolved against the backend's <c>/app</c> instead of the chat workspace.
/// <list type="bullet">
/// <item>Workspace files are never opened through <c>file://</c> (Chromium would then read any host
/// file an HTML page references). They are served from a synthetic origin,
/// <c>http://workspace.conexy.invalid/…</c>, and every request to it is resolved through
/// <see cref="IWorkspacePathValidator"/> — the same jail as the file tools.</item>
/// <item>http(s) must pass <see cref="PublicUrlGuard"/>, for the page itself and every sub-resource.</item>
/// <item>Everything else (<c>file:</c>, <c>chrome:</c>, <c>ftp:</c>…) is blocked.</item>
/// </list>
/// </summary>
public sealed class BrowserRequestPolicy
{
    /// <summary>Synthetic host for workspace files; <c>.invalid</c> never resolves in real DNS.</summary>
    public const string WorkspaceHost = "workspace.conexy.invalid";

    // Путь, под которым рабочая область видна агенту в песочнице (bash): модель часто его и называет.
    private const string SandboxMountPrefix = "/workspace";

    private readonly PublicUrlGuard _guard;
    private readonly IWorkspacePathValidator _paths;

    public BrowserRequestPolicy(PublicUrlGuard guard, IWorkspacePathValidator paths)
    {
        _guard = guard;
        _paths = paths;
    }

    /// <summary>
    /// Turns the model's target into the URL the browser navigates to. http(s) must be public; any other
    /// target is a path inside the chat workspace (relative, <c>/workspace/…</c> as the sandbox shows
    /// it, or <c>file://</c> of a file inside the workspace) and becomes a <see cref="WorkspaceHost"/> URL.
    /// </summary>
    /// <exception cref="ScreenshotTargetException">The target is not allowed or does not exist.</exception>
    public async Task<Uri> ResolveTargetAsync(Guid chatId, string target, CancellationToken ct = default)
    {
        var trimmed = (target ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            throw new ScreenshotTargetException("take_screenshot requires 'url'.");

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var web))
                throw new ScreenshotTargetException($"Некорректный URL: '{trimmed}'.");

            var check = await _guard.CheckAsync(web, ct);
            if (!check.Allowed)
                throw new ScreenshotTargetException($"Адрес запрещён: {check.Error}");
            return web;
        }

        var path = trimmed;
        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var fileUri) || !fileUri.IsFile)
                throw new ScreenshotTargetException($"Некорректный file-URL: '{trimmed}'.");
            path = fileUri.LocalPath;
        }
        else if (path.Contains("://", StringComparison.Ordinal))
        {
            throw new ScreenshotTargetException("Разрешены только http(s)-адреса и файлы из рабочей области чата.");
        }

        var fullPath = ResolveWorkspacePath(chatId, path)
            ?? throw new ScreenshotTargetException(
                $"Путь '{trimmed}' вне рабочей области чата. Укажи путь относительно рабочей области (например 'index.html').");

        if (Directory.Exists(fullPath))
            fullPath = Path.Combine(fullPath, "index.html");
        if (!File.Exists(fullPath))
            throw new ScreenshotTargetException($"Файл '{trimmed}' не найден в рабочей области.");

        var root = _paths.ResolveSafePath(chatId, ".");
        var relative = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        var escaped = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        return new Uri($"http://{WorkspaceHost}/{escaped}");
    }

    /// <summary>Decides one browser request (the page itself, a sub-resource or a redirect hop).</summary>
    public async Task<BrowserRequestDecision> DecideAsync(Guid chatId, string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return new BrowserRequestDecision(BrowserRequestAction.Block, Reason: "malformed URL");

        switch (uri.Scheme)
        {
            case "http" or "https" when string.Equals(uri.Host, WorkspaceHost, StringComparison.OrdinalIgnoreCase):
            {
                var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
                var fullPath = ResolveWorkspacePath(chatId, relative.Length == 0 ? "index.html" : relative);
                return fullPath is null
                    ? new BrowserRequestDecision(BrowserRequestAction.Block, Reason: "outside the chat workspace")
                    : new BrowserRequestDecision(BrowserRequestAction.ServeWorkspaceFile, FilePath: fullPath);
            }

            case "http" or "https":
            {
                var check = await _guard.CheckAsync(uri, ct);
                return check.Allowed
                    ? new BrowserRequestDecision(BrowserRequestAction.FetchPublic)
                    : new BrowserRequestDecision(BrowserRequestAction.Block, Reason: check.Error);
            }

            case "data" or "blob" or "about":
                return new BrowserRequestDecision(BrowserRequestAction.Continue);

            default:
                // file:, chrome:, ftp:, ws: … — never.
                return new BrowserRequestDecision(BrowserRequestAction.Block, Reason: $"scheme '{uri.Scheme}' is not allowed");
        }
    }

    /// <summary>Absolute path inside the chat workspace, or null when <paramref name="path"/> escapes it.</summary>
    private string? ResolveWorkspacePath(Guid chatId, string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized == SandboxMountPrefix)
            normalized = ".";
        else if (normalized.StartsWith(SandboxMountPrefix + "/", StringComparison.Ordinal))
            normalized = normalized[(SandboxMountPrefix.Length + 1)..];

        try
        {
            // An absolute host path is accepted only if it is inside this chat's workspace; the
            // validator rejects everything else (including "..", other chats and /proc).
            return _paths.ResolveSafePath(chatId, normalized);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
