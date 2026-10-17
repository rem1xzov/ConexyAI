using ConexyAI.Desktop.Tools;

namespace ConexyAI.Desktop;

/// <summary>
/// Подтверждение команд терминала: «Да» / «Да, всегда для этой команды в проекте» / «Да, и больше не
/// спрашивать» (авто-режим) / «Нет». Авто-режим можно включить флагом --auto или командой /auto.
/// Запрещённые политикой команды блокируются в любом режиме.
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

    /// <summary>Корень текущего проекта — к нему привязаны постоянные разрешения.</summary>
    public string? ProjectRoot { get; set; }

    public PermissionStore Permissions { get; } = PermissionStore.Load();

    public Task<bool> ApproveAsync(string command, string workingDirectory, CancellationToken ct) =>
        ApproveAsync("Bash", command, workingDirectory, ct);

    public async Task<bool> ApproveAsync(string title, string command, string workingDirectory, CancellationToken ct)
    {
        if (_auto)
            return true;

        var isCommand = title is "Bash" or "cmd";
        if (isCommand && ProjectRoot is { } root && Permissions.Match(root, command) is { } rule)
        {
            Ui.Info("  " + Ui.Muted($"√ разрешено правилом «{rule}» (/permissions)"));
            return true;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_auto)
                return true;

            if (!Ui.Ansi)
                return await AskPlainAsync(command, workingDirectory, ct);

            var remember = isCommand && ProjectRoot is not null ? PermissionStore.SuggestRule(command) : null;
            var choice = await Ui.ConfirmAsync(title, command, workingDirectory, ct, rememberRule: remember);
            if (choice == 1)
                _auto = true;
            if (choice == 3 && remember is not null)
            {
                Permissions.Add(ProjectRoot!, remember);
                Ui.Info("  " + Ui.Muted($"Запомнил: «{remember}» в этом проекте выполняется без вопроса. Список — /permissions"));
            }
            var allowed = choice is 0 or 1 or 3;
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

    public async Task<bool> ApproveSensitiveAsync(string title, string details, string where, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!Ui.Ansi)
                return await AskPlainAsync(details, where, ct, allowAuto: false);
            var choice = await Ui.ConfirmAsync(title, details, where, ct, allowAuto: false);
            var allowed = choice == 0;
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

    public async Task<string?> AskUserAsync(string question, string[] options, CancellationToken ct)
    {
        // Вопрос задаётся и в авто-режиме: это не разрешение, а решение, которое агент сам не примет.
        await _gate.WaitAsync(ct);
        try
        {
            if (!Ui.Ansi)
            {
                Console.WriteLine();
                Console.WriteLine("Агент спрашивает: " + question);
                for (var i = 0; i < options.Length; i++)
                    Console.WriteLine($"  {i + 1}. {options[i]}");
                Console.Write("Ответ (номер или текст): ");
                var line = (await Task.Run(Console.ReadLine, ct))?.Trim();
                if (string.IsNullOrEmpty(line))
                    return null;
                return int.TryParse(line, out var n) && n >= 1 && n <= options.Length ? options[n - 1] : line;
            }
            var answer = await Ui.AskUserAsync(question, options, ct);
            Ui.Busy("Продолжаю");
            return answer;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> AskPlainAsync(string command, string workingDirectory, CancellationToken ct, bool allowAuto = true)
    {
        Console.WriteLine();
        Console.WriteLine($"Агент хочет выполнить команду в {workingDirectory}:");
        Console.WriteLine("  " + command);
        Console.Write("Разрешить? [y]да / [n]нет / [a]да, и дальше без вопросов: ");

        var answer = await Task.Run(Console.ReadLine, ct);
        switch (answer?.Trim().ToLowerInvariant())
        {
            case "a" when allowAuto:
                _auto = true;
                return true;
            case "y" or "д" or "да" or "yes":
                return true;
            default:
                return false;
        }
    }
}
