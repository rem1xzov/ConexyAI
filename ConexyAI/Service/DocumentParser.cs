using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UglyToad.PdfPig;

namespace ConexyAI.Service;

// RAG: добавлено 2026-09-17
/// <summary>Extracts plain text from uploaded documents (TXT, MD, DOCX, XLSX, PPTX, PDF).</summary>
/// <remarks>
/// DOC_PARSER_LIMITS: переписано 2026-09-24 (ревью H4) — .xlsx в 9 КБ (1000 строк с ячейкой в колонке
/// XFD) раздувался до 49 млн символов: каждая строка добивалась пустыми ячейками до максимальной колонки,
/// XML грузился целиком в DOM, лимитов не было. Теперь: XML читается потоково (XmlReader), колонка —
/// только ASCII A..XFD и не дальше <see cref="MaxColumns"/>, хвостовые пустые ячейки не выводятся,
/// общий бюджет <see cref="MaxOutputChars"/> символов, кап строк, проверки zip-бомбы (число записей,
/// размер центрального каталога, степень сжатия, реальный объём распакованных байт на запись и всего).
/// TEXT_DECODING: добавлено 2026-09-24 (ревью M6) — BOM (UTF-8/16/32), UTF-16 без BOM, Windows-1251;
/// из любого извлечённого текста убираются NUL и прочие недопустимые символы (Postgres не принимает \0).
/// </remarks>
public static partial class DocumentParser
{
    /// <summary>Most characters one document yields; the rest is dropped with a note.</summary>
    public const int MaxOutputChars = 200_000;

    /// <summary>Spreadsheet columns beyond this (≈ "IV") are ignored.</summary>
    public const int MaxColumns = 256;

    public const int MaxRowsPerSheet = 10_000;
    private const int MaxRowsScannedPerSheet = 1_000_000;
    private const int MaxSheets = 256;
    private const int MaxSlides = 2_000;
    private const int MaxPdfPages = 2_000;
    private const int MaxCellChars = 32_767;

    public const int MaxZipEntries = 10_000;
    private const long MaxCentralDirectoryBytes = 16L * 1024 * 1024;
    public const long MaxEntryBytes = 50L * 1024 * 1024;
    private const long MaxTotalBytes = 150L * 1024 * 1024;
    public const int MaxCompressionRatio = 100;
    private const long RatioCheckMinBytes = 1024 * 1024;
    private const int MaxSharedStrings = 1_000_000;
    private const long MaxSharedStringChars = 5_000_000;

