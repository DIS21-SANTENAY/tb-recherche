using System.Globalization;
using System.Text;
using MimeKit;
using MimeKit.Text;

namespace TBRecherche.Core;

public sealed class MessageRecord
{
    public long Offset;
    public long Date;
    public string Sender = "";
    public string Recipients = "";
    public string Subject = "";
    public string MessageId = "";
    public string Body = "";
    public string AttachNames = "";
    public int AttachCount;
    public string Snippet = "";
    public string AttachText = "";
}

public static class MessageExtract
{
    public const int MaxBodyChars = 150_000;

    static MessageExtract() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static void Init() { }

    /// <summary>Thunderbird garde les messages supprimés (IMAP) jusqu'au compactage, marqués 0x0008.</summary>
    public static bool IsExpunged(MimeMessage msg)
    {
        var s = msg.Headers["X-Mozilla-Status"];
        return s != null && int.TryParse(s.Trim(), NumberStyles.HexNumber, null, out var f) && (f & 0x0008) != 0;
    }

    public static string FormatAddresses(InternetAddressList list) =>
        string.Join(", ", list.Mailboxes.Select(m =>
            string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));

    public static MessageRecord ToRecord(MimeMessage msg, long offset, bool attachText = true, CancellationToken ct = default)
    {
        var r = new MessageRecord { Offset = offset };
        try { r.Date = msg.Date == DateTimeOffset.MinValue ? 0 : msg.Date.ToUnixTimeSeconds(); } catch { }
        try { r.Sender = FormatAddresses(msg.From); } catch { }
        try { r.Recipients = FormatAddresses(msg.To) + (msg.Cc.Count > 0 ? ", " + FormatAddresses(msg.Cc) : ""); } catch { }
        r.Subject = msg.Subject ?? "";
        r.MessageId = msg.MessageId ?? "";
        r.Body = BodyText(msg, MaxBodyChars);
        r.Snippet = MakeSnippet(r.Body);

        var names = new List<string>();
        var texts = new StringBuilder();
        try
        {
            foreach (var a in msg.Attachments)
            {
                var n = a switch
                {
                    MimePart p => p.FileName,
                    MessagePart mp => (mp.Message?.Subject ?? "message") + ".eml",
                    _ => null
                };
                names.Add(string.IsNullOrWhiteSpace(n) ? "(sans nom)" : n);
                if (attachText && a is MimePart part && texts.Length < 400_000)
                {
                    var t = AttachmentText.Extract(part, ct);
                    if (t.Length > 0) texts.Append(t).Append('\n');
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        r.AttachCount = names.Count;
        r.AttachNames = string.Join(" | ", names);
        r.AttachText = texts.ToString();
        return r;
    }

    /// <summary>Début du texte, sans citations ni lignes vides, pour l'aperçu dans la liste.</summary>
    public static string MakeSnippet(string body, int max = 220)
    {
        var sb = new StringBuilder();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('>')) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(line);
            if (sb.Length >= max) break;
        }
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ");
        return s.Length > max ? s[..max] : s;
    }

    public static string BodyText(MimeMessage msg, int max)
    {
        string text = "";
        try
        {
            text = msg.TextBody ?? "";
            if (string.IsNullOrWhiteSpace(text) && msg.HtmlBody is { } html)
                text = HtmlToPlain(html);
        }
        catch { }
        return text.Length > max ? text[..max] : text;
    }

    static readonly HashSet<HtmlTagId> BlockTags = new()
    {
        HtmlTagId.Br, HtmlTagId.P, HtmlTagId.Div, HtmlTagId.TR, HtmlTagId.LI, HtmlTagId.H1, HtmlTagId.H2,
        HtmlTagId.H3, HtmlTagId.H4, HtmlTagId.Table, HtmlTagId.BlockQuote, HtmlTagId.HR
    };

    /// <summary>HTML vers texte brut : garde le texte, saute script/style, retours à la ligne sur les blocs.</summary>
    public static string HtmlToPlain(string html)
    {
        var sb = new StringBuilder(html.Length / 3);
        var tok = new HtmlTokenizer(new StringReader(html));
        int skip = 0;
        while (tok.ReadNextToken(out var t))
        {
            switch (t)
            {
                case HtmlDataToken d when skip == 0:
                    sb.Append(d.Data);
                    break;
                case HtmlTagToken tag:
                    if (tag.Id is HtmlTagId.Script or HtmlTagId.Style or HtmlTagId.Head)
                        skip = Math.Max(0, skip + (tag.IsEndTag ? -1 : tag.IsEmptyElement ? 0 : 1));
                    else if (BlockTags.Contains(tag.Id) && (tag.Id == HtmlTagId.Br || !tag.IsEndTag || tag.Id == HtmlTagId.P))
                        sb.Append('\n');
                    else if (tag.Id is HtmlTagId.TD or HtmlTagId.TH && !tag.IsEndTag)
                        sb.Append('\t');
                    break;
            }
        }
        // Espaces multiples et lignes vides en série
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"[ \t ]+", " ");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s*\n\s*\n\s*", "\n\n").Trim();
    }

    /// <summary>Relit un message à partir de son emplacement (mbox + position, ou fichier maildir).</summary>
    public static MimeMessage Load(string path, long offset, bool maildirMessage)
    {
        if (!maildirMessage) return MboxReader.LoadAt(path, offset);
        using var fs = MailStore.OpenShared(path);
        return new MimeParser(fs, MimeFormat.Entity, persistent: false).ParseMessage();
    }
}
