using ConexyAI.Desktop.Tools;

namespace ConexyAI.Desktop;

/// <summary>Спрашивает в консоли: y — выполнить, n — отклонить, a — разрешать всё до конца сессии.</summary>
public sealed class ConsoleApprover : ICommandApprover
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _allowAll;

    public async Task<bool> ApproveAsync(string command, string workingDirectory, CancellationToken ct)
    {
        if (_allowAll)
            return true;

        await _gate.WaitAsync(ct);
        try
        {
            if (_allowAll)
                return true;

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Агент хочет выполнить команду в {workingDirectory}:");
            Console.ResetColor();
            Console.WriteLine("  " + command);
            Console.Write("Разрешить? [y]да / [n]нет / [a]да, и дальше без вопросов: ");

            var answer = await Task.Run(Console.ReadLine, ct);
            switch (answer?.Trim().ToLowerInvariant())
            {
                case "a":
                    _allowAll = true;
                    return true;
                case "y":
                case "д":
                case "да":
                case "yes":
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
