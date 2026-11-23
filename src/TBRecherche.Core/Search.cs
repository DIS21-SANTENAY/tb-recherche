using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TBRecherche.Core;

public enum SortMode { Pertinence, DateDesc, DateAsc }

public sealed class SearchQuery
{
    public string Text = "";
    public string? Account;
    public DateTime? From;
    public DateTime? To;
    public bool AttachOnly;
    public SortMode Sort = SortMode.DateDesc;
    public int Limit = 1000;
}

public sealed class SearchHit
{
    public long Id { get; init; }
    public DateTime Date { get; init; }
    public string Sender { get; init; } = "";
    public string Recipients { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Account { get; init; } = "";
    public string Folder { get; init; } = "";
    public int Attach { get; init; }
    public string AttachNames { get; init; } = "";
    public string Snippet { get; init; } = "";
    public string MessageId { get; init; } = "";
    public string Path { get; init; } = "";
    public long Offset { get; init; }
    public bool Maildir { get; init; }
    /// <summary>Un des termes cherchés figure dans le texte d'une pièce jointe.</summary>
    public bool InAttachment { get; set; }
    public string DateText => Date == DateTime.MinValue ? "" : Date.ToString("dd/MM/yyyy HH:mm");
}

public sealed record ParsedQuery(string? Fts, List<string> HighlightTerms, List<string> FolderFilters, string? AttachProbe);

public sealed record AccountCount(string Name, long Count);

public static class Search
{
    // Préfixes acceptés dans la saisie -> colonnes FTS
    static readonly Dictionary<string, string> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de"] = "sender", ["from"] = "sender", ["exp"] = "sender",
        ["a"] = "recipients", ["à"] = "recipients", ["to"] = "recipients", ["dest"] = "recipients",
        ["objet"] = "subject", ["sujet"] = "subject", ["subject"] = "subject",
        ["corps"] = "body", ["texte"] = "body", ["body"] = "body",
        ["pj"] = "attachments", ["fichier"] = "attachments",
        ["contenu"] = "attachtext", ["dans"] = "attachtext",
    };

    static readonly Regex Token = new(@"(?<neg>-)?(?:(?<field>[\p{L}]+):)?(?:""(?<phrase>[^""]*)""?|(?<word>[^\s""]+))", RegexOptions.Compiled);

    /// <summary>
    /// Transforme la saisie en requête FTS5 :
    ///   mots simples = tous obligatoires, recherche par début de mot ;
    ///   "phrase exacte" ; -exclure ; OU entre deux termes ;
    ///   de: a: objet: corps: pj: (nom de PJ) contenu: (texte des PJ) ; dossier: filtre sur le dossier.
    /// </summary>
    public static ParsedQuery Parse(string input)
    {
        var pos = new List<string>();
        var neg = new List<string>();
        var hl = new List<string>();
        var folders = new List<string>();
        var probe = new List<string>();
        bool pendingOr = false;

        foreach (Match m in Token.Matches(input ?? ""))
        {
            var word = m.Groups["word"].Success ? m.Groups["word"].Value : null;
            var phrase = m.Groups["phrase"].Success ? m.Groups["phrase"].Value : null;
            var field = m.Groups["field"].Success ? m.Groups["field"].Value : null;

            if (word is "OU" or "OR" && field == null && !m.Groups["neg"].Success) { pendingOr = pos.Count > 0; continue; }

            if (field != null && field.Equals("dossier", StringComparison.OrdinalIgnoreCase))
            {
                var f = (phrase ?? word ?? "").Trim();
                if (f.Length > 0) folders.Add(f);
                continue;
            }

            string? col = null;
            if (field != null && !Fields.TryGetValue(field, out col))
            {
                // "xxx:yyy" qui n'est pas un champ connu (ex. une heure) : on garde le texte brut
                word = field + ":" + (word ?? phrase);
                phrase = null;
            }

            var text = (phrase ?? word ?? "").Trim();
            var clean = Tokens(text);
            if (clean.Count == 0) continue;

            bool prefix = phrase == null && clean[^1].Length >= 2;
            string phraseExpr = "\"" + string.Join(" ", clean) + "\"" + (prefix ? "*" : "");
            string expr = col != null ? col + " : " + phraseExpr : phraseExpr;

            if (m.Groups["neg"].Success) { neg.Add(expr); continue; }
            hl.AddRange(phrase != null ? new[] { string.Join(" ", clean) } : clean);
            if (col is null or "attachtext" && clean.All(t => t.Length >= 3)) probe.Add("attachtext : " + phraseExpr);

            if (pendingOr && pos.Count > 0) pos[^1] = "(" + pos[^1] + " OR " + expr + ")";
            else pos.Add(expr);
            pendingOr = false;
        }

        var p = probe.Count > 0 ? string.Join(" OR ", probe) : null;
        if (pos.Count == 0) return new ParsedQuery(null, hl, folders, null);
        var q = string.Join(" AND ", pos);
        foreach (var n in neg) q = "(" + q + ") NOT " + n;
        return new ParsedQuery(q, hl, folders, p);
    }

    /// <summary>Découpe comme le tokenizer unicode61 : lettres et chiffres, le reste est séparateur.</summary>
    public static List<string> Tokens(string s)
    {
        var l = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { l.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) l.Add(sb.ToString());
        return l;
    }

