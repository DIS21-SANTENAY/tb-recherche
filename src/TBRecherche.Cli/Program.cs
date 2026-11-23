using System.Diagnostics;
using System.Text;
using TBRecherche.Core;

Console.OutputEncoding = Encoding.UTF8;
string dbPath = IndexDb.DefaultPath;
string? profile = null;
var rest = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--db") dbPath = args[++i];
    else if (args[i] == "--profile") profile = args[++i];
    else rest.Add(args[i]);
}
profile ??= ThunderbirdProfile.FindDefaultProfile();
var cmd = rest.FirstOrDefault() ?? "help";

switch (cmd)
{
    case "accounts":
        Console.WriteLine($"Profil : {profile}");
        foreach (var a in ThunderbirdProfile.LoadAccounts(profile!))
            Console.WriteLine($"  [{a.Key}] {a.Name} ({a.Type}{(a.Maildir ? ", maildir" : "")}) {(a.Exists ? "" : "ABSENT ")}{a.RootDir}");
        break;

    case "index":
    {
        var accs = ThunderbirdProfile.LoadAccounts(profile!);
        var last = Stopwatch.StartNew();
        var res = Indexer.Run(dbPath, accs, new Progress<IndexProgress>(p =>
        {
            if (last.ElapsedMilliseconds < 2000) return;
            last.Restart();
            Console.WriteLine($"  {p.FilesDone}/{p.FilesTotal} fichiers, {p.BytesDone / 1048576} / {p.BytesTotal / 1048576} Mo, {p.Messages} messages");
        }), full: rest.Contains("--full"));
        Console.WriteLine($"Terminé en {res.Elapsed:mm\\:ss} : {res.FilesSeen} fichiers vus, {res.FilesChanged} traités, {res.FilesRemoved} retirés, +{res.MessagesAdded} / -{res.MessagesRemoved} messages");
        foreach (var w in res.Warnings) Console.WriteLine("  ! " + w);
        using var db = IndexDb.OpenRead(dbPath);
        var (m, f) = Search.Stats(db);
        Console.WriteLine($"Index : {m} messages, {f} fichiers, {new FileInfo(dbPath).Length / 1048576} Mo");
        break;
    }

    case "search":
    {
        using var db = IndexDb.OpenRead(dbPath);
        var q = new SearchQuery { Text = string.Join(" ", rest.Skip(1)), Limit = 20, Sort = SortMode.Pertinence };
        var sw = Stopwatch.StartNew();
        var hits = Search.Run(db, q, out var parsed);
        Console.WriteLine($"FTS : {parsed.Fts}   ({hits.Count} résultats en {sw.ElapsedMilliseconds} ms)");
        foreach (var h in hits)
            Console.WriteLine($"  #{h.Id} {h.DateText} | {Trunc(h.Sender, 35)} | {Trunc(h.Subject, 60)} | {h.Account}/{h.Folder}{(h.InAttachment ? "  [PJ]" : "")}");
        break;
    }

    case "show":
    {
        using var db = IndexDb.OpenRead(dbPath);
        var id = long.Parse(rest[1]);
        using var c = db.Cmd("SELECT f.path, m.offset, f.maildir FROM messages m JOIN files f ON f.id=m.file_id WHERE m.id=$id", ("$id", id));
        using var r = c.ExecuteReader();
        if (!r.Read()) { Console.WriteLine("introuvable"); break; }
        var msg = MessageExtract.Load(r.GetString(0), r.GetInt64(1), r.GetInt32(2) != 0);
        Console.WriteLine($"De : {msg.From}\nObjet : {msg.Subject}\nDate : {msg.Date}\n");
        Console.WriteLine(Trunc(MessageExtract.BodyText(msg, 2000), 1500));
        break;
    }

    case "host":
        NativeHost.Run(profile);
        break;

    case "hosttest":
    {
        // Client de test : parle à l'hôte comme Thunderbird (longueur sur 4 octets + JSON)
        var target = rest.Count > 1 ? rest[1] : Environment.ProcessPath!;
        var psi = new ProcessStartInfo(target) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        if (rest.Count > 1) { psi.ArgumentList.Add(@"C:\x\tbrecherche.json"); psi.ArgumentList.Add(NativeHost.ExtensionId); }
        else psi.ArgumentList.Add("host");
        using var p = Process.Start(psi)!;
        var input = p.StandardInput.BaseStream;
        var output = p.StandardOutput.BaseStream;
        void SendJson(string json)
        {
            var b = Encoding.UTF8.GetBytes(json);
            input.Write(BitConverter.GetBytes(b.Length));
            input.Write(b);
            input.Flush();
        }
        string Recv()
        {
            var l = new byte[4]; output.ReadExactly(l);
            var b = new byte[BitConverter.ToInt32(l)]; output.ReadExactly(b);
            return Encoding.UTF8.GetString(b);
        }
        var sw = Stopwatch.StartNew();
        SendJson("""{"id":1,"cmd":"status"}""");
        Console.WriteLine($"status ({sw.ElapsedMilliseconds} ms) : {Recv()}");
        sw.Restart();
        SendJson("""{"id":2,"cmd":"search","text":"ludivine facture","limit":300}""");
        var r = Recv();
        var j = System.Text.Json.Nodes.JsonNode.Parse(r)!;
        var hits = j["hits"]!.AsArray();
        Console.WriteLine($"search ({sw.ElapsedMilliseconds} ms) : ok={j["ok"]} hits={hits.Count} octets={r.Length} pj={hits.Count(h => h!["inAttachment"]!.GetValue<bool>())} msgid={hits[0]!["msgid"]}");
        SendJson("""{"id":3,"cmd":"accounts"}""");
        Console.WriteLine("accounts : " + Recv());
        input.Close();
        Console.WriteLine(p.WaitForExit(5000) ? $"sortie propre ({p.ExitCode})" : "l'hôte ne s'arrête pas");
        break;
    }

    case "hostdump":
    {
        // Enregistre de vraies réponses de l'hôte dans un fichier JS, pour tester la page de l'extension hors Thunderbird
        var outFile = rest[1];
        var text = string.Join(" ", rest.Skip(2));
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("host");
        using var p = Process.Start(psi)!;
        var input = p.StandardInput.BaseStream; var output = p.StandardOutput.BaseStream;
        string Ask(string json)
        {
            var b = Encoding.UTF8.GetBytes(json);
            input.Write(BitConverter.GetBytes(b.Length)); input.Write(b); input.Flush();
            var l = new byte[4]; output.ReadExactly(l);
            var r = new byte[BitConverter.ToInt32(l)]; output.ReadExactly(r);
            return Encoding.UTF8.GetString(r);
        }
        var search = Ask(System.Text.Json.JsonSerializer.Serialize(new { id = 1, cmd = "search", text, limit = 300 }));
        var sj = System.Text.Json.Nodes.JsonNode.Parse(search)!;
        var first = sj["hits"]!.AsArray().FirstOrDefault(h => h!["inAttachment"]!.GetValue<bool>()) ?? sj["hits"]![0]!;
        var terms = sj["terms"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray();
        var preview = Ask(System.Text.Json.JsonSerializer.Serialize(new { id = 2, cmd = "preview", message = first["id"]!.GetValue<long>(), terms }));
        var accounts = Ask("""{"id":3,"cmd":"accounts"}""");
        var status = Ask("""{"id":4,"cmd":"status"}""");
        File.WriteAllText(outFile, $"window.MOCK = {{ search: {search}, preview: {preview}, accounts: {accounts}, status: {status}, selected: {first["id"]} }};");
        input.Close(); p.WaitForExit(5000);
        Console.WriteLine($"ok : {new FileInfo(outFile).Length / 1024} Ko");
        break;
    }

    case "verify":
    {
        // Compare, fichier par fichier, le nombre de messages indexés au nombre brut de séparateurs "From "
        using var db = IndexDb.OpenRead(dbPath);
        using var c = db.Cmd("SELECT f.id, f.path, f.size, (SELECT count(*) FROM messages m WHERE m.file_id=f.id) FROM files f WHERE f.maildir=0 ORDER BY f.size DESC");
        using var r = c.ExecuteReader();
        long totIdx = 0, totRaw = 0, totDel = 0; int bad = 0;
        while (r.Read())
        {
            var path = r.GetString(1); long size = r.GetInt64(2), n = r.GetInt64(3);
            long raw = 0, del = 0;
            using (var fs = MailStore.OpenShared(path, true))
            using (var sr = new StreamReader(fs, Encoding.Latin1))
            {
                bool prevBlank = true, inHeader = false; string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.StartsWith("From ")) { raw++; inHeader = true; }
                    else if (inHeader && line.StartsWith("X-Mozilla-Status: ") && int.TryParse(line[18..].Trim(), System.Globalization.NumberStyles.HexNumber, null, out var fl) && (fl & 8) != 0) del++;
                    else if (line.Length == 0) inHeader = false;
                    prevBlank = line.Length == 0;
                }
            }
            totIdx += n; totRaw += raw; totDel += del;
            if (n != raw - del) { bad++; Console.WriteLine($"  ECART {n} indexés / {raw} bruts - {del} supprimés  {size / 1048576} Mo  {path}"); }
        }
        Console.WriteLine($"Total : {totIdx} indexés, {totRaw} séparateurs, {totDel} supprimés en attente de compactage, {bad} fichiers en écart");
        break;
    }

    default:
        Console.WriteLine("tbr accounts | index [--full] | search <texte> | show <id>   [--db chemin] [--profile chemin]");
        break;
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
