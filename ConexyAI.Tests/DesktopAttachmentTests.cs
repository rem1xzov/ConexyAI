using System.Runtime.CompilerServices;
using ConexyAI.Desktop;

// DESKTOP_ATTACHMENTS: регрессия на краш клиента при перетаскивании файла, который занят другой
// программой (пользователь перетащил PDF из сетевой папки, открытый в просмотрщике, и консоль
// упала с «The process cannot access the file … because it is being used by another process»).
// AddFile обязан вернуть понятную ошибку, а не бросить IOException в интерактивный цикл.
internal static class DesktopAttachmentTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("desktop attachments: a file locked by another program is refused, not crashed",
            LockedFileIsRefusedAsync);
        TestRegistry.Add("desktop attachments: a missing path is a message, not an exception",
            MissingFileIsRefusedAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static async Task LockedFileIsRefusedAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "conexy-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Практическая работа.pdf");
        await File.WriteAllBytesAsync(file, new byte[] { 0x25, 0x50, 0x44, 0x46 });

        var before = Attachments.Pending.Count;
        try
        {
            // Держим файл монопольно, как это делает Word/PDF-вьюер.
            await using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var error = Attachments.AddFile(file);
                Assert(error is { Length: > 0 }, "a locked file must produce an error message");
                Assert(error!.Contains("занят", StringComparison.OrdinalIgnoreCase)
                       || error.Contains("Не удалось прочитать", StringComparison.OrdinalIgnoreCase),
                    $"the error must explain the lock, got: {error}");
                Assert(Attachments.Pending.Count == before, "the locked file must not be attached");
            }

            // После закрытия просмотрщика тот же файл прикрепляется нормально.
            var retry = Attachments.AddFile(file);
            Assert(retry is null, $"after the lock is released the file attaches, got: {retry}");
            Assert(Attachments.Pending.Count == before + 1, "the file is attached after the lock is gone");
        }
        finally
        {
            Attachments.Clear();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static Task MissingFileIsRefusedAsync()
    {
        var before = Attachments.Pending.Count;
        var error = Attachments.AddFile(Path.Combine(Path.GetTempPath(), "conexy-missing-" + Guid.NewGuid().ToString("N") + ".pdf"));
        Assert(error is { Length: > 0 } && error!.Contains("не найден", StringComparison.OrdinalIgnoreCase),
            $"a missing file must be a friendly message, got: {error}");
        Assert(Attachments.Pending.Count == before, "nothing is attached for a missing file");
        return Task.CompletedTask;
    }
}
