namespace ConexyAI.Desktop;

/// <summary>
/// ATTENTION_BADGE: «агент закончил» для круга-компаньона. Клиент ставит флаг только когда его окно не активно
/// (человек в другом приложении или окно свёрнуто) и убирает, когда человек вернулся в Conexy или открыл его
/// с круга. Пока окно на переднем плане, значок не показывается вообще.
/// </summary>
public static class Attention
{
    private static Thread? _watch;
    private static readonly object Sync = new();

    public static string FilePath => Path.Combine(ProjectStore.ConfigDir, "attention.pending");

    public static void Set(string reason)
    {
        try
        {
            Directory.CreateDirectory(ProjectStore.ConfigDir);
            File.WriteAllText(FilePath, reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Ставит флаг, если окно клиента не на переднем плане (иначе человек и так всё видит).</summary>
    public static void SetIfUnfocused(string reason)
    {
        if (!Bubble.IsClientWindowForeground())
            Set(reason);
    }

    /// <summary>Раз в секунду снимает флаг, когда окно клиента снова активно.</summary>
    public static void Watch()
    {
        lock (Sync)
        {
            if (_watch is not null)
                return;
            _watch = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (Bubble.IsClientWindowForeground())
                            Clear();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    Thread.Sleep(1000);
                }
            })
            { IsBackground = true, Name = "conexy-attention" };
            _watch.Start();
        }
    }
}
