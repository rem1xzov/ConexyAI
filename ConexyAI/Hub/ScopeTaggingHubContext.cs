using Microsoft.AspNetCore.SignalR;

namespace ConexyAI.Hub;

// STREAM_SCOPE_TAG: добавлено 2026-09-24 — ревью H6.
//
// Одно соединение браузера одновременно состоит в нескольких группах task_{id}: в группе текущего хода,
// в группе воркспейса чата и — после переключения чата — в группах чужих для этого экрана ходов. События
// же (OnContentToken и остальные) не несли никакого признака, КУДА они относятся, и клиент дописывал
// токены хода A в пузырь хода B. Вместо правки трёх десятков SendAsync по коду этот контекст хаба
// добавляет ко ВСЕМ событиям группы task_{id} последний аргумент — сам {id}. Новый вызов SendAsync
// получает метку автоматически и не может её «забыть».

/// <summary>
/// <see cref="IHubContext{THub}"/> for <see cref="ConexyHub"/> that appends the group's scope id as the
/// last argument of every message sent to a <c>task_{id}</c> group. Everything else is passed through.
/// </summary>
public sealed class ScopeTaggingHubContext : IHubContext<ConexyHub>
{
    public const string TaskGroupPrefix = "task_";

    public ScopeTaggingHubContext(HubLifetimeManager<ConexyHub> lifetimeManager)
    {
        Clients = new TaggingClients(lifetimeManager);
        Groups = new GroupManager(lifetimeManager);
    }

    public IHubClients Clients { get; }

    public IGroupManager Groups { get; }

    /// <summary>The id to append for <paramref name="groupName"/>, or null for groups that are not tagged.</summary>
    internal static string? ScopeOf(string groupName) =>
        groupName.StartsWith(TaskGroupPrefix, StringComparison.Ordinal) ? groupName[TaskGroupPrefix.Length..] : null;

    internal static object?[] Tag(object?[] args, string scope)
    {
        var tagged = new object?[args.Length + 1];
        Array.Copy(args, tagged, args.Length);
        tagged[^1] = scope;
        return tagged;
    }

    private sealed class TaggingClients : IHubClients
    {
        private readonly HubLifetimeManager<ConexyHub> _lifetime;

        public TaggingClients(HubLifetimeManager<ConexyHub> lifetime) => _lifetime = lifetime;

        public IClientProxy All => new Proxy((m, a, ct) => _lifetime.SendAllAsync(m, a, ct));

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) =>
            new Proxy((m, a, ct) => _lifetime.SendAllExceptAsync(m, a, excludedConnectionIds, ct));

        public ISingleClientProxy Client(string connectionId) => new SingleProxy(_lifetime, connectionId);

        IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => Client(connectionId);

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) =>
            new Proxy((m, a, ct) => _lifetime.SendConnectionsAsync(connectionIds, m, a, ct));

        public IClientProxy Group(string groupName)
        {
            var scope = ScopeOf(groupName);
            return new Proxy((m, a, ct) =>
                _lifetime.SendGroupAsync(groupName, m, scope is null ? a : Tag(a, scope), ct));
        }

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds)
        {
            var scope = ScopeOf(groupName);
            return new Proxy((m, a, ct) =>
                _lifetime.SendGroupExceptAsync(groupName, m, scope is null ? a : Tag(a, scope), excludedConnectionIds, ct));
        }

        public IClientProxy Groups(IReadOnlyList<string> groupNames) =>
            // Several groups cannot share one scope tag: send to each so each gets its own.
            new Proxy(async (m, a, ct) =>
            {
                foreach (var name in groupNames)
                {
                    var scope = ScopeOf(name);
                    await _lifetime.SendGroupAsync(name, m, scope is null ? a : Tag(a, scope), ct);
                }
            });

        public IClientProxy User(string userId) =>
            new Proxy((m, a, ct) => _lifetime.SendUserAsync(userId, m, a, ct));

        public IClientProxy Users(IReadOnlyList<string> userIds) =>
            new Proxy((m, a, ct) => _lifetime.SendUsersAsync(userIds, m, a, ct));
    }

    private sealed class Proxy : IClientProxy
    {
        private readonly Func<string, object?[], CancellationToken, Task> _send;

        public Proxy(Func<string, object?[], CancellationToken, Task> send) => _send = send;

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            _send(method, args, cancellationToken);
    }

    private sealed class SingleProxy : ISingleClientProxy
    {
        private readonly HubLifetimeManager<ConexyHub> _lifetime;
        private readonly string _connectionId;

        public SingleProxy(HubLifetimeManager<ConexyHub> lifetime, string connectionId)
        {
            _lifetime = lifetime;
            _connectionId = connectionId;
        }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
            _lifetime.SendConnectionAsync(_connectionId, method, args, cancellationToken);

        public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default) =>
            _lifetime.InvokeConnectionAsync<T>(_connectionId, method, args, cancellationToken);
    }

    private sealed class GroupManager : IGroupManager
    {
        private readonly HubLifetimeManager<ConexyHub> _lifetime;

        public GroupManager(HubLifetimeManager<ConexyHub> lifetime) => _lifetime = lifetime;

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            _lifetime.AddToGroupAsync(connectionId, groupName, cancellationToken);

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            _lifetime.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);
    }
}