    public static List<SearchHit> Run(IndexDb db, SearchQuery q, out ParsedQuery parsed, CancellationToken ct = default)
    {
        parsed = Parse(q.Text);
        var sql = new StringBuilder("""
            SELECT m.id, m.date, m.sender, m.recipients, m.subject, f.account, f.folder, m.attach, m.attach_names,
                   f.path, m.offset, f.maildir, m.snippet, m.msgid
            FROM messages m JOIN files f ON f.id = m.file_id
            """);
        var p = new List<(string, object?)>();
        var where = new List<string>();

        if (parsed.Fts != null)
        {
            sql.Append(" JOIN fts ON fts.rowid = m.id");
            where.Add("fts MATCH $q");
            p.Add(("$q", parsed.Fts));
        }
        for (int i = 0; i < parsed.FolderFilters.Count; i++)
        {
            where.Add($"f.folder LIKE $fo{i}");
            p.Add(($"$fo{i}", "%" + parsed.FolderFilters[i] + "%"));
        }
        if (!string.IsNullOrEmpty(q.Account)) { where.Add("f.account = $acc"); p.Add(("$acc", q.Account)); }
        if (q.From is { } d1) { where.Add("m.date >= $d1"); p.Add(("$d1", new DateTimeOffset(d1.Date).ToUnixTimeSeconds())); }
        if (q.To is { } d2) { where.Add("m.date < $d2"); p.Add(("$d2", new DateTimeOffset(d2.Date.AddDays(1)).ToUnixTimeSeconds())); }
        if (q.AttachOnly) where.Add("m.attach > 0");

        if (where.Count > 0) sql.Append(" WHERE ").Append(string.Join(" AND ", where));
        sql.Append(q.Sort switch
        {
            SortMode.Pertinence when parsed.Fts != null => " ORDER BY bm25(fts, 6.0, 4.0, 2.0, 1.0, 3.0, 0.8), m.date DESC",
            SortMode.DateAsc => " ORDER BY m.date ASC",
            _ => " ORDER BY m.date DESC",
        });
        sql.Append(" LIMIT $lim");
        p.Add(("$lim", q.Limit));

        var hits = new List<SearchHit>();
        using (var c = db.Cmd(sql.ToString(), p.ToArray()))
        using (ct.Register(() => { try { c.Cancel(); } catch { } }))
        using (var r = c.ExecuteReader())
        {
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                var unix = r.GetInt64(1);
                hits.Add(new SearchHit
                {
                    Id = r.GetInt64(0),
                    Date = unix == 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime,
                    Sender = r.IsDBNull(2) ? "" : r.GetString(2),
                    Recipients = r.IsDBNull(3) ? "" : r.GetString(3),
                    Subject = r.IsDBNull(4) ? "" : r.GetString(4),
                    Account = r.GetString(5),
                    Folder = r.GetString(6),
                    Attach = r.GetInt32(7),
                    AttachNames = r.IsDBNull(8) ? "" : r.GetString(8),
                    Path = r.GetString(9),
                    Offset = r.GetInt64(10),
                    Maildir = r.GetInt32(11) != 0,
                    Snippet = r.IsDBNull(12) ? "" : r.GetString(12),
                    MessageId = r.IsDBNull(13) ? "" : r.GetString(13),
                });
            }
        }

        ct.ThrowIfCancellationRequested();
        MarkAttachmentHits(db, hits, parsed, ct);
        return hits;
    }

    /// <summary>Repère les résultats dont une pièce jointe contient un des termes (affiché « trouvé dans une PJ »).</summary>
    static void MarkAttachmentHits(IndexDb db, List<SearchHit> hits, ParsedQuery parsed, CancellationToken ct)
    {
        if (parsed.AttachProbe == null || hits.Count == 0) return;
        // Limité aux premiers résultats : la sonde coûte une seconde requête plein texte
        var withAttach = hits.Take(300).Where(h => h.Attach > 0).ToList();
        if (withAttach.Count == 0) return;
        try
        {
            var found = new HashSet<long>();
            foreach (var chunk in withAttach.Chunk(500))
            {
                var ids = string.Join(",", chunk.Select(h => h.Id));
                using var c = db.Cmd($"SELECT rowid FROM fts WHERE fts MATCH $p AND rowid IN ({ids})", ("$p", parsed.AttachProbe));
                using var reg = ct.Register(() => { try { c.Cancel(); } catch { } });
                using var r = c.ExecuteReader();
                while (r.Read()) found.Add(r.GetInt64(0));
            }
            foreach (var h in withAttach) h.InAttachment = found.Contains(h.Id);
        }
        catch { }
    }

    public static List<AccountCount> AccountCounts(IndexDb db)
    {
        var l = new List<AccountCount>();
        using var c = db.Cmd("SELECT f.account, count(m.id) FROM files f LEFT JOIN messages m ON m.file_id = f.id GROUP BY f.account ORDER BY f.account");
        using var r = c.ExecuteReader();
        while (r.Read()) l.Add(new AccountCount(r.GetString(0), r.GetInt64(1)));
        return l;
    }

    public static (long Messages, long Files) Stats(IndexDb db) =>
        ((long)db.Scalar("SELECT count(*) FROM messages")!, (long)db.Scalar("SELECT count(*) FROM files")!);

    /// <summary>Forme "pliée" d'un caractère (minuscule, sans accent), même longueur que l'original.</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch < 128) { sb.Append(char.ToLowerInvariant(ch)); continue; }
            var d = ch.ToString().Normalize(NormalizationForm.FormD);
            var b = d.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark ? d[0] : ch;
            sb.Append(char.ToLowerInvariant(b));
        }
        return sb.ToString();
    }
}
