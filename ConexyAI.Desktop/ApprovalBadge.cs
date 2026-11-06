namespace ConexyAI.Desktop;

/// <summary>
/// APPROVAL_BADGE: пока агент ждёт подтверждения, на круге-компаньоне горит значок уведомления. Кружок
/// живёт в отдельном процессе, поэтому счётчик передаётся файлом: клиент пишет, сколько подтверждений
/// ждёт ответа, и удаляет файл, когда ответили все.
/// </summary>
public static class ApprovalBadge
{
    private static int _count;
    private static readonly object Sync = new();

    public static string FilePath => Bubble.ApprovalFile;

    /// <summary>Подтверждение началось. Освободить результат — когда ответ получен или отменён.</summary>
    public static IDisposable Enter()
    {
        lock (Sync)
        {
            _count++;
            Write(_count);
        }
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            lock (Sync)
            {
                _count = Math.Max(0, _count - 1);
                Write(_count);
            }
        }
    }

    private static void Write(int count)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
            if (count > 0)
                File.WriteAllText(FilePath, count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Значок — не повод мешать подтверждению.
        }
    }
}
