using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MimeKit;

namespace TBRecherche.Core;

/// <summary>
/// Page HTML affichée dans le volet de lecture. Une politique CSP stricte bloque les scripts du mail
/// et, par défaut, les images distantes (pixels de suivi). Seul notre script de surlignage s'exécute.
/// </summary>
public static class MailHtml
{
    static readonly Regex RemoteRef = new(@"(?:src|background|srcset)\s*=\s*[""']?\s*https?:|url\(\s*[""']?https?:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex CidRef = new(@"(?<a>(?:src|background)\s*=\s*[""']?)cid:(?<id>[^""'\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Image distante dans une balise <img> : src="http..." ou src=http... sans guillemets
    static readonly Regex RemoteImgSrc = new(@"(?<a>\bsrc\s*=\s*)(?:""\s*https?:[^""]*""|'\s*https?:[^']*'|https?:[^\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex RemoteSrcset = new(@"\bsrcset\s*=\s*(?:""[^""]*""|'[^']*')", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    const string Transparent = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";
    static readonly Regex Url = new(@"\b(https?://[^\s<>""']+[^\s<>""'.,;:!?)\]])", RegexOptions.Compiled);

    public static string Build(MimeMessage msg, IReadOnlyList<string> terms, bool allowRemote, out bool hasRemote,
        bool script = true, bool inlineImages = true)
    {
        string content;
        var html = SafeHtmlBody(msg);
        if (!string.IsNullOrWhiteSpace(html))
        {
            content = inlineImages ? InlineCid(msg, html) : html;
            hasRemote = RemoteRef.IsMatch(content);
            // Images bloquées : une image transparente garde la mise en page sans afficher
            // le texte de remplacement, qu'Edge écrase lettre par lettre dans les petites cases
            if (hasRemote && !allowRemote)
            {
                content = RemoteImgSrc.Replace(content, m => m.Groups["a"].Value + "\"" + Transparent + "\"");
                content = RemoteSrcset.Replace(content, "");
            }
        }
        else
        {
            content = PlainToHtml(MessageExtract.BodyText(msg, 400_000));
            hasRemote = false;
        }

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var remote = allowRemote ? " http: https:" : "";
        var csp = $"default-src 'none'; img-src data:{remote}; style-src 'unsafe-inline'{remote}; font-src data:{remote}; script-src 'nonce-{nonce}'";
        var termsJson = JsonSerializer.Serialize(terms.Where(t => t.Length > 0).Distinct().ToArray());

        return $$"""
            <!doctype html>
            <html lang="fr"><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{{csp}}">
            <style>
              html { background: #fff; }
              body { margin: 0; padding: 6px 6px 40px; font: 14px/1.55 "Segoe UI", system-ui, sans-serif; color: #1D1B1A;
                     word-wrap: break-word; overflow-wrap: anywhere; }
              img { max-width: 100%; height: auto; }
              table { max-width: 100%; }
              a { color: #B3264A; }
              .plain { white-space: pre-wrap; }
              .plain .q { color: #8A847E; border-left: 3px solid #E8E5E1; padding-left: 10px; display: block; }
              blockquote { border-left: 3px solid #E8E5E1; margin-left: 4px; padding-left: 12px; color: #4A4643; }
              mark.tbr { background: #FFE58A; color: inherit; border-radius: 3px; box-shadow: 0 0 0 2px #FFE58A; }
              mark.tbr.first { background: #FFD24A; box-shadow: 0 0 0 2px #FFD24A; }
              ::-webkit-scrollbar { width: 10px; height: 10px; }
              ::-webkit-scrollbar-thumb { background: #C9C4BE; border-radius: 5px; border: 2px solid #fff; }
            </style></head>
            <body>{{content}}
            {{(script ? $"<script nonce=\"{nonce}\">{HighlightScript(termsJson)}</script>" : "")}}
            </body></html>
            """;
    }

    static string? SafeHtmlBody(MimeMessage msg)
    {
        try { return msg.HtmlBody; } catch { return null; }
    }

    /// <summary>Images intégrées (cid:) converties en data: pour s'afficher sans accès réseau.</summary>
    static string InlineCid(MimeMessage msg, string html)
    {
        var parts = new Dictionary<string, MimePart>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in msg.BodyParts.OfType<MimePart>())
                if (!string.IsNullOrEmpty(p.ContentId)) parts[p.ContentId.Trim('<', '>')] = p;
        }
        catch { }
        if (parts.Count == 0) return html;

        var cache = new Dictionary<string, string>();
        return CidRef.Replace(html, m =>
        {
            var id = WebUtility.UrlDecode(m.Groups["id"].Value);
            if (!parts.TryGetValue(id, out var part)) return m.Value;
            if (!cache.TryGetValue(id, out var uri))
            {
                try
                {
                    using var ms = new MemoryStream();
                    part.Content!.DecodeTo(ms);
                    uri = ms.Length > 8 << 20 ? "" : $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(ms.ToArray())}";
                }
                catch { uri = ""; }
                cache[id] = uri;
            }
            return m.Groups["a"].Value + uri;
        });
    }

    static string PlainToHtml(string text)
    {
        var sb = new StringBuilder("<div class=\"plain\">");
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var enc = Url.Replace(WebUtility.HtmlEncode(line), m => $"<a href=\"{m.Value}\">{m.Value}</a>");
            if (line.TrimStart().StartsWith('>')) sb.Append("<span class=\"q\">").Append(enc).Append("</span>");
            else sb.Append(enc).Append('\n');
        }
        return sb.Append("</div>").ToString();
    }

    /// <summary>Surligne les termes en début de mot, sans tenir compte des accents ni de la casse.</summary>
    static string HighlightScript(string termsJson) => """
        (function () {
          const terms =
        """ + termsJson + """
        ;
          if (!terms.length) return;
          const fold = s => { let o = ''; for (let i = 0; i < s.length; i++) { const d = s[i].normalize('NFD')[0]; const l = d.toLowerCase(); o += l.length === 1 ? l : d; } return o; };
          const ft = terms.map(fold).filter(t => t.length);
          const isWord = c => /[\p{L}\p{N}]/u.test(c);
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
            acceptNode: n => (n.parentElement && /^(SCRIPT|STYLE|TITLE)$/.test(n.parentElement.tagName)) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT });
          const nodes = []; while (walker.nextNode()) nodes.push(walker.currentNode);
          let total = 0;
          for (const node of nodes) {
            const text = node.nodeValue; const f = fold(text); const hits = [];
            for (const t of ft) { let i = 0; while ((i = f.indexOf(t, i)) >= 0) { if (i === 0 || !isWord(f[i - 1])) hits.push([i, t.length]); i += t.length; } }
            if (!hits.length) continue;
            hits.sort((a, b) => a[0] - b[0]);
            const frag = document.createDocumentFragment(); let pos = 0;
            for (const [s, len] of hits) {
              if (s < pos) continue;
              if (s > pos) frag.appendChild(document.createTextNode(text.slice(pos, s)));
              const m = document.createElement('mark'); m.className = 'tbr' + (total === 0 ? ' first' : '');
              m.textContent = text.slice(s, s + len); frag.appendChild(m); pos = s + len; total++;
            }
            if (pos < text.length) frag.appendChild(document.createTextNode(text.slice(pos)));
            node.parentNode.replaceChild(frag, node);
            if (total > 3000) break;
          }
          const first = document.querySelector('mark.tbr');
          if (first) first.scrollIntoView({ block: 'center' });
        })();
        """;
}
