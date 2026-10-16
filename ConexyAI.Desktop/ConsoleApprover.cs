using ConexyAI.Desktop.Tools;

namespace ConexyAI.Desktop;

/// <summary>
/// Подтверждение команд терминала: «Да» / «Да, и больше не спрашивать» (авто-режим) / «Нет».
/// Авто-режим можно включить флагом --auto или командой /auto. Запрещённые политикой команды
/// блокируются в любом режиме.
/// </summary>
public sealed class ConsoleApprover : ICommandApprover
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _auto;

    public bool AutoMode
    {
        get => _auto;
        set => _auto = value;
    }

    public Task<bool> ApproveAsync(string command, string workingDirectory, CancellationToken ct) =>
        ApproveAsync("Bash", command, workingDirectory, ct);

    public async Task<bool> ApproveAsync(string title, string command, string workingDirectory, CancellationToken ct)
    {
        if (_auto)
            return true;

        await _gate.WaitAsync(ct);
        try
        {
            if (_auto)
                return true;

            if (!Ui.Ansi)
                return await AskPlainAsync(command, workingDirectory, ct);

            var choice = await Ui.ConfirmAsync(title, command, workingDirectory, ct);
            if (choice == 1)
                _auto = true;
            var allowed = choice is 0 or 1;
            Ui.Decision(allowed);
            if (allowed)
                Ui.Busy("Выполняю");
            return allowed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> AskPlainAsync(string command, string workingDirectory, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"Агент хочет выполнить команду в {workingDirectory}:");
        Console.WriteLine("  " + command);
        Console.Write("Разрешить? [y]да / [n]нет / [a]да, и дальше без вопросов: ");

        var answer = await Task.Run(Console.ReadLine, ct);
        switch (answer?.Trim().ToLowerInvariant())
        {
            case "a":
                _auto = true;
                return true;
            case "y" or "д" or "да" or "yes":
                return true;
            default:
                return false;
        }
    }
}