    // OFFICE_FORMATS: добавлено 2026-09-23 — formats that are text already.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log",
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".html", ".css", ".sql",
    };

    static DocumentParser()
    {
        // Windows-1251 for Russian text files saved by Excel/Notepad on Windows.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// OFFICE_FORMATS: true when <see cref="Parse"/> actually understands the file, as opposed to
    /// its best-effort "read the bytes as text" fallback — which for a zip, an image or a legacy
    /// binary .doc produces garbage that must not be shown to a model.
    /// </summary>
    public static bool CanExtract(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return TextExtensions.Contains(ext) || ext.ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx" or ".pdf";
    }

    /// <summary>
    /// The document's text, at most <see cref="MaxOutputChars"/> characters (plus a truncation note),
    /// free of characters PostgreSQL or XML cannot store. Never throws for malformed input: whatever
    /// was read before the problem is returned.
    /// </summary>
    public static string Parse(byte[] bytes, string fileName)
    {
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant() ?? string.Empty;
        var output = new TextBudget(MaxOutputChars);
        try
        {
            switch (ext)
            {
                case ".docx":
                    WithZip(bytes, output, ReadDocx);
                    break;
                case ".xlsx":
                    WithZip(bytes, output, ReadXlsx);
                    break;
                case ".pptx":
                    WithZip(bytes, output, ReadPptx);
                    break;
                case ".pdf":
                    ReadPdf(bytes, output);
                    break;
                default:
                    // Text formats, and the best-effort fallback for anything unknown.
                    ReadText(bytes, output);
                    break;
            }
        }
        catch (LimitExceededException)
        {
            output.MarkTruncated();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Malformed file: keep what was read.
        }
        return output.ToString();
    }

    // ------------------------------------------------------------------ text files (M6)

    private static void ReadText(byte[] bytes, TextBudget output)
    {
        // Decode no more than the budget can use: 4 bytes per character at most.
        var limit = (int)Math.Min(bytes.Length, (long)MaxOutputChars * 4 + 16);
        var text = DecodeText(bytes.AsSpan(0, limit));
        output.Append(text.Replace("\r\n", "\n"));
        if (limit < bytes.Length) output.MarkTruncated();
    }

    /// <summary>
    /// Decodes a text file: BOM (UTF-8, UTF-16 LE/BE, UTF-32 LE/BE) first, then BOM-less UTF-32/UTF-16
    /// (NUL bytes at fixed positions), strict UTF-8, Windows-1251, and lenient UTF-8 as the last resort.
    /// </summary>
    public static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return Utf8Prefix(bytes[3..]);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })) return Wide(new UTF32Encoding(false, false), bytes[4..], 4);
        if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF })) return Wide(new UTF32Encoding(true, false), bytes[4..], 4);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return Wide(Encoding.Unicode, bytes[2..], 2);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return Wide(Encoding.BigEndianUnicode, bytes[2..], 2);

        var sample = bytes[..Math.Min(bytes.Length, 64 * 1024)];
        var (wide, unit) = GuessWideEncoding(sample);
        if (wide is not null) return Wide(wide, bytes, unit);

        var utf8End = Utf8Boundary(bytes);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes[..utf8End]);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8.
        }

        var cyrillic = Encoding.GetEncoding(1251).GetString(bytes);
        var nonAscii = 0;
        var letters = 0;
        foreach (var ch in cyrillic)
        {
            if (ch < 128) continue;
            nonAscii++;
            if (ch is >= 'Ѐ' and <= 'ӿ') letters++;
        }
        if (nonAscii > 0 && letters >= nonAscii * 0.7) return cyrillic;

        return Encoding.UTF8.GetString(bytes[..utf8End]);
    }

    private static string Utf8Prefix(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes[..Utf8Boundary(bytes)]);

    /// <summary>Length of the longest prefix that does not end inside a multi-byte UTF-8 sequence.</summary>
    private static int Utf8Boundary(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.Length;
        var back = 0;
        while (end - back > 0 && back < 4 && (bytes[end - back - 1] & 0xC0) == 0x80) back++;
        if (end - back == 0) return end;
        var lead = bytes[end - back - 1];
        var needed = lead >= 0xF0 ? 3 : lead >= 0xE0 ? 2 : lead >= 0xC0 ? 1 : 0;
        return needed > back ? end - back - 1 : end;
    }

    private static string Wide(Encoding encoding, ReadOnlySpan<byte> bytes, int unit)
    {
        var text = encoding.GetString(bytes[..(bytes.Length - bytes.Length % unit)]);
        // A prefix cut inside a surrogate pair decodes to U+FFFD at the very end.
        return text.TrimEnd('�');
    }

    /// <summary>
    /// Text files never contain NUL; NULs at every other (or every 2nd–4th) byte mean UTF-16 (UTF-32)
    /// without a BOM. The candidate must also decode to plausible text.
    /// </summary>
    private static (Encoding? Encoding, int Unit) GuessWideEncoding(ReadOnlySpan<byte> sample)
    {
        if (sample.Length < 2) return (null, 0);
        var nulAt = new int[4];
        var total = 0;
        for (var i = 0; i < sample.Length; i++)
        {
            if (sample[i] != 0) continue;
            nulAt[i % 4]++;
            total++;
        }
        if (total == 0) return (null, 0);

        var quads = sample.Length / 4;
        if (quads > 0)
        {
            if (nulAt[2] >= quads * 0.95 && nulAt[3] >= quads * 0.95 && Plausible(new UTF32Encoding(false, false), sample, 4))
                return (new UTF32Encoding(false, false), 4);
            if (nulAt[0] >= quads * 0.95 && nulAt[1] >= quads * 0.95 && Plausible(new UTF32Encoding(true, false), sample, 4))
                return (new UTF32Encoding(true, false), 4);
        }

        var odd = nulAt[1] + nulAt[3];
        var even = nulAt[0] + nulAt[2];
        if (odd > 0 && odd >= even * 4 && FewHighBytes(sample, 1) && Plausible(Encoding.Unicode, sample, 2)) return (Encoding.Unicode, 2);
        if (even > 0 && even >= odd * 4 && FewHighBytes(sample, 0) && Plausible(Encoding.BigEndianUnicode, sample, 2)) return (Encoding.BigEndianUnicode, 2);
        return (null, 0);
    }

    /// <summary>
    /// In UTF-16 text of one script the high bytes take one or two values (0x00 for ASCII, 0x04 for
    /// Cyrillic); UTF-8 or binary data read as UTF-16 has them all over the place.
    /// </summary>
    private static bool FewHighBytes(ReadOnlySpan<byte> sample, int offset)
    {
        var counts = new int[256];
        var pairs = 0;
        for (var i = offset; i < sample.Length; i += 2)
        {
            counts[sample[i]]++;
            pairs++;
        }
        var sorted = counts.OrderByDescending(c => c).ToArray();
        return pairs > 0 && sorted[0] + sorted[1] >= pairs * 0.8;
    }

    private static bool Plausible(Encoding encoding, ReadOnlySpan<byte> sample, int unit)
    {
        var text = encoding.GetString(sample[..(sample.Length - sample.Length % unit)]);
        if (text.Length == 0) return false;
        var good = 0;
        foreach (var ch in text)
        {
            if (ch is '\t' or '\n' or '\r' || (!char.IsControl(ch) && ch != '�' && !char.IsSurrogate(ch)
                    && CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.OtherNotAssigned))
            {
                good++;
            }
        }
        return good >= text.Length * 0.9;
    }

    // ------------------------------------------------------------------ zip safety (H4)

    private static void WithZip(byte[] bytes, TextBudget output, Action<SafeZip, TextBudget> read)
    {
        using var zip = SafeZip.Open(bytes);
        if (zip is null) return;
        read(zip, output);
    }

    private sealed class LimitExceededException : Exception
    {
        public LimitExceededException(string message) : base(message) { }
    }

    private sealed record Relationship(string Target, string Type);

    /// <summary>
    /// A zip archive read under limits: the central directory is checked before .NET parses it, every
    /// entry is checked for a bomb-like compression ratio, and the bytes actually inflated are counted
    /// per entry and in total (the sizes in the directory can lie).
    /// </summary>
    private sealed class SafeZip : IDisposable
    {
        private static readonly XmlReaderSettings Settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true,
        };

        private readonly ZipArchive _zip;
        private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
        private long _remaining = MaxTotalBytes;

        private SafeZip(ZipArchive zip)
        {
            _zip = zip;
            foreach (var entry in zip.Entries) _entries.TryAdd(entry.FullName.TrimStart('/'), entry);
        }

        public static SafeZip? Open(byte[] bytes)
        {
            if (!CentralDirectoryIsSane(bytes)) return null;
            var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxZipEntries)
            {
                zip.Dispose();
                return null;
            }
            return new SafeZip(zip);
        }

        public XmlReader? OpenXml(string? path)
        {
            if (path is null || !_entries.TryGetValue(path.TrimStart('/'), out var entry)) return null;
            // A zip bomb: a megabyte-plus entry inflating more than 100:1.
            if (entry.Length > RatioCheckMinBytes && entry.Length > MaxCompressionRatio * Math.Max(1, entry.CompressedLength)) return null;
            if (_remaining <= 0) throw new LimitExceededException("total uncompressed size");

            var stream = new CountingStream(entry.Open(), Math.Min(MaxEntryBytes, _remaining), consumed => _remaining -= consumed);
            return XmlReader.Create(stream, Settings);
        }

        /// <summary>The main part (word/document.xml, xl/workbook.xml, ppt/presentation.xml) per _rels/.rels.</summary>
        public string MainPart(string fallback)
        {
            var main = Relationships(string.Empty).Values.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal));
            return main is not null && _entries.ContainsKey(main.Target) ? main.Target : fallback;
        }

        /// <summary>Relationships of a part, with targets resolved to entry names.</summary>
        public Dictionary<string, Relationship> Relationships(string part)
        {
            var directory = part.Contains('/') ? part[..part.LastIndexOf('/')] : string.Empty;
            var relsPath = (directory.Length > 0 ? directory + "/" : string.Empty) + "_rels/" + part[(part.LastIndexOf('/') + 1)..] + ".rels";
            var result = new Dictionary<string, Relationship>(StringComparer.Ordinal);
            using var reader = OpenXml(relsPath);
            if (reader is null) return result;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Relationship") continue;
                if (string.Equals(Attr(reader, "TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
                var id = Attr(reader, "Id");
                var target = Attr(reader, "Target");
                if (id is null || target is null || result.Count >= MaxZipEntries) continue;
                result.TryAdd(id, new Relationship(ResolveTarget(directory, target), Attr(reader, "Type") ?? string.Empty));
            }
            return result;
        }

        public string? RelatedPart(Dictionary<string, Relationship> relationships, string typeSuffix, string fallback) =>
            relationships.Values.FirstOrDefault(r => r.Type.EndsWith(typeSuffix, StringComparison.Ordinal))?.Target ?? fallback;

        public void Dispose() => _zip.Dispose();
    }

    private static string ResolveTarget(string directory, string target)
    {
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(target);
        }
        catch (UriFormatException)
        {
            decoded = target;
        }
        var segments = new List<string>();
        if (!decoded.StartsWith('/') && directory.Length > 0) segments.AddRange(directory.Split('/'));
        foreach (var segment in decoded.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return string.Join('/', segments);
    }

    /// <summary>
    /// Reads the End Of Central Directory record (and its ZIP64 variant) so that an archive declaring
    /// millions of entries is refused before <see cref="ZipArchive"/> allocates them.
    /// </summary>
    internal static bool CentralDirectoryIsSane(byte[] bytes)
    {
        const int eocdSize = 22;
        if (bytes.Length < eocdSize) return false;
        var lowest = Math.Max(0, bytes.Length - eocdSize - ushort.MaxValue);
        for (var i = bytes.Length - eocdSize; i >= lowest; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) != 0x06054B50) continue;

            long entries = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i + 10));
            long directorySize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i + 12));
            if (entries == ushort.MaxValue || directorySize == uint.MaxValue)
            {
                var locator = i - 20;
                if (locator < 0 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(locator)) != 0x07064B50) return false;
                var offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(locator + 8));
                if (offset > (ulong)(bytes.Length - 56)) return false;
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset)) != 0x06064B50) return false;
                var entries64 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)offset + 32));
                var size64 = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)offset + 40));
                if (entries64 > MaxZipEntries || size64 > (ulong)MaxCentralDirectoryBytes) return false;
                return true;
            }
            return entries <= MaxZipEntries && directorySize <= MaxCentralDirectoryBytes;
        }
        return false;
    }

    /// <summary>Counts inflated bytes and stops at the limit, whatever the directory claimed.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private readonly Action<long> _onRead;
        private long _read;

        public CountingStream(Stream inner, long limit, Action<long> onRead)
        {
            _inner = inner;
            _limit = limit;
            _onRead = onRead;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = _inner.Read(buffer);
            _read += n;
            _onRead(n);
            if (_read > _limit) throw new LimitExceededException("entry uncompressed size");
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ XML helpers

    /// <summary>An attribute by local name, whatever its namespace (transitional or strict OOXML).</summary>
    private static string? Attr(XmlReader reader, string localName)
    {
        if (!reader.HasAttributes) return null;
        string? value = null;
        for (var i = 0; i < reader.AttributeCount; i++)
        {
            reader.MoveToAttribute(i);
            if (reader.LocalName == localName)
            {
                value = reader.Value;
                break;
            }
        }
        reader.MoveToElement();
        return value;
    }

    /// <summary>The r:id of an element (sheet, sldId): the "id" attribute in a namespace.</summary>
    private static string? RelationshipId(XmlReader reader)
    {
        string? value = null;
        for (var i = 0; i < reader.AttributeCount; i++)
        {
            reader.MoveToAttribute(i);
            if (reader.LocalName == "id" && reader.NamespaceURI.Length > 0)
            {
                value = reader.Value;
                break;
            }
        }
        reader.MoveToElement();
        return value;
    }

    private static int? IntAttr(XmlReader reader, string localName) =>
        int.TryParse(Attr(reader, localName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// Appends the text content of the current element (positioned on its start tag) to
    /// <paramref name="target"/>, at most up to <paramref name="maxLength"/>; leaves the reader on
    /// the end tag. Large text nodes are read in chunks, never materialized whole.
    /// </summary>
    private static void ReadElementText(XmlReader reader, StringBuilder target, int maxLength)
    {
        if (reader.IsEmptyElement) return;
        var depth = reader.Depth;
        var buffer = new char[4096];
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) return;
            if (reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace))
            {
                continue;
            }

            if (reader.CanReadValueChunk)
            {
                int n;
                while ((n = reader.ReadValueChunk(buffer, 0, buffer.Length)) > 0)
                {
                    var room = maxLength - target.Length;
                    if (room > 0) target.Append(buffer, 0, Math.Min(n, room));
                }
            }
            else if (target.Length < maxLength)
            {
                var value = reader.Value;
                target.Append(value, 0, Math.Min(value.Length, maxLength - target.Length));
            }
        }
    }

    private static string ReadElementText(XmlReader reader, int maxLength)
    {
        var sb = new StringBuilder();
        ReadElementText(reader, sb, maxLength);
        return sb.ToString();
    }

    private static bool IsSkippedSubtree(string localName) => localName is
        "Fallback" or "del" or "delText" or "moveFrom" or "instrText" or "pPrChange" or "rPrChange" or
        "sectPrChange" or "tblPrChange" or "trPrChange" or "tcPrChange" or "numberingChange" or "rPh";

    private static string TableRow(IEnumerable<string> cells) =>
        "| " + string.Join(" | ", cells.Select(c => c.Replace("\r", string.Empty).Replace('\n', ' ').Replace("|", "\\|"))) + " |";

    // ------------------------------------------------------------------ docx

    // OFFICE_FORMATS: переписано 2026-09-23 — абзацы, заголовки и ячейки таблиц раздельно, чтобы модель
    // видела структуру документа. DOC_PARSER_LIMITS: 2026-09-24 — потоково; заголовки как «#», пункты
    // списков как «- »/«1. ».
    private static void ReadDocx(SafeZip zip, TextBudget output)
    {
        var main = zip.MainPart("word/document.xml");
        var relationships = zip.Relationships(main);
        var headings = ReadDocxHeadingStyles(zip, zip.RelatedPart(relationships, "/styles", "word/styles.xml"));
        var numbering = ReadDocxNumbering(zip, zip.RelatedPart(relationships, "/numbering", "word/numbering.xml"));

        using var reader = zip.OpenXml(main);
        if (reader is null) return;
        new DocxBodyReader(reader, output, headings, numbering).Run();
    }

    private sealed class DocxNumbering
    {
        public Dictionary<int, int> AbstractOf { get; } = new();
        public Dictionary<(int Abstract, int Level), (string Format, int Start)> Levels { get; } = new();
        public Dictionary<(int Num, int Level), int> StartOverrides { get; } = new();
    }

    private sealed class DocxBodyReader
    {
        private readonly XmlReader _reader;
        private readonly TextBudget _output;
        private readonly Dictionary<string, int> _headings;
        private readonly DocxNumbering _numbering;
        private readonly Dictionary<(int Num, int Level), int> _counters = new();
        private readonly StringBuilder _paragraph = new();
        private readonly Stack<StringBuilder> _cells = new();
        private readonly Stack<List<string>> _rows = new();
        private int _paragraphDepth;
        private int _runDepth;
        private int _tableDepth;
        private string? _style;
        private int? _numId;
        private int _level;
        private int? _outline;

        public DocxBodyReader(XmlReader reader, TextBudget output, Dictionary<string, int> headings, DocxNumbering numbering)
        {
            _reader = reader;
            _output = output;
            _headings = headings;
            _numbering = numbering;
        }

        public void Run()
        {
            var r = _reader;
            r.MoveToContent();
            while (!r.EOF && !_output.Exhausted)
            {
                if (r.NodeType == XmlNodeType.Element)
                {
                    var name = r.LocalName;
                    if (IsSkippedSubtree(name))
                    {
                        r.Skip();
                        continue;
                    }

                    var empty = r.IsEmptyElement;
                    switch (name)
                    {
                        case "p" when empty:
                            if (_paragraphDepth == 0 && _cells.Count == 0) _output.BlankLine();
                            break;
                        case "p":
                            if (++_paragraphDepth == 1)
                            {
                                _paragraph.Clear();
                                _style = null;
                                _numId = null;
                                _level = 0;
                                _outline = null;
                            }
                            break;
                        case "pStyle" when _paragraphDepth == 1:
                            _style = Attr(r, "val");
                            break;
                        case "numId" when _paragraphDepth == 1:
                            _numId = IntAttr(r, "val");
                            break;
                        case "ilvl" when _paragraphDepth == 1:
                            _level = Math.Clamp(IntAttr(r, "val") ?? 0, 0, 8);
                            break;
                        case "outlineLvl" when _paragraphDepth == 1:
                            _outline = IntAttr(r, "val");
                            break;
                        case "r" when !empty:
                            _runDepth++;
                            break;
                        case "t" when _runDepth > 0 && _paragraphDepth > 0:
                            ReadElementText(r, _paragraph, MaxOutputChars);
                            break;
                        case "tab" when _runDepth > 0:
                            _paragraph.Append('\t');
                            break;
                        case "br" or "cr" when _runDepth > 0:
                            _paragraph.Append('\n');
                            break;
                        case "noBreakHyphen" when _runDepth > 0:
                            _paragraph.Append('-');
                            break;
                        case "tbl" when !empty:
                            _tableDepth++;
                            break;
                        case "tr" when !empty:
                            _rows.Push(new List<string>());
                            break;
                        case "tc" when empty:
                            if (_rows.Count > 0) _rows.Peek().Add(string.Empty);
                            break;
                        case "tc":
                            _cells.Push(new StringBuilder());
                            break;
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement)
                {
                    switch (r.LocalName)
                    {
                        case "r":
                            _runDepth = Math.Max(0, _runDepth - 1);
                            break;
                        case "p" when _paragraphDepth > 0:
                            if (--_paragraphDepth == 0) EndParagraph();
                            else _paragraph.Append(' ');
                            break;
                        case "tc" when _cells.Count > 0:
                            var cell = _cells.Pop().ToString().Trim();
                            if (_rows.Count > 0) _rows.Peek().Add(cell);
                            break;
                        case "tr" when _rows.Count > 0:
                            var row = _rows.Pop();
                            if (_cells.Count > 0) _cells.Peek().Append(' ').Append(string.Join(" | ", row));
                            else _output.Line(TableRow(row.Take(MaxColumns)));
                            break;
                        case "tbl" when _tableDepth > 0:
                            if (--_tableDepth == 0 && _cells.Count == 0) _output.BlankLine();
                            break;
                    }
                }
                r.Read();
            }
        }

        private void EndParagraph()
        {
            var text = _paragraph.ToString();
            if (_cells.Count > 0)
            {
                var cell = _cells.Peek();
                if (text.Trim().Length == 0) return;
                if (cell.Length > 0) cell.Append(' ');
                cell.Append(text.Replace('\n', ' '));
                return;
            }

            if (text.Trim().Length == 0)
            {
                _output.BlankLine();
                return;
            }
            _output.Line(Prefix() + text);
        }

        private string Prefix()
        {
            var heading = _outline is >= 0 and < 9 ? _outline + 1 : _style is not null && _headings.TryGetValue(_style, out var level) ? level : 0;
            if (heading > 0) return new string('#', Math.Min(heading.Value, 6)) + " ";
            if (_numId is not { } numId || numId <= 0 || !_numbering.AbstractOf.TryGetValue(numId, out var abstractId)) return string.Empty;

            var (format, start) = _numbering.Levels.TryGetValue((abstractId, _level), out var definition) ? definition : ("decimal", 1);
            if (_numbering.StartOverrides.TryGetValue((numId, _level), out var overridden)) start = overridden;
            for (var deeper = _level + 1; deeper < 9; deeper++) _counters.Remove((numId, deeper));

            var indent = new string(' ', 2 * _level);
            if (format == "bullet") return indent + "- ";
            if (format == "none") return indent;
            var number = _counters.TryGetValue((numId, _level), out var previous) ? previous + 1 : start;
            _counters[(numId, _level)] = number;
            return indent + number.ToString(CultureInfo.InvariantCulture) + ". ";
        }
    }

    /// <summary>styleId → heading level (1–6) for "Title", "heading N" and styles with an outline level.</summary>
    private static Dictionary<string, int> ReadDocxHeadingStyles(SafeZip zip, string? path)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = zip.OpenXml(path);
        if (reader is null) return result;

        string? id = null, type = null, name = null;
        int? outline = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "style":
                        id = Attr(reader, "styleId");
                        type = Attr(reader, "type");
                        name = null;
                        outline = null;
                        break;
                    case "name" when id is not null:
                        name = Attr(reader, "val");
                        break;
                    case "outlineLvl" when id is not null:
                        outline = IntAttr(reader, "val");
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "style")
            {
                if (id is not null && type == "paragraph")
                {
                    var level = outline is >= 0 and < 9 ? outline.Value + 1
                        : name is not null && name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[8..], out var n) ? n
                        : string.Equals(name, "Title", StringComparison.OrdinalIgnoreCase) ? 1
                        : 0;
                    if (level > 0) result[id] = Math.Min(level, 6);
                }
                id = null;
            }
        }
        return result;
    }

    private static DocxNumbering ReadDocxNumbering(SafeZip zip, string? path)
    {
        var result = new DocxNumbering();
        using var reader = zip.OpenXml(path);
        if (reader is null) return result;

        int? abstractId = null, numId = null, level = null, overrideLevel = null;
        var format = "decimal";
        var start = 1;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "abstractNum":
                        abstractId = IntAttr(reader, "abstractNumId");
                        numId = null;
                        break;
                    case "num":
                        numId = IntAttr(reader, "numId");
                        abstractId = null;
                        break;
                    case "lvl" when abstractId is not null:
                        level = IntAttr(reader, "ilvl");
                        format = "decimal";
                        start = 1;
                        break;
                    case "numFmt" when level is not null:
                        format = Attr(reader, "val") ?? "decimal";
                        break;
                    case "start" when level is not null:
                        start = IntAttr(reader, "val") ?? 1;
                        break;
                    case "abstractNumId" when numId is not null:
                        if (IntAttr(reader, "val") is { } target) result.AbstractOf[numId.Value] = target;
                        break;
                    case "lvlOverride" when numId is not null:
                        overrideLevel = IntAttr(reader, "ilvl");
                        break;
                    case "startOverride" when numId is not null && overrideLevel is not null:
                        if (IntAttr(reader, "val") is { } value) result.StartOverrides[(numId.Value, overrideLevel.Value)] = value;
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "lvl" when abstractId is not null && level is not null:
                        result.Levels[(abstractId.Value, level.Value)] = (format, start);
                        level = null;
                        break;
                    case "lvlOverride":
                        overrideLevel = null;
                        break;
                    case "abstractNum":
                        abstractId = null;
                        break;
                    case "num":
                        numId = null;
                        break;
                }
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ xlsx

    private enum CellFormat
    {
        Number,
        Date,
        Time,
        DateTime,
        Percent,
    }

    // OFFICE_FORMATS: добавлено 2026-09-23 — every sheet as a Markdown-like table, so a model reads
    // rows and columns rather than a flat list of values.
    private static void ReadXlsx(SafeZip zip, TextBudget output)
    {
        var workbook = zip.MainPart("xl/workbook.xml");
        var relationships = zip.Relationships(workbook);

        var sheets = new List<(string Name, string RelationshipId)>();
        var date1904 = false;
        using (var reader = zip.OpenXml(workbook))
        {
            if (reader is null) return;
            while (reader.Read() && sheets.Count < MaxSheets)
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "workbookPr") date1904 = Attr(reader, "date1904") is "1" or "true";
                if (reader.LocalName == "sheet" && RelationshipId(reader) is { } id) sheets.Add((Attr(reader, "name") ?? string.Empty, id));
            }
        }

        var shared = ReadSharedStrings(zip, zip.RelatedPart(relationships, "/sharedStrings", "xl/sharedStrings.xml"));
        var formats = ReadCellFormats(zip, zip.RelatedPart(relationships, "/styles", "xl/styles.xml"));

        foreach (var (name, id) in sheets)
        {
            if (output.Exhausted) break;
            if (!relationships.TryGetValue(id, out var sheet)) continue;
            output.Line($"## Лист: {name}");
            ReadSheet(zip, sheet.Target, shared, formats, date1904, output);
            output.BlankLine();
        }
    }

    private static void ReadSheet(SafeZip zip, string path, IReadOnlyList<string> shared, IReadOnlyList<CellFormat> formats, bool date1904, TextBudget output)
    {
        using var reader = zip.OpenXml(path);
        if (reader is null) return;

        var cells = new List<(int Column, string Text)>();
        var nextColumn = 0;
        var rowsEmitted = 0;
        var rowsScanned = 0;
        reader.MoveToContent();
        while (!reader.EOF && !output.Exhausted)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                cells.Clear();
                nextColumn = 0;
                if (++rowsScanned > MaxRowsScannedPerSheet) break;
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
            {
                var reference = Attr(reader, "r");
                var column = reference is null ? nextColumn : ColumnIndex(reference);
                if (column < 0 || column >= MaxColumns)
                {
                    // Not A1..XFD, or beyond the column cap: the cell is skipped, never padded up to.
                    if (column >= 0) nextColumn = column + 1;
                    reader.Skip();
                    continue;
                }
                nextColumn = column + 1;
                var text = ReadCell(reader, shared, formats, date1904);
                if (text.Trim().Length > 0) cells.Add((column, text));
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row" && cells.Count > 0)
            {
                if (rowsEmitted++ >= MaxRowsPerSheet)
                {
                    output.Line($"[… строки после {MaxRowsPerSheet} пропущены]");
                    break;
                }
                // Only up to the last non-empty cell; gaps inside the row stay as empty cells.
                var width = cells.Max(c => c.Column) + 1;
                var values = new string[width];
                Array.Fill(values, string.Empty);
                foreach (var (col, value) in cells) values[col] = value;
                output.Line(TableRow(values));
                cells.Clear();
            }
            reader.Read();
        }
    }

    private static string ReadCell(XmlReader reader, IReadOnlyList<string> shared, IReadOnlyList<CellFormat> formats, bool date1904)
    {
        var type = Attr(reader, "t");
        var style = IntAttr(reader, "s") ?? 0;
        if (reader.IsEmptyElement) return string.Empty;

        string? value = null, formula = null;
        StringBuilder? inline = null;
        var depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "rPh":
                        reader.Skip();
                        continue;
                    case "v":
                        value = ReadElementText(reader, MaxCellChars);
                        break;
                    case "f":
                        formula = ReadElementText(reader, MaxCellChars);
                        break;
                    case "t":
                        inline ??= new StringBuilder();
                        ReadElementText(reader, inline, MaxCellChars);
                        break;
                }
            }
            reader.Read();
        }

        return type switch
        {
            "s" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Count ? shared[index] : string.Empty,
            "inlineStr" => inline?.ToString() ?? string.Empty,
            "b" => value is null ? string.Empty : value == "1" ? "TRUE" : "FALSE",
            "e" or "str" or "d" => value ?? string.Empty,
            _ when value is null => formula is null ? string.Empty : "=" + formula,
            _ => FormatNumber(value, style >= 0 && style < formats.Count ? formats[style] : CellFormat.Number, date1904),
        };
    }

    private static string FormatNumber(string value, CellFormat format, bool date1904)
    {
        if (format == CellFormat.Number || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return value;
        }
        if (format == CellFormat.Percent)
        {
            return (number * 100).ToString("0.##########", CultureInfo.InvariantCulture) + "%";
        }

        if (date1904) number += 1462;
        if (number is < 0 or > 2_958_465) return value;
        var date = DateTime.FromOADate(number);
        var hasTime = date.TimeOfDay.Ticks != 0;
        return format switch
        {
            CellFormat.Time => date.ToString(date.TimeOfDay.Seconds != 0 ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture),
            CellFormat.DateTime => date.ToString(hasTime ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => date.ToString(hasTime ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    private static List<string> ReadSharedStrings(SafeZip zip, string? path)
    {
        var result = new List<string>();
        using var reader = zip.OpenXml(path);
        if (reader is null) return result;

        long total = 0;
        var item = new StringBuilder();
        reader.MoveToContent();
        while (!reader.EOF && result.Count < MaxSharedStrings)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "si" when reader.IsEmptyElement:
                        result.Add(string.Empty);
                        break;
                    case "si":
                        item.Clear();
                        break;
                    case "rPh":
                        reader.Skip();
                        continue;
                    case "t":
                        ReadElementText(reader, item, MaxCellChars);
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si")
            {
                // Past the character cap the table keeps its indexes but not the text.
                var text = total < MaxSharedStringChars ? item.ToString() : string.Empty;
                total += text.Length;
                result.Add(text);
            }
            reader.Read();
        }
        return result;
    }

    /// <summary>The number-format kind of every cell style (cellXfs), to show dates as dates.</summary>
    private static List<CellFormat> ReadCellFormats(SafeZip zip, string? path)
    {
        var result = new List<CellFormat>();
        using var reader = zip.OpenXml(path);
        if (reader is null) return result;

        var custom = new Dictionary<int, string>();
        var inCellXfs = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "numFmt" when IntAttr(reader, "numFmtId") is { } id:
                        custom[id] = Attr(reader, "formatCode") ?? string.Empty;
                        break;
                    case "cellXfs":
                        inCellXfs = !reader.IsEmptyElement;
                        break;
                    case "xf" when inCellXfs && result.Count < 65_536:
                        var formatId = IntAttr(reader, "numFmtId") ?? 0;
                        result.Add(Classify(formatId, custom.TryGetValue(formatId, out var code) ? code : null));
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs")
            {
                inCellXfs = false;
            }
        }
        return result;
    }

    private static CellFormat Classify(int id, string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return id switch
            {
                >= 14 and <= 17 or >= 27 and <= 36 or >= 50 and <= 58 => CellFormat.Date,
                >= 18 and <= 21 or >= 45 and <= 47 => CellFormat.Time,
                22 => CellFormat.DateTime,
                9 or 10 => CellFormat.Percent,
                _ => CellFormat.Number,
            };
        }

        var cleaned = FormatNoiseRegex().Replace(code.Split(';')[0], string.Empty).ToLowerInvariant();
        var date = cleaned.IndexOfAny(new[] { 'd', 'y' }) >= 0;
        var time = cleaned.IndexOfAny(new[] { 'h', 's' }) >= 0;
        if (date && time) return CellFormat.DateTime;
        if (date || (cleaned.Contains('m') && !time)) return CellFormat.Date;
        if (time) return CellFormat.Time;
        return cleaned.Contains('%') ? CellFormat.Percent : CellFormat.Number;
    }

    // Quoted literals, escaped characters and [colour]/[$-locale] sections — everything but [h]/[m]/[s].
    [GeneratedRegex(@"""[^""]*""|\\.|\[(?![hms]+\])[^\]]*\]")]
    private static partial Regex FormatNoiseRegex();

    /// <summary>"C12" → 2. -1 unless the reference is ASCII letters A..XFD followed by digits.</summary>
    internal static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return -1;
        var index = 0;
        var i = 0;
        for (; i < reference.Length && i < 4; i++)
        {
            var ch = reference[i];
            if (ch is >= 'a' and <= 'z') ch = (char)(ch - 'a' + 'A');
            if (ch is < 'A' or > 'Z') break;
            index = index * 26 + (ch - 'A' + 1);
        }
        if (i == 0 || i > 3 || index > 16_384) return -1;

        var digits = 0;
        for (; i < reference.Length; i++, digits++)
        {
            if (reference[i] is < '0' or > '9' || digits >= 7) return -1;
        }
        return digits == 0 ? -1 : index - 1;
    }

    // ------------------------------------------------------------------ pptx

    // OFFICE_FORMATS: добавлено 2026-09-23 — slides in presentation order, one paragraph per line.
    private static void ReadPptx(SafeZip zip, TextBudget output)
    {
        var presentation = zip.MainPart("ppt/presentation.xml");
        var relationships = zip.Relationships(presentation);

        var slideIds = new List<string>();
        using (var reader = zip.OpenXml(presentation))
        {
            if (reader is null) return;
            while (reader.Read() && slideIds.Count < MaxSlides)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sldId" && RelationshipId(reader) is { } id)
                {
                    slideIds.Add(id);
                }
            }
        }

        var number = 0;
        foreach (var id in slideIds)
        {
            if (output.Exhausted) break;
            if (!relationships.TryGetValue(id, out var slide)) continue;
            output.Line($"## Слайд {++number}");
            ReadSlide(zip, slide.Target, output);
            output.BlankLine();
        }
    }

    private static void ReadSlide(SafeZip zip, string path, TextBudget output)
    {
        using var reader = zip.OpenXml(path);
        if (reader is null) return;

        var paragraph = new StringBuilder();
        var inParagraph = 0;
        var cells = new Stack<StringBuilder>();
        var rows = new Stack<List<string>>();
        reader.MoveToContent();
        while (!reader.EOF && !output.Exhausted)
        {
            var drawing = reader.NamespaceURI.Contains("drawingml", StringComparison.Ordinal);
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "Fallback")
                {
                    reader.Skip();
                    continue;
                }
                var empty = reader.IsEmptyElement;
                switch (reader.LocalName)
                {
                    case "p" when drawing && !empty:
                        if (inParagraph++ == 0) paragraph.Clear();
                        break;
                    case "t" when drawing && inParagraph > 0:
                        ReadElementText(reader, paragraph, MaxOutputChars);
                        break;
                    case "br" when drawing && inParagraph > 0:
                        paragraph.Append('\n');
                        break;
                    case "tr" when drawing && !empty:
                        rows.Push(new List<string>());
                        break;
                    case "tc" when drawing && empty:
                        if (rows.Count > 0) rows.Peek().Add(string.Empty);
                        break;
                    case "tc" when drawing:
                        cells.Push(new StringBuilder());
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && drawing)
            {
                switch (reader.LocalName)
                {
                    case "p" when inParagraph > 0:
                        if (--inParagraph > 0) break;
                        var text = paragraph.ToString();
                        if (text.Trim().Length == 0) break;
                        if (cells.Count > 0)
                        {
                            if (cells.Peek().Length > 0) cells.Peek().Append(' ');
                            cells.Peek().Append(text.Replace('\n', ' '));
                        }
                        else
                        {
                            output.Line(text);
                        }
                        break;
                    case "tc" when cells.Count > 0:
                        var cell = cells.Pop().ToString().Trim();
                        if (rows.Count > 0) rows.Peek().Add(cell);
                        break;
                    case "tr" when rows.Count > 0:
                        var row = rows.Pop();
                        if (cells.Count > 0) cells.Peek().Append(' ').Append(string.Join(" | ", row));
                        else output.Line(TableRow(row.Take(MaxColumns)));
                        break;
                }
            }
            reader.Read();
        }
    }

    // ------------------------------------------------------------------ pdf

    private static void ReadPdf(byte[] bytes, TextBudget output)
    {
        // PdfPig (MIT) extracts text in reading order, honoring ToUnicode CMaps, so
        // Cyrillic and multi-column/multi-page PDFs are handled far better than the
        // previous regex-based parser.
        using var document = PdfDocument.Open(bytes);
        var pages = 0;
        foreach (var page in document.GetPages())
        {
            if (output.Exhausted) break;
            if (++pages > MaxPdfPages)
            {
                output.MarkTruncated();
                break;
            }
            output.Line(page.Text);
        }
    }

    // ------------------------------------------------------------------ output

    /// <summary>Collects extracted text up to a character budget, without splitting a surrogate pair.</summary>
    private sealed class TextBudget
    {
        private readonly StringBuilder _text = new();
        private readonly int _max;
        private bool _blank = true;

        public TextBudget(int max)
        {
            _max = max;
        }

        public bool Exhausted { get; private set; }

        public void MarkTruncated() => Exhausted = true;

        public void Append(string text)
        {
            if (Exhausted || text.Length == 0) return;
            var room = _max - _text.Length;
            if (text.Length <= room)
            {
                _text.Append(text);
                return;
            }
            _text.Append(text, 0, TextSanitizer.SafeCut(text, room));
            Exhausted = true;
        }

        public void Line(string text)
        {
            Append(text);
            Append("\n");
            _blank = text.Trim().Length == 0;
        }

        /// <summary>One empty line, never several in a row.</summary>
        public void BlankLine()
        {
            if (_blank) return;
            Append("\n");
            _blank = true;
        }

        public override string ToString()
        {
            var text = TextSanitizer.Clean(_text.ToString()).Trim();
            return Exhausted
                ? text + $"\n\n[… текст документа обрезан: показаны первые {_max} символов]"
                : text;
        }
    }
}

// TEXT_DECODING: добавлено 2026-09-24 (ревью M6)
/// <summary>
/// Makes extracted text safe to store and to cut: PostgreSQL rejects NUL in <c>text</c>, Npgsql rejects
/// lone surrogates, and a cut between the halves of a surrogate pair creates one.
/// </summary>
public static class TextSanitizer
{
    /// <summary>
    /// Removes NUL and other control characters (except tab, line feed and carriage return), DEL, C1
    /// controls, lone surrogates, U+FFFE/U+FFFF and byte-order marks. Returns the same instance when
    /// there is nothing to remove.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var firstBad = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }
            if (IsUnwanted(text[i]))
            {
                firstBad = i;
                break;
            }
        }
        if (firstBad < 0) return text;

        var sb = new StringBuilder(text.Length);
        sb.Append(text, 0, firstBad);
        for (var i = firstBad; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(ch).Append(text[++i]);
                continue;
            }
            if (!IsUnwanted(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    private static bool IsUnwanted(char ch) =>
        (ch < ' ' && ch is not ('\t' or '\n' or '\r'))
        || ch is >= '\u007F' and <= '\u009F'
        || char.IsSurrogate(ch)
        || ch is '￾' or '￿' or '﻿';

    /// <summary>A cut position at or below <paramref name="max"/> that does not split a surrogate pair.</summary>
    public static int SafeCut(string text, int max)
    {
        if (max >= text.Length) return text.Length;
        if (max <= 0) return 0;
        return char.IsHighSurrogate(text[max - 1]) && char.IsLowSurrogate(text[max]) ? max - 1 : max;
    }

    /// <summary>The first <paramref name="max"/> characters (fewer when that would split a surrogate pair).</summary>
    public static string Truncate(string text, int max) => text.Length <= max ? text : text[..SafeCut(text, max)];
}
