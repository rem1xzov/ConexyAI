namespace ConexyAI.Contract;

// GITHUB_PAT_PER_USER: добавлено 2026-09-30
/// <summary>
/// Идентичность GitHub-аккаунта, которому принадлежит персональный токен пользователя.
/// Нужна, чтобы коммиты агента были авторизованы и подписаны именем самого пользователя,
/// а не общим «Conexy AI Agent».
/// </summary>
public sealed record GitHubIdentity(string Login, long Id);
