using System.Runtime.CompilerServices;
using ConexyAI.Contract;
using ConexyAI.Service;

// ATTACHMENT_MEDIA: регрессия на баг «модель видит только одно вложение из нескольких».
// Причины были разные и все — в том, что тип вложения решал объявленный MIME, а не содержимое:
// фото с Android приходило как application/octet-stream и вообще не считалось картинкой; фото с
// неверным MIME и GIF вырезались image-гардом; картинка больше 8 МБ вырезалась по лимиту, хотя
// провайдер принимает до 32 МиБ. Здесь зафиксировано поведение контракта провайдера: формат по
// сигнатуре, каждое поддерживаемое изображение доезжает до модели, неподдерживаемое — называется.
internal static class AttachmentPipelineTests
{
    [ModuleInitializer]
    internal static void Register()
    {
        TestRegistry.Add("attachments: every supported image reaches the model whatever MIME the client sent", AllImagesReachTheModelAsync);
        TestRegistry.Add("attachments: the image format comes from the bytes, not from the declared MIME", FormatFromContentAsync);
        TestRegistry.Add("attachments: an unsupported image is named in the message instead of vanishing", UnsupportedImageIsNamedAsync);
        TestRegistry.Add("attachments: documents are still delivered as text and are never mistaken for images", DocumentsStillWorkAsync);
        TestRegistry.Add("attachments: a large valid photo is not dropped by a size heuristic", LargePhotoSurvivesAsync);
    }

