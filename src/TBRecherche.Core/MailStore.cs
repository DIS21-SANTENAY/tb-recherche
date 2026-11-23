namespace TBRecherche.Core;

public sealed record StoreFile(string Path, string Account, string Folder, bool IsMaildirMessage);

/// <summary>Parcourt l'arborescence d'un compte (mbox ou maildir) et liste les fichiers à indexer.</summary>
public static class MailStore
{
    static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".msf", ".dat", ".json", ".html", ".htm", ".sqlite", ".sqlite-wal", ".sqlite-shm", ".log", ".js",
        ".bak", ".tmp", ".wdseml", ".mozmsgs", ".txt", ".ini", ".lock", ".xml", ".sbd"
    };

    public static IEnumerable<StoreFile> Enumerate(MailAccount acc)
    {
        if (!acc.Exists) yield break;
        var items = acc.Maildir ? WalkMaildir(acc.RootDir, "") : WalkMbox(acc.RootDir, "");
        foreach (var (path, folder, md) in items)
            yield return new StoreFile(path, acc.Name, folder, md);
    }

    static string Join(string prefix, string name)
    {
        name = DecodeImapUtf7(name);
        return prefix.Length == 0 ? name : prefix + "/" + name;
    }

    /// <summary>Noms de dossiers IMAP en UTF-7 modifié (RFC 3501) : "re&amp;AOc-ues" -> "reçues", "&amp;-" -> "&amp;".</summary>
    public static string DecodeImapUtf7(string s)
    {
        if (!s.Contains('&')) return s;
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            int amp = s.IndexOf('&', i);
            if (amp < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, amp - i);
            int dash = s.IndexOf('-', amp + 1);
            if (dash < 0) { sb.Append(s, amp, s.Length - amp); break; }
            if (dash == amp + 1) sb.Append('&');
            else
            {
                var b64 = s.Substring(amp + 1, dash - amp - 1).Replace(',', '/');
                b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
                try { sb.Append(System.Text.Encoding.BigEndianUnicode.GetString(Convert.FromBase64String(b64))); }
                catch { sb.Append(s, amp, dash - amp + 1); }
            }
            i = dash + 1;
        }
        return sb.ToString();
    }

    static IEnumerable<(string, string, bool)> WalkMbox(string dir, string prefix)
    {
        string[] files, dirs;
        try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
        catch { yield break; }

        foreach (var f in files)
            if (IsMbox(f)) yield return (f, Join(prefix, Path.GetFileName(f)), false);

        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (name.EndsWith(".sbd", StringComparison.OrdinalIgnoreCase))
                foreach (var x in WalkMbox(d, Join(prefix, name[..^4]))) yield return x;
        }
    }

    static IEnumerable<(string, string, bool)> WalkMaildir(string dir, string prefix)
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(dir); }
        catch { yield break; }

        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (name.EndsWith(".sbd", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var x in WalkMaildir(d, Join(prefix, name[..^4]))) yield return x;
                continue;
            }
            var cur = Path.Combine(d, "cur");
            if (!Directory.Exists(cur)) continue;
            IEnumerable<string> msgs;
            try { msgs = Directory.EnumerateFiles(cur); } catch { continue; }
            foreach (var m in msgs) yield return (m, Join(prefix, name), true);
        }
    }

    /// <summary>Un mbox Thunderbird n'a pas d'extension connue et commence par "From ".</summary>
    public static bool IsMbox(string path)
    {
        if (SkipExt.Contains(Path.GetExtension(path))) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < 5) return false;
            Span<byte> b = stackalloc byte[5];
            fs.ReadExactly(b);
            return b.SequenceEqual("From "u8);
        }
        catch { return false; }
    }

    public static FileStream OpenShared(string path, bool sequential = false) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16,
            sequential ? FileOptions.SequentialScan : FileOptions.RandomAccess);
}
