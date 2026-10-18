using System.Text;
using System.Text.Json;
using ConexyAI.Contract;
using ConexyAI.Hub;
using Microsoft.AspNetCore.SignalR;

namespace ConexyAI.Service.Device;

// WEB_DEVICE: добавлено 2026-10-18 — итоговый diff хода, выполненного на ПК по запросу из веба.
// Сервер молча (не как инструмент модели) вызывает на устройстве turn_diff, рассылает результат
// событием OnDeviceDiff и возвращает блок для сохранённого ответа, чтобы diff был виден после перезагрузки.
public static class DeviceTurnDiff
{
    /// <summary>Служебный инструмент устройства; модели не предлагается.</summary>
    public const string Tool = "turn_diff";

    public const string Heading = "**Изменения на ПК:**";
    private const int MaxStoredChars = 100_000;
    private const int MaxFiles = 500;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public sealed record FileDiff(string Path, string Status, string Diff);

    /// <summary>
    /// Для хода в десктопном режиме из веба (RemoteApprovals) с подключённым ПК: рассылает OnDeviceDiff и
    /// возвращает markdown-блок для сохранённого ответа (пусто, если изменений нет или diff недоступен).
    /// </summary>
    public static async Task<string> CollectAsync(
        IServiceProvider services, IHubContext<ConexyHub> hub, ConexyJob job, ILogger logger, CancellationToken ct)
    {
        if (!job.UseDevice || !job.RemoteApprovals)
            return string.Empty;
        var bridge = services.GetService<IDeviceToolBridge>();
        if (bridge is null || !bridge.IsOnline(job.UserId))
            return string.Empty;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var outcome = await bridge.ExecuteAsync(job.UserId, job.TaskId, Tool, "{}", timeout.Token, job.DeviceProjectId);
            if (outcome.IsError)
                return string.Empty;

            var (files, truncated) = Parse(outcome.Output);
            await hub.Clients.Group($"task_{job.TaskId}").SendAsync("OnDeviceDiff", new
            {
                taskId = job.TaskId,
                files = files.Select(f => new { path = f.Path, status = f.Status, diff = f.Diff }),
                truncated,
            }, ct);
            return BuildStoredBlock(files);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Device turn diff unavailable for task {TaskId}: {Error}", job.TaskId, ex.GetType().Name);
            return string.Empty;
        }
    }

    public static (List<FileDiff> Files, bool Truncated) Parse(string json)
    {
        var files = new List<FileDiff>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var truncated = root.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (files.Count >= MaxFiles)
                {
                    truncated = true;
                    break;
                }
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var path = Str(item, "path");
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                var status = Str(item, "status") switch
                {
                    "added" => "added",
                    "deleted" => "deleted",
                    _ => "modified",
                };
                files.Add(new FileDiff(path, status, Str(item, "diff") ?? string.Empty));
            }
        }
        return (files, truncated);

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public static string BuildStoredBlock(IReadOnlyList<FileDiff> files)
    {
        if (files.Count == 0)
            return string.Empty;

        var diff = new StringBuilder();
        foreach (var file in files)
        {
            var text = string.IsNullOrEmpty(file.Diff) ? $"# {file.Status}: {file.Path}" : file.Diff.TrimEnd('\n');
            if (diff.Length + text.Length + 1 > MaxStoredChars)
            {
                diff.Append("# ... diff обрезан\n");
                break;
            }
            diff.Append(text).Append('\n');
        }

        // Тройные бэктики внутри diff закрыли бы блок раньше времени — берём ограждение длиннее любого в тексте.
        var fence = "```";
        while (diff.ToString().Contains(fence, StringComparison.Ordinal))
            fence += "`";
        return $"\n\n{Heading}\n\n{fence}diff\n{diff}{fence}";
    }
}
