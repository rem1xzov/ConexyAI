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

    // BASH_DESCRIPTION: описание показывается отдельной строкой — пользователь читает намерение,
    // а команда ниже остаётся точной и копируемой.
    public Task<bool> ApproveCommandAsync(string command, string? description, string workingDirectory, CancellationToken ct) =>
        ApproveAsync("Bash", command, workingDirectory, ct, description);

    public Task<bool> ApproveAsync(string title, string command, string workingDirectory, CancellationToken ct) =>
        ApproveAsync(title, command, workingDirectory, ct, null);

    private async Task<bool> ApproveAsync(string title, string command, string workingDirectory, CancellationToken ct, string? description)
    {
        var note = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        var isCommand = title is "Bash" or "cmd";
        // DESTRUCTIVE: удаление или откат файлов проекта — единственный класс, который спрашивает и в авто-режиме,
        // и не покрывается запомненными правилами. Здесь ошибка не исправляется повторным запуском: файлов
        // уже нет. Всё остальное работает как раньше — авто-режим не трогаем.
        var destructive = isCommand ? CommandPolicy.DestructiveReason(command) : null;
        if (destructive is not null)
            note = (note is null ? string.Empty : note + " · ") + "внимание: " + destructive;

        if (_auto && destructive is null)
            return true;

        if (isCommand && destructive is null && ProjectRoot is { } root && Permissions.Match(root, command) is { } rule)
        {
            Ui.Info("  " + Ui.Muted($"√ разрешено правилом «{rule}» (/permissions)"));
            return true;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_auto && destructive is null)
                return true;

            // APPROVAL_BADGE: пока человек решает, на круге горит значок «нужно подтверждение».
            using var badge = ApprovalBadge.Enter();

            if (!Ui.Ansi)
                return await AskPlainAsync(command, workingDirectory, ct, note, allowAuto: destructive is null);

            var remember = isCommand && destructive is null && ProjectRoot is not null
                ? PermissionStore.SuggestRule(command)
                : null;
            var choice = await Ui.ConfirmAsync(title, command, workingDirectory, ct, rememberRule: remember, description: note);
            // Авто-режим включается только обычной командой: разрушительная должна оставаться под вопросом.
            if (choice == 1 && destructive is null)
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
            using var badge = ApprovalBadge.Enter();
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

    private async Task<bool> AskPlainAsync(string command, string workingDirectory, CancellationToken ct, string? description = null, bool allowAuto = true)
    {
        Console.WriteLine();
        Console.WriteLine($"Агент хочет выполнить команду в {workingDirectory}:");
        if (description is not null)
            Console.WriteLine("  " + description);
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
