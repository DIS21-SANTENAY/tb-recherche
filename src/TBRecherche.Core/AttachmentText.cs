using System.IO.Compression;
using System.Text;
using System.Xml;
using MimeKit;
using UglyToad.PdfPig;

namespace TBRecherche.Core;

/// <summary>Extraction du texte des pièces jointes pour l'index : PDF, Office (docx/xlsx/pptx), OpenDocument, texte, HTML.</summary>
public static class AttachmentText
{
    public const long MaxDecodedBytes = 25L << 20;   // au-delà, on n'indexe que le nom
    public const int MaxCharsPerFile = 120_000;
    public const int MaxPdfPages = 80;
    static readonly TimeSpan PdfTimeout = TimeSpan.FromSeconds(25);

    public static bool IsSupported(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext is ".pdf" or ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".pptx" or ".odt" or ".ods" or ".odp"
            or ".txt" or ".csv" or ".md" or ".log" or ".htm" or ".html" or ".xml" or ".rtf";
    }

    public static string Extract(MimePart part, CancellationToken ct = default)
    {
        var name = part.FileName ?? "";
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (!IsSupported(name) || part.Content == null) return "";

        try
        {
            using var ms = new MemoryStream();
            using (var limited = new LimitStream(ms, MaxDecodedBytes))
                part.Content.DecodeTo(limited, ct);
            ms.Position = 0;

            var text = ext switch
            {
                ".pdf" => Pdf(ms.ToArray()),
                ".docx" or ".docm" => ZipXml(ms, e => e == "word/document.xml" || e.StartsWith("word/header") || e.StartsWith("word/footer"), "t"),
                ".xlsx" or ".xlsm" => ZipXml(ms, e => e == "xl/sharedStrings.xml" || e.StartsWith("xl/worksheets/sheet"), "t"),
                ".pptx" => ZipXml(ms, e => e.StartsWith("ppt/slides/slide") && e.EndsWith(".xml"), "t"),
                ".odt" or ".ods" or ".odp" => ZipXml(ms, e => e == "content.xml", null),
                ".htm" or ".html" => MessageExtract.HtmlToPlain(Decode(part, ms)),
                ".rtf" => Rtf(Decode(part, ms)),
                _ => Decode(part, ms),
            };
            text = Normalize(text);
            return text.Length > MaxCharsPerFile ? text[..MaxCharsPerFile] : text;
        }
        catch (LimitExceeded) { return ""; }
        catch (OperationCanceledException) { throw; }
        catch { return ""; }
    }

    static string Decode(MimePart part, MemoryStream ms)
    {
        var bytes = ms.ToArray();
        var charset = part.ContentType.Charset;
        Encoding enc;
        try { enc = charset != null ? Encoding.GetEncoding(charset) : Utf8OrLatin(bytes); }
        catch { enc = Utf8OrLatin(bytes); }
        return enc.GetString(bytes);
    }

    static Encoding Utf8OrLatin(byte[] b)
    {
        try { new UTF8Encoding(false, true).GetString(b); return Encoding.UTF8; }
        catch { return Encoding.GetEncoding(1252); }
    }

    /// <summary>PdfPig dans une tâche bornée dans le temps : un PDF pathologique ne bloque pas l'indexation.</summary>
    static string Pdf(byte[] bytes)
    {
        var task = Task.Run(() =>
        {
            var sb = new StringBuilder();
            using var doc = PdfDocument.Open(bytes);
            int n = 0;
            foreach (var page in doc.GetPages())
            {
                if (++n > MaxPdfPages || sb.Length > MaxCharsPerFile) break;
                foreach (var w in page.GetWords()) sb.Append(w.Text).Append(' ');
                sb.Append('\n');
            }
            return sb.ToString();
        });
        return task.Wait(PdfTimeout) ? task.Result : "";
    }

    /// <summary>Texte des éléments XML voulus (ou de tous les nœuds texte si <paramref name="localNames"/> vaut null).</summary>
    static string ZipXml(Stream s, Func<string, bool> entryFilter, params string[]? localNames)
    {
        var sb = new StringBuilder();
        using var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
        var wanted = localNames is { Length: > 0 } ? new HashSet<string>(localNames) : null;
        foreach (var entry in zip.Entries.Where(e => entryFilter(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            if (entry.Length > MaxDecodedBytes) continue;
            using var es = entry.Open();
            using var xr = XmlReader.Create(es, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            bool capture = false;
            while (xr.Read())
            {
                switch (xr.NodeType)
                {
                    case XmlNodeType.Element:
                        capture = wanted == null || wanted.Contains(xr.LocalName);
                        // Fins de paragraphe / cellules : on sépare les mots
                        if (xr.LocalName is "p" or "tc" or "c" or "br" or "tab" or "h") sb.Append(' ');
                        if (xr.IsEmptyElement) capture = false;
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace when capture:
                        sb.Append(xr.Value);
                        break;
                    case XmlNodeType.EndElement:
                        if (capture) sb.Append(' ');
                        capture = false;
                        break;
                }
                if (sb.Length > MaxCharsPerFile) return sb.ToString();
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static string Rtf(string rtf)
    {
        // Suffisant pour la recherche : on retire groupes de contrôle et mots-clés
        var s = System.Text.RegularExpressions.Regex.Replace(rtf, @"\\'([0-9a-fA-F]{2})",
            m => Encoding.GetEncoding(1252).GetString(new[] { Convert.ToByte(m.Groups[1].Value, 16) }));
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[a-zA-Z]+-?\d* ?|[{}]", " ");
        return s;
    }

    static string Normalize(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();

    sealed class LimitExceeded : Exception { }

    sealed class LimitStream(Stream inner, long max) : Stream
    {
        long written;
        public override void Write(byte[] buffer, int offset, int count)
        {
            written += count;
            if (written > max) throw new LimitExceeded();
            inner.Write(buffer, offset, count);
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }
}
