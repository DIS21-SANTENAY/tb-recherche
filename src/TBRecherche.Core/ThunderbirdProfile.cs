using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TBRecherche.Core;

public sealed record MailAccount(string Key, string Name, string Type, string RootDir, bool Maildir, bool Exists);

/// <summary>Lit profiles.ini et prefs.js pour retrouver le profil et les comptes Thunderbird.</summary>
public static class ThunderbirdProfile
{
    public static string ThunderbirdAppData =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Thunderbird");

    public static string? FindDefaultProfile()
    {
        var ini = Path.Combine(ThunderbirdAppData, "profiles.ini");
        if (!File.Exists(ini)) return null;

        var sections = new List<(string Name, Dictionary<string, string> Values)>();
        Dictionary<string, string>? cur = null;
        foreach (var raw in File.ReadAllLines(ini))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections.Add((line[1..^1], cur));
            }
            else if (cur != null && line.Contains('='))
            {
                var i = line.IndexOf('=');
                cur[line[..i]] = line[(i + 1)..];
            }
        }

        string Resolve(string path, bool relative) =>
            Path.GetFullPath(relative ? Path.Combine(ThunderbirdAppData, path.Replace('/', '\\')) : path.Replace('/', '\\'));

        // 1. Section [Install...] : profil réellement utilisé par l'installation
        foreach (var (name, v) in sections)
            if (name.StartsWith("Install", StringComparison.OrdinalIgnoreCase) && v.TryGetValue("Default", out var d))
            {
                var p = Resolve(d, !Path.IsPathRooted(d));
                if (Directory.Exists(p)) return p;
            }

        // 2. Profil marqué Default=1, sinon le premier
        var profiles = sections.Where(s => s.Name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase)).ToList();
        var chosen = profiles.FirstOrDefault(s => s.Values.GetValueOrDefault("Default") == "1");
        if (chosen.Values == null && profiles.Count > 0) chosen = profiles[0];
        if (chosen.Values == null || !chosen.Values.TryGetValue("Path", out var path)) return null;
        return Resolve(path, chosen.Values.GetValueOrDefault("IsRelative") == "1");
    }

    static readonly Regex PrefLine = new(@"^user_pref\(""([^""]+)"",\s*(.*)\);\s*$", RegexOptions.Compiled);

    public static Dictionary<string, string> ReadPrefs(string profileDir)
    {
        var prefs = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = Path.Combine(profileDir, "prefs.js");
        if (!File.Exists(file)) return prefs;
        foreach (var line in File.ReadLines(file))
        {
            var m = PrefLine.Match(line);
            if (!m.Success) continue;
            var val = m.Groups[2].Value.Trim();
            if (val.Length >= 2 && val[0] == '"' && val[^1] == '"')
                val = Regex.Unescape(val[1..^1]);
            prefs[m.Groups[1].Value] = val;
        }
        return prefs;
    }

    public static List<MailAccount> LoadAccounts(string profileDir)
    {
        var prefs = ReadPrefs(profileDir);

        // Serveurs rattachés à un compte actif (évite les restes de comptes supprimés)
        var active = new HashSet<string>(StringComparer.Ordinal);
        if (prefs.TryGetValue("mail.accountmanager.accounts", out var accList))
            foreach (var acc in accList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (prefs.TryGetValue($"mail.account.{acc}.server", out var srv)) active.Add(srv);

        var servers = prefs.Keys
            .Select(k => Regex.Match(k, @"^mail\.server\.(server\d+)\.type$"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value)
            .Distinct().OrderBy(s => int.Parse(s[6..]));

        var result = new List<MailAccount>();
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in servers)
        {
            if (active.Count > 0 && !active.Contains(s)) continue;
            string P(string k) => prefs.GetValueOrDefault($"mail.server.{s}.{k}") ?? "";
            var type = P("type");
            var host = P("hostname");
            if (type == "none" && host == "smart mailboxes") continue;
            if (type is "rss" or "nntp") continue;

            string? dir = null;
            var rel = P("directory-rel");
            if (rel.StartsWith("[ProfD]"))
                dir = Path.GetFullPath(Path.Combine(profileDir, rel["[ProfD]".Length..].Replace('/', '\\')));
            if (dir == null || !Directory.Exists(dir))
            {
                var abs = P("directory");
                if (abs.Length > 0 && Directory.Exists(abs)) dir = abs;
            }
            dir ??= P("directory");
            if (string.IsNullOrEmpty(dir) || !seenDirs.Add(dir)) continue;

            var name = P("name");
            if (name.Length == 0) name = P("userName").Length > 0 ? $"{P("userName")}@{host}" : host;
            if (type == "none") name = "Dossiers locaux";

            result.Add(new MailAccount(s, name, type, dir,
                P("storeContractID").Contains("maildir", StringComparison.OrdinalIgnoreCase),
                Directory.Exists(dir)));
        }
        return result;
    }

    /// <summary>Chemin de thunderbird.exe tel qu'enregistré pour ouvrir les .eml, sinon emplacements classiques.</summary>
    public static string? FindThunderbirdExe()
    {
        foreach (var key in new[] { @"ThunderbirdEML\shell\open\command" })
        {
            try
            {
                using var k = Registry.ClassesRoot.OpenSubKey(key);
                if (k?.GetValue(null) is string cmd)
                {
                    var m = Regex.Match(cmd, "^\"([^\"]+)\"");
                    if (m.Success && File.Exists(m.Groups[1].Value)) return m.Groups[1].Value;
                }
            }
            catch { }
        }
        foreach (var p in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Thunderbird", "thunderbird.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mozilla Thunderbird", "thunderbird.exe"),
        })
            if (File.Exists(p)) return p;
        return null;
    }
}
