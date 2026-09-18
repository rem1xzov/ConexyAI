using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace ConexyAI.Service;

// RAG: добавлено 2026-09-17
/// <summary>Extracts plain text from uploaded documents (TXT, MD, DOCX, PDF).</summary>
public static class DocumentParser
{
    public static string Parse(byte[] bytes, string fileName)
    {
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant() ?? string.Empty;

        return ext switch
        {
            ".txt" or ".md" or ".markdown" or ".csv" or ".json" or ".xml" or ".cs" or ".ts" or ".tsx" or ".js" or ".py" or ".html" or ".css" or ".sql" or ".yaml" or ".yml" =>
                Encoding.UTF8.GetString(bytes),

            ".docx" => ParseDocx(bytes),
            ".pdf" => ParsePdf(bytes),

            _ => Encoding.UTF8.GetString(bytes) // best-effort: treat unknown as text
        };
    }

    private static string ParseDocx(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.GetEntry("word/document.xml");
            if (entry is null)
                return string.Empty;

            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            var runs = doc.Descendants()
                .Where(e => e.Name.LocalName == "t")
                .Select(e => e.Value);

            return string.Join(" ", runs).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ParsePdf(byte[] bytes)
    {
        // PdfPig (MIT) extracts text in reading order, honoring ToUnicode CMaps, so
        // Cyrillic and multi-column/multi-page PDFs are handled far better than the
        // previous regex-based parser.
        try
        {
            using var document = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                sb.AppendLine(page.Text);
            }

            return sb.ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }
}