    private static void Assert(bool condition, string message) => TestRegistry.Assert(condition, message);

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];
    private static readonly byte[] Gif = [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 2, 3, 4];
    private static readonly byte[] Webp =
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P', 1, 2, 3, 4];
    private static readonly byte[] Bmp = [(byte)'B', (byte)'M', 1, 2, 3, 4, 5, 6];

    private static TaskAttachment Attach(string name, string contentType, byte[] bytes) =>
        new(name, Convert.ToBase64String(bytes), contentType);

    private static List<ContentBlock> ImageBlocks(ChatMessage message) =>
        message.Content is IEnumerable<ContentBlock> blocks
            ? blocks.Where(b => b.Type == "image_url").ToList()
            : [];

    private static Task AllImagesReachTheModelAsync()
    {
        var attachments = new List<TaskAttachment>
        {
            // A normal photo.
            Attach("selfie.jpg", "image/jpeg", Jpeg),
            // Android file pickers often omit the type — the photo used to disappear entirely.
            Attach("beach.jpg", "application/octet-stream", Jpeg),
            // A photo saved with the wrong extension: the provider judges by content, we must too.
            Attach("wrong.png", "image/png", Jpeg),
            // GIF is a supported vision format and used to be stripped by the guard.
            Attach("meme.gif", "image/gif", Gif),
            Attach("shot.webp", "image/webp", Webp),
        };

        var user = ChatMessageFactory.User("смотри фото", attachments);
        var blocks = ImageBlocks(user);
        Assert(blocks.Count == 5, $"all five images must be packed as content blocks, got {blocks.Count}");

        var urls = blocks.Select(b => b.ImageUrl?.Url ?? string.Empty).ToList();
        Assert(urls[0].StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal), "a JPEG keeps its content type");
        Assert(urls[1].StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal), "a JPEG declared as octet-stream is recognised by content");
        Assert(urls[2].StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal), "a JPEG declared as PNG is still sent as JPEG");
        Assert(urls[3].StartsWith("data:image/gif;base64,", StringComparison.Ordinal), "a GIF reaches the model as a GIF");
        Assert(urls[4].StartsWith("data:image/webp;base64,", StringComparison.Ordinal), "a WebP keeps its content type");

        var (safe, removed) = ImagePayloadGuard.Sanitize(new List<ChatMessage> { user });
        Assert(removed == 0, $"the payload guard must not strip supported images, removed {removed}");
        Assert(ImageBlocks(safe[0]).Count == 5, "all five images survive the payload guard");

        var text = AttachmentText.ComposeUserMessage("смотри фото", attachments);
        Assert(!text.Contains("не отправлено модели", StringComparison.Ordinal), "images must not be reported as undeliverable");
        return Task.CompletedTask;
    }

    private static Task FormatFromContentAsync()
    {
        var jpegAsPng = $"data:image/png;base64,{Convert.ToBase64String(Jpeg)}";
        var normalized = ImagePayloadGuard.NormalizeDataUrl(jpegAsPng);
        Assert(normalized is not null && normalized.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal),
            $"the guard must rewrite the data URL to the detected format, got: {normalized}");

        Assert(ImagePayloadGuard.IsValidImage(Jpeg, "image/png"), "a JPEG is valid whatever the declared MIME says");
        Assert(ImagePayloadGuard.IsValidImage(Gif, "image/gif"), "GIF is a supported vision format");
        Assert(!ImagePayloadGuard.IsValidImage(Bmp, "image/png"), "BMP is not a supported vision format");
        Assert(AttachmentMedia.IsImage(Attach("photo", "application/octet-stream", Jpeg)),
            "a photo with no declared MIME is still an image by signature");
        return Task.CompletedTask;
    }

    private static Task UnsupportedImageIsNamedAsync()
    {
        var bmp = Attach("diagram.bmp", "image/bmp", Bmp);
        var text = AttachmentText.ComposeUserMessage("что на схеме?", new List<TaskAttachment> { bmp });

        Assert(text.Contains("diagram.bmp", StringComparison.Ordinal), "the unsupported image must be named");
        Assert(text.Contains("не отправлено модели", StringComparison.Ordinal), "the user/model must be told the image was not delivered");
        Assert(text.Contains("JPEG, PNG, GIF, WebP", StringComparison.Ordinal), "the message must name the supported formats");

        var user = ChatMessageFactory.User(text, new List<TaskAttachment> { bmp });
        Assert(ImageBlocks(user).Count == 0, "an unsupported format must not be sent as an image block");
        return Task.CompletedTask;
    }

    private static Task DocumentsStillWorkAsync()
    {
        var notes = Attach("notes.txt", "text/plain", System.Text.Encoding.UTF8.GetBytes("Список покупок: хлеб и молоко."));
        // A text file that came without a usable MIME must stay a document, not become an image.
        var notesAsOctet = Attach("plan.txt", "application/octet-stream", System.Text.Encoding.UTF8.GetBytes("План: тесты и релиз."));

        var text = AttachmentText.ComposeUserMessage("прочитай", new List<TaskAttachment> { notes, notesAsOctet });
        Assert(text.Contains("хлеб и молоко", StringComparison.Ordinal), "text attachment content reaches the message");
        Assert(text.Contains("тесты и релиз", StringComparison.Ordinal), "a text file without a MIME is still read as text");
        Assert(!text.Contains("не отправлено модели", StringComparison.Ordinal), "documents must not be reported as undeliverable");

        var user = ChatMessageFactory.User(text, new List<TaskAttachment> { notes, notesAsOctet });
        Assert(ImageBlocks(user).Count == 0, "documents must not be sent as image blocks");
        return Task.CompletedTask;
    }

    private static Task LargePhotoSurvivesAsync()
    {
        // The provider accepts up to 32 MiB per inline image; the old guard dropped anything over 8 MiB.
        var big = new byte[9 * 1024 * 1024];
        Array.Copy(Jpeg, big, Jpeg.Length);
        var attachment = Attach("photo.jpg", "image/jpeg", big);

        var user = ChatMessageFactory.User("фото", new List<TaskAttachment> { attachment });
        var (safe, removed) = ImagePayloadGuard.Sanitize(new List<ChatMessage> { user });

        Assert(removed == 0, $"a 9 MB photo is within the provider limit and must not be dropped, removed {removed}");
        Assert(ImageBlocks(safe[0]).Count == 1, "the large photo stays in the model payload");
        Assert(ImagePayloadGuard.MaxInlineImageBytes >= 32L * 1024 * 1024,
            "the per-image limit must match the provider contract");
        return Task.CompletedTask;
    }
}
