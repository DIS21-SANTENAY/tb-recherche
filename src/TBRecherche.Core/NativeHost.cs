using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace TBRecherche.Core;

/// <summary>
/// Hôte "native messaging" pour l'extension Thunderbird : messages JSON précédés de leur longueur (4 octets),
/// sur l'entrée et la sortie standard. Thunderbird lance l'exe quand l'extension se connecte.
/// </summary>
public static class NativeHost
{
    public const string HostName = "tbrecherche";
    public const string ExtensionId = "tb-recherche@dis21.fr";
    const int MaxOutgoing = 1_000_000; // limite de Thunderbird pour un message de l'hôte

    static readonly object WriteLock = new();
    static Stream stdout = Stream.Null;

    /// <summary>Thunderbird lance l'hôte avec le chemin du manifeste et l'identifiant de l'extension.</summary>
    public static bool IsHostLaunch(string[] args) =>
        args.Contains("--native-host") ||
        args.Any(a => a.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && a.Contains(HostName, StringComparison.OrdinalIgnoreCase)) ||
        args.Any(a => a == ExtensionId);

    static readonly string LogPath = Path.Combine(Path.GetDirectoryName(IndexDb.DefaultPath)!, "host.log");

    /// <summary>Journal minimal de l'hôte : Thunderbird masque ses erreurs, ce fichier sert au diagnostic.</summary>
    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {line}{Environment.NewLine}");
        }
        catch { }
    }

    public static void Run(string? profileOverride = null)
    {
        Log("démarrage, arguments : " + string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("exception non gérée : " + e.ExceptionObject);
        MessageExtract.Init();
        // Application à fenêtre (WinExe) : Console.OpenStandardInput renvoie une fin de flux immédiate
        // sur un tuyau. On ouvre directement les poignées héritées de Thunderbird.
        var stdin = StdStream(-10, FileAccess.Read);
        stdout = StdStream(-11, FileAccess.Write);
        var dbPath = IndexDb.DefaultPath;
        var profile = profileOverride ?? ThunderbirdProfile.FindDefaultProfile();
        var hostCts = new CancellationTokenSource();
        var pending = new List<Task>();
        int indexing = 0;

        while (true)
        {
            JsonObject? msg;
            try { msg = ReadMessage(stdin); }
            catch (Exception ex) { Log("lecture impossible : " + ex); break; }
            if (msg == null) { Log("entrée fermée, arrêt"); break; } // l'extension s'est déconnectée
            Log("reçu : " + msg["cmd"]);
            var task = Task.Run(() =>
            {
                long id = msg["id"]?.GetValue<long>() ?? 0;
                try
                {
                    var cmd = msg["cmd"]?.GetValue<string>();
                    switch (cmd)
                    {
                        case "search":
                            Send(SearchReply(id, msg, dbPath));
                            break;
                        case "accounts":
                        {
                            using var db = IndexDb.OpenRead(dbPath);
                            var arr = new JsonArray(Search.AccountCounts(db).Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["count"] = a.Count }).ToArray());
                            Send(new JsonObject { ["id"] = id, ["ok"] = true, ["accounts"] = arr });
                            break;
                        }
                        case "status":
                        {
                            using var db = IndexDb.OpenRead(dbPath);
                            var (m, f) = Search.Stats(db);
                            Send(new JsonObject { ["id"] = id, ["ok"] = true, ["messages"] = m, ["files"] = f, ["profile"] = profile, ["indexing"] = indexing == 1 });
                            break;
                        }
                        case "index":
                            if (profile == null) throw new InvalidOperationException("Profil Thunderbird introuvable.");
                            if (Interlocked.Exchange(ref indexing, 1) == 1)
                            {
                                Send(new JsonObject { ["id"] = id, ["ok"] = true, ["started"] = false });
                                break;
                            }
                            Send(new JsonObject { ["id"] = id, ["ok"] = true, ["started"] = true });
                            try { RunIndex(profile, dbPath, msg["full"]?.GetValue<bool>() == true, hostCts.Token); }
                            finally { Interlocked.Exchange(ref indexing, 0); }
                            break;
                        case "preview":
                            Send(PreviewReply(id, msg, dbPath));
                            break;
                        case "openAttachment":
                            OpenAttachment(msg, dbPath);
                            Send(new JsonObject { ["id"] = id, ["ok"] = true });
                            break;
                        default:
                            throw new InvalidOperationException("Commande inconnue : " + cmd);
                    }
                }
                catch (Exception ex)
                {
                    Log("erreur : " + ex);
                    try { Send(new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = ex.Message, ["busy"] = ex is IndexBusyException }); }
                    catch (Exception ex2) { Log("envoi impossible : " + ex2); }
                }
            });
            lock (pending) { pending.RemoveAll(t => t.IsCompleted); pending.Add(task); }
        }

        hostCts.Cancel();
        try { Task.WaitAll(pending.ToArray(), TimeSpan.FromSeconds(10)); } catch { }
    }

    static JsonObject SearchReply(long id, JsonObject msg, string dbPath)
    {
        var sw = Stopwatch.StartNew();
        DateTime? Date(string k) => DateTime.TryParse(msg[k]?.GetValue<string>(), out var d) ? d : null;
        var q = new SearchQuery
        {
            Text = msg["text"]?.GetValue<string>() ?? "",
            Account = msg["account"]?.GetValue<string>(),
            From = Date("from"),
            To = Date("to"),
            AttachOnly = msg["attachOnly"]?.GetValue<bool>() == true,
            Sort = (msg["sort"]?.GetValue<string>()) switch { "relevance" => SortMode.Pertinence, "old" => SortMode.DateAsc, _ => SortMode.DateDesc },
            Limit = Math.Clamp(msg["limit"]?.GetValue<int>() ?? 300, 1, 500),
        };

        using var db = IndexDb.OpenRead(dbPath);
        List<SearchHit> hits;
        ParsedQuery parsed;
        try { hits = Search.Run(db, q, out parsed); }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = "Recherche incomplète : vérifiez les guillemets et les opérateurs." };
        }

        var arr = new JsonArray();
        int size = 0;
        foreach (var h in hits)
        {
            var o = new JsonObject
            {
                ["id"] = h.Id,
                ["date"] = h.Date == DateTime.MinValue ? null : h.Date.ToString("s"),
                ["sender"] = h.Sender,
                ["subject"] = h.Subject,
                ["snippet"] = h.Snippet.Length > 240 ? h.Snippet[..240] : h.Snippet,
                ["account"] = h.Account,
                ["folder"] = h.Folder,
                ["attach"] = h.Attach,
                ["attachNames"] = h.AttachNames.Length > 300 ? h.AttachNames[..300] : h.AttachNames,
                ["inAttachment"] = h.InAttachment,
                ["msgid"] = h.MessageId,
            };
            size += o.ToJsonString().Length;
            if (size > MaxOutgoing - 50_000) break; // marge sous la limite de 1 Mo
            arr.Add(o);
        }
        return new JsonObject
        {
            ["id"] = id,
            ["ok"] = true,
            ["hits"] = arr,
            ["limited"] = hits.Count >= q.Limit,
            ["terms"] = new JsonArray(parsed.HighlightTerms.Select(t => (JsonNode)t).ToArray()),
            ["ms"] = sw.ElapsedMilliseconds,
        };
    }

    static (string Path, long Offset, bool Maildir, string Account, string Folder, long Date) Locate(string dbPath, long id)
    {
        using var db = IndexDb.OpenRead(dbPath);
        using var c = db.Cmd("SELECT f.path, m.offset, f.maildir, f.account, f.folder, m.date FROM messages m JOIN files f ON f.id = m.file_id WHERE m.id = $id", ("$id", id));
        using var r = c.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Message introuvable dans l'index : actualisez.");
        return (r.GetString(0), r.GetInt64(1), r.GetInt32(2) != 0, r.GetString(3), r.GetString(4), r.GetInt64(5));
    }

    /// <summary>Aperçu prêt à afficher : HTML sécurisé (sans script, images distantes bloquées) + en-têtes + PJ.</summary>
    static JsonObject PreviewReply(long id, JsonObject req, string dbPath)
    {
        var mid = req["message"]!.GetValue<long>();
        var allowRemote = req["allowRemote"]?.GetValue<bool>() == true;
        var terms = req["terms"]?.AsArray().Select(t => t!.GetValue<string>()).ToList() ?? new();
        var loc = Locate(dbPath, mid);
        var msg = MessageExtract.Load(loc.Path, loc.Offset, loc.Maildir);

        var html = MailHtml.Build(msg, terms, allowRemote, out var hasRemote, script: false);
        bool trimmed = false;
        if (Encoding.UTF8.GetByteCount(html) > 800_000)
        {
            // Limite de 1 Mo par message vers Thunderbird : on retire d'abord les images intégrées
            html = MailHtml.Build(msg, terms, allowRemote, out hasRemote, script: false, inlineImages: false);
            trimmed = true;
            if (Encoding.UTF8.GetByteCount(html) > 800_000)
            {
                var text = System.Net.WebUtility.HtmlEncode(MessageExtract.BodyText(msg, 300_000));
                html = $"<!doctype html><meta charset=\"utf-8\"><body style=\"font:14px Segoe UI;white-space:pre-wrap\">{text}</body>";
            }
        }

        var folded = terms.Select(Search.Fold).Where(t => t.Length >= 3).Distinct().ToList();
        var atts = new JsonArray();
        int i = 0;
        foreach (var a in msg.Attachments)
        {
            string name; long size = 0; bool match = false;
            if (a is MimeKit.MessagePart mp) name = (mp.Message?.Subject ?? "message") + ".eml";
            else
            {
                var p = (MimeKit.MimePart)a;
                name = string.IsNullOrWhiteSpace(p.FileName) ? "(sans nom)" : p.FileName;
                try
                {
                    if (p.Content?.Stream is { CanSeek: true } st)
                        size = p.ContentTransferEncoding == MimeKit.ContentEncoding.Base64 ? st.Length * 3 / 4 : st.Length;
                }
                catch { }
                if (folded.Count > 0 && AttachmentText.IsSupported(p.FileName))
                {
                    var t = Search.Fold(AttachmentText.Extract(p));
                    match = folded.Any(f => t.Contains(f, StringComparison.Ordinal));
                }
            }
            atts.Add(new JsonObject { ["index"] = i++, ["name"] = name, ["size"] = size, ["match"] = match });
        }

        return new JsonObject
        {
            ["id"] = id,
            ["ok"] = true,
            ["html"] = html,
            ["hasRemote"] = hasRemote,
            ["trimmed"] = trimmed,
            ["subject"] = msg.Subject ?? "",
            ["fromName"] = msg.From.Mailboxes.FirstOrDefault()?.Name ?? "",
            ["fromAddress"] = msg.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            ["to"] = MessageExtract.FormatAddresses(msg.To),
            ["cc"] = MessageExtract.FormatAddresses(msg.Cc),
            ["date"] = loc.Date == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(loc.Date).LocalDateTime.ToString("s"),
            ["account"] = loc.Account,
            ["folder"] = loc.Folder,
            ["msgid"] = msg.MessageId ?? "",
            ["attachments"] = atts,
        };
    }

    /// <summary>Extrait la pièce jointe dans un dossier temporaire et l'ouvre avec le programme associé.</summary>
    static void OpenAttachment(JsonObject req, string dbPath)
    {
        var mid = req["message"]!.GetValue<long>();
        var index = req["index"]!.GetValue<int>();
        var loc = Locate(dbPath, mid);
        var msg = MessageExtract.Load(loc.Path, loc.Offset, loc.Maildir);
        var att = msg.Attachments.ElementAt(index);
        var dir = Path.Combine(Path.GetTempPath(), "TBRecherche", "pj-" + mid);
        Directory.CreateDirectory(dir);
        string file;
        if (att is MimeKit.MessagePart { Message: not null } mp)
        {
            file = Path.Combine(dir, $"message-joint-{index}.eml");
            mp.Message.WriteTo(file);
        }
        else if (att is MimeKit.MimePart { Content: not null } part)
        {
            var name = string.Join("_", (part.FileName ?? $"piece-{index}").Split(Path.GetInvalidFileNameChars()));
            file = Path.Combine(dir, name);
            using (var fs = File.Create(file)) part.Content.DecodeTo(fs);
        }
        else throw new InvalidOperationException("Pièce jointe illisible.");
        Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    static void RunIndex(string profile, string dbPath, bool full, CancellationToken ct)
    {
        var last = Stopwatch.StartNew();
        var progress = new Progress<IndexProgress>(p =>
        {
            if (last.ElapsedMilliseconds < 500) return;
            last.Restart();
            Send(new JsonObject
            {
                ["type"] = "progress",
                ["percent"] = p.BytesTotal > 0 ? Math.Round(100.0 * p.BytesDone / p.BytesTotal, 1) : null,
                ["messages"] = p.Messages,
                ["filesDone"] = p.FilesDone,
                ["filesTotal"] = p.FilesTotal,
            });
        });
        try
        {
            var res = Indexer.Run(dbPath, ThunderbirdProfile.LoadAccounts(profile), progress, ct, full);
            Send(new JsonObject
            {
                ["type"] = "indexed",
                ["changed"] = res.FilesChanged,
                ["added"] = res.MessagesAdded,
                ["removed"] = res.MessagesRemoved,
                ["seconds"] = Math.Round(res.Elapsed.TotalSeconds, 1),
            });
        }
        catch (IndexBusyException)
        {
            Send(new JsonObject { ["type"] = "indexed", ["busy"] = true });
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);

    static Stream StdStream(int which, FileAccess access)
    {
        var h = GetStdHandle(which);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) throw new IOException("Poignée standard absente : l'hôte doit être lancé par Thunderbird.");
        return new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(h, ownsHandle: false), access, 1, isAsync: false);
    }

    static JsonObject? ReadMessage(Stream s)
    {
        Span<byte> len = stackalloc byte[4];
        if (!ReadExactly(s, len)) return null;
        int n = BinaryPrimitives.ReadInt32LittleEndian(len);
        if (n <= 0 || n > 64 << 20) return null;
        var buf = new byte[n];
        if (!ReadExactly(s, buf)) return null;
        return JsonNode.Parse(buf) as JsonObject;
    }

    static bool ReadExactly(Stream s, Span<byte> buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int k = s.Read(buf[read..]);
            if (k <= 0) return false;
            read += k;
        }
        return true;
    }

    static void Send(JsonObject o)
    {
        var bytes = Encoding.UTF8.GetBytes(o.ToJsonString());
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, bytes.Length);
        lock (WriteLock)
        {
            stdout.Write(len);
            stdout.Write(bytes);
            stdout.Flush();
        }
    }

    // ---------- Inscription auprès de Thunderbird ----------

    /// <summary>
    /// Déclare l'hôte pour Thunderbird (utilisateur courant) : manifeste JSON + clé de registre.
    /// Refait à chaque lancement de l'application, pour suivre l'exe s'il a été déplacé.
    /// </summary>
    public static string Register(string exePath)
    {
        var dir = Path.GetDirectoryName(IndexDb.DefaultPath)!;
        Directory.CreateDirectory(dir);
        var manifest = Path.Combine(dir, HostName + ".json");
        var json = new JsonObject
        {
            ["name"] = HostName,
            ["description"] = "TB Recherche : moteur de recherche des mails",
            ["path"] = exePath,
            ["type"] = "stdio",
            ["allowed_extensions"] = new JsonArray(ExtensionId),
        };
        File.WriteAllText(manifest, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Mozilla\NativeMessagingHosts\" + HostName);
        key.SetValue(null, manifest);
        return manifest;
    }
}
