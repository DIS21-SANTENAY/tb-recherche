using Microsoft.Data.Sqlite;

namespace TBRecherche.Core;

public sealed record FileRow(long Id, string Path, long Size, long Mtime, string Head, long ResumeOffset, string ResumeMsgId);

/// <summary>Index SQLite : table des fichiers, métadonnées des messages et index plein texte FTS5 sans contenu.</summary>
public sealed class IndexDb : IDisposable
{
    public const int SchemaVersion = 2;
    public SqliteConnection Conn { get; }
    public string FilePath { get; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TBRecherche", "index.db");

    /// <param name="ensureSchema">Créer ou migrer le schéma (nécessite un verrou d'écriture).</param>
    public IndexDb(string path, bool ensureSchema = true) : this(path, ensureSchema, readOnly: false) { }

    IndexDb(string path, bool ensureSchema, bool readOnly)
    {
        FilePath = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        Conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        Conn.Open();
        if (readOnly)
        {
            // En WAL, un lecteur n'attend jamais l'indexation en cours
            Exec("PRAGMA query_only=1; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-32768; PRAGMA busy_timeout=5000;");
            return;
        }
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-65536; PRAGMA busy_timeout=30000;");
        if (ensureSchema) EnsureSchema();
    }

    /// <summary>Connexion de lecture pour la recherche : crée l'index au besoin, puis lecture seule.</summary>
    public static IndexDb OpenRead(string path)
    {
        if (!File.Exists(path)) new IndexDb(path).Dispose();
        return new IndexDb(path, ensureSchema: false, readOnly: true);
    }

    public void Exec(string sql, params (string, object?)[] p)
    {
        using var c = Cmd(sql, p);
        c.ExecuteNonQuery();
    }

    public SqliteCommand Cmd(string sql, params (string, object?)[] p)
    {
        var c = Conn.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in p) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return c;
    }

    public object? Scalar(string sql, params (string, object?)[] p)
    {
        using var c = Cmd(sql, p);
        return c.ExecuteScalar();
    }

    void EnsureSchema()
    {
        Exec("CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT)");
        var ver = Scalar("SELECT v FROM meta WHERE k='schema'") as string;
        if (ver != null && ver != SchemaVersion.ToString())
            Exec("DROP TABLE IF EXISTS fts; DROP TABLE IF EXISTS messages; DROP TABLE IF EXISTS files;");

        Exec("""
            CREATE TABLE IF NOT EXISTS files(
              id INTEGER PRIMARY KEY, path TEXT UNIQUE NOT NULL, account TEXT NOT NULL, folder TEXT NOT NULL,
              maildir INTEGER NOT NULL DEFAULT 0, size INTEGER NOT NULL DEFAULT -1, mtime INTEGER NOT NULL DEFAULT 0,
              head TEXT NOT NULL DEFAULT '', resume_offset INTEGER NOT NULL DEFAULT 0, resume_msgid TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS messages(
              id INTEGER PRIMARY KEY, file_id INTEGER NOT NULL, offset INTEGER NOT NULL, date INTEGER NOT NULL,
              sender TEXT, recipients TEXT, subject TEXT, msgid TEXT, attach INTEGER NOT NULL, attach_names TEXT, snippet TEXT);
            CREATE INDEX IF NOT EXISTS ix_msg_file ON messages(file_id, offset);
            CREATE INDEX IF NOT EXISTS ix_msg_date ON messages(date);
            CREATE VIRTUAL TABLE IF NOT EXISTS fts USING fts5(
              subject, sender, recipients, body, attachments, attachtext,
              content='', contentless_delete=1, tokenize='unicode61 remove_diacritics 2');
            """);
        Exec("INSERT OR REPLACE INTO meta(k,v) VALUES('schema',$v)", ("$v", SchemaVersion.ToString()));
    }

    public Dictionary<string, FileRow> LoadFiles()
    {
        var d = new Dictionary<string, FileRow>(StringComparer.OrdinalIgnoreCase);
        using var c = Cmd("SELECT id,path,size,mtime,head,resume_offset,resume_msgid FROM files");
        using var r = c.ExecuteReader();
        while (r.Read())
            d[r.GetString(1)] = new FileRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3),
                r.GetString(4), r.GetInt64(5), r.GetString(6));
        return d;
    }

    /// <summary>Supprime les messages d'un fichier à partir d'une position (0 = tout).</summary>
    public void DeleteMessages(long fileId, long fromOffset = 0)
    {
        Exec("DELETE FROM fts WHERE rowid IN (SELECT id FROM messages WHERE file_id=$f AND offset>=$o)", ("$f", fileId), ("$o", fromOffset));
        Exec("DELETE FROM messages WHERE file_id=$f AND offset>=$o", ("$f", fileId), ("$o", fromOffset));
    }

    public void DeleteMessageIds(IEnumerable<long> ids)
    {
        using var a = Cmd("DELETE FROM fts WHERE rowid=$id");
        using var b = Cmd("DELETE FROM messages WHERE id=$id");
        var pa = a.Parameters.Add("$id", SqliteType.Integer);
        var pb = b.Parameters.Add("$id", SqliteType.Integer);
        foreach (var id in ids)
        {
            pa.Value = id; a.ExecuteNonQuery();
            pb.Value = id; b.ExecuteNonQuery();
        }
    }

    public void Dispose() => Conn.Dispose();
}
