using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace TBRecherche.Core;

public sealed record IndexProgress(string Phase, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, long Messages);

public sealed record IndexResult(int FilesSeen, int FilesChanged, int FilesRemoved, long MessagesAdded,
    long MessagesRemoved, TimeSpan Elapsed, List<string> Warnings);

/// <summary>
/// Indexation incrémentale :
/// - fichier inchangé (taille + date) : ignoré ;
/// - mbox qui a grossi et dont le début n'a pas bougé : on reprend au dernier message indexé ;
/// - date changée : on relit les drapeaux X-Mozilla-Status pour retirer les messages supprimés ;
/// - sinon (compactage, fichier remplacé) : réindexation complète du fichier.
/// </summary>
public sealed class IndexBusyException() : InvalidOperationException("Indexation déjà en cours (application ou extension Thunderbird).");

public static class Indexer
{
    sealed class Job
    {
        public required StoreFile File;
        public long FileId, Size, Mtime;
        public string Head = "";
        public long StartOffset;                 // -1 = pas de lecture des messages
        public List<(long Id, long Offset)>? FlagScan;
    }

    sealed record MsgItem(long FileId, MessageRecord Rec);
    sealed record ExpungedItem(List<long> Ids);
    sealed record DoneItem(long FileId, long Size, long Mtime, string Head, long? ResumeOffset, string? ResumeSig);

    static readonly object Lock = new();

    public static IndexResult Run(string dbPath, IList<MailAccount> accounts, IProgress<IndexProgress>? progress = null,
        CancellationToken ct = default, bool full = false)
    {
        // Une seule indexation à la fois, y compris entre l'application et l'extension Thunderbird
        if (!Monitor.TryEnter(Lock)) throw new IndexBusyException();
        FileStream? lockFile = null;
        try
        {
            try
            {
                lockFile = new FileStream(dbPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.DeleteOnClose);
            }
            catch (IOException) { throw new IndexBusyException(); }
            return RunCore(dbPath, accounts, progress, ct, full);
        }
        finally
        {
            lockFile?.Dispose();
            Monitor.Exit(Lock);
        }
    }

    static IndexResult RunCore(string dbPath, IList<MailAccount> accounts, IProgress<IndexProgress>? progress,
        CancellationToken ct, bool full)
    {
        MessageExtract.Init();
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        using var db = new IndexDb(dbPath);

        progress?.Report(new IndexProgress("Inventaire des dossiers", 0, 0, 0, 0, 0));
        var files = new List<StoreFile>();
        foreach (var acc in accounts)
        {
            if (!acc.Exists) { warnings.Add($"Compte {acc.Name} : dossier absent ({acc.RootDir}), pas de copie locale."); continue; }
            files.AddRange(MailStore.Enumerate(acc));
        }

        var known = db.LoadFiles();
        var jobs = new List<Job>();
        int removed = 0;
        long msgRemoved = 0;

        using (var tx = db.Conn.BeginTransaction())
        {
            if (full)
            {
                db.Exec("DELETE FROM fts; DELETE FROM messages; DELETE FROM files;");
                known.Clear();
            }

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sf in files)
            {
                present.Add(sf.Path);
                FileInfo fi;
                try { fi = new FileInfo(sf.Path); _ = fi.Length; }
                catch { continue; }
                long size = fi.Length, mtime = fi.LastWriteTimeUtc.Ticks;

                known.TryGetValue(sf.Path, out var row);
                if (row == null)
                {
                    var id = (long)db.Scalar("INSERT INTO files(path,account,folder,maildir) VALUES($p,$a,$f,$m) RETURNING id",
                        ("$p", sf.Path), ("$a", sf.Account), ("$f", sf.Folder), ("$m", sf.IsMaildirMessage ? 1 : 0))!;
                    jobs.Add(new Job { File = sf, FileId = id, Size = size, Mtime = mtime, StartOffset = 0 });
                    continue;
                }

                db.Exec("UPDATE files SET account=$a, folder=$f WHERE id=$id", ("$a", sf.Account), ("$f", sf.Folder), ("$id", row.Id));
                if (row.Size == size && row.Mtime == mtime) continue;

                var job = new Job { File = sf, FileId = row.Id, Size = size, Mtime = mtime };
                if (sf.IsMaildirMessage)
                {
                    msgRemoved += CountFrom(db, row.Id, 0);
                    db.DeleteMessages(row.Id);
                    job.StartOffset = 0;
                }
                else if (size == row.Size)
                {
                    // Seuls des drapeaux ont changé (lu, supprimé...) : écriture sur place par Thunderbird
                    job.StartOffset = -1;
                    job.FlagScan = LoadOffsets(db, row.Id, long.MaxValue);
                }
                else if (size > row.Size && row.ResumeOffset > 0 && ReadHead(sf.Path) == row.Head
                         && SignatureAt(sf.Path, row.ResumeOffset) == row.ResumeMsgId)
                {
                    msgRemoved += CountFrom(db, row.Id, row.ResumeOffset);
                    db.DeleteMessages(row.Id, row.ResumeOffset);
                    job.StartOffset = row.ResumeOffset;
                    job.FlagScan = LoadOffsets(db, row.Id, row.ResumeOffset);
                }
                else
                {
                    msgRemoved += CountFrom(db, row.Id, 0);
                    db.DeleteMessages(row.Id);
                    job.StartOffset = 0;
                }
                jobs.Add(job);
            }

            foreach (var row in known.Values.Where(r => !present.Contains(r.Path)))
            {
                msgRemoved += CountFrom(db, row.Id, 0);
                db.DeleteMessages(row.Id);
                db.Exec("DELETE FROM files WHERE id=$id", ("$id", row.Id));
                removed++;
            }
            tx.Commit();
        }

        long bytesTotal = jobs.Where(j => j.StartOffset >= 0).Sum(j => j.Size - j.StartOffset);
        long bytesDone = 0, msgCount = 0;
        int filesDone = 0;
        var queue = new BlockingCollection<object>(4000);
        long added = 0, expunged = 0;

        var writer = Task.Run(() =>
        {
            using var wdb = new IndexDb(dbPath, ensureSchema: false);
            var tx = wdb.Conn.BeginTransaction();
            using var insMsg = wdb.Cmd("""
                INSERT INTO messages(file_id,offset,date,sender,recipients,subject,msgid,attach,attach_names,snippet)
                VALUES($f,$o,$d,$s,$r,$sub,$mid,$a,$an,$sn) RETURNING id
                """);
            using var insFts = wdb.Cmd("INSERT INTO fts(rowid,subject,sender,recipients,body,attachments,attachtext) VALUES($id,$sub,$s,$r,$b,$an,$at)");
            using var upd = wdb.Cmd("UPDATE files SET size=$sz, mtime=$mt, head=$h, resume_offset=COALESCE($ro,resume_offset), resume_msgid=COALESCE($rs,resume_msgid) WHERE id=$id");
            foreach (var c in new[] { insMsg, insFts, upd }) c.Transaction = tx;
            int ops = 0;
            var sinceCommit = Stopwatch.StartNew();

            void Set(SqliteCommand c, string k, object? v)
            {
                if (c.Parameters.Contains(k)) c.Parameters[k].Value = v ?? DBNull.Value;
                else c.Parameters.AddWithValue(k, v ?? DBNull.Value);
            }

            foreach (var item in queue.GetConsumingEnumerable())
            {
                switch (item)
                {
                    case MsgItem m:
                        var r = m.Rec;
                        Set(insMsg, "$f", m.FileId); Set(insMsg, "$o", r.Offset); Set(insMsg, "$d", r.Date);
                        Set(insMsg, "$s", r.Sender); Set(insMsg, "$r", r.Recipients); Set(insMsg, "$sub", r.Subject);
                        Set(insMsg, "$mid", r.MessageId); Set(insMsg, "$a", r.AttachCount); Set(insMsg, "$an", r.AttachNames); Set(insMsg, "$sn", r.Snippet);
                        var id = (long)insMsg.ExecuteScalar()!;
                        Set(insFts, "$id", id); Set(insFts, "$sub", r.Subject); Set(insFts, "$s", r.Sender);
                        Set(insFts, "$r", r.Recipients); Set(insFts, "$b", r.Body); Set(insFts, "$an", r.AttachNames); Set(insFts, "$at", r.AttachText);
                        insFts.ExecuteNonQuery();
                        added++;
                        break;
                    case ExpungedItem e:
                        foreach (var x in e.Ids)
                        {
                            using var d1 = wdb.Cmd("DELETE FROM fts WHERE rowid=$id", ("$id", x)); d1.Transaction = tx; d1.ExecuteNonQuery();
                            using var d2 = wdb.Cmd("DELETE FROM messages WHERE id=$id", ("$id", x)); d2.Transaction = tx; d2.ExecuteNonQuery();
                        }
                        expunged += e.Ids.Count;
                        break;
                    case DoneItem d:
                        Set(upd, "$sz", d.Size); Set(upd, "$mt", d.Mtime); Set(upd, "$h", d.Head);
                        Set(upd, "$ro", d.ResumeOffset); Set(upd, "$rs", d.ResumeSig); Set(upd, "$id", d.FileId);
                        upd.ExecuteNonQuery();
                        break;
                }
                // Transactions courtes : la recherche voit les nouveaux messages au fil de l'eau
                if (++ops >= 5000 || sinceCommit.ElapsedMilliseconds > 1000)
                {
                    tx.Commit();
                    tx = wdb.Conn.BeginTransaction();
                    foreach (var c in new[] { insMsg, insFts, upd }) c.Transaction = tx;
                    ops = 0;
                    sinceCommit.Restart();
                }
            }
            tx.Commit();
            wdb.Exec("PRAGMA wal_checkpoint(TRUNCATE)");
        });

        void Report() => progress?.Report(new IndexProgress("Indexation", filesDone, jobs.Count,
            Interlocked.Read(ref bytesDone), bytesTotal, Interlocked.Read(ref msgCount)));

        try
        {
            // Gros fichiers d'abord pour équilibrer les threads
            var ordered = jobs.OrderByDescending(j => j.Size - Math.Max(0, j.StartOffset)).ToList();
            var opts = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 2, 8), CancellationToken = ct };
            var lastReport = Stopwatch.StartNew();
            Parallel.ForEach(ordered, opts, job =>
            {
                try
                {
                    ProcessJob(job, queue, ct, (bytes, msgs) =>
                    {
                        Interlocked.Add(ref bytesDone, bytes);
                        Interlocked.Add(ref msgCount, msgs);
                        if (lastReport.ElapsedMilliseconds > 300) { lastReport.Restart(); Report(); }
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lock (warnings) warnings.Add($"{job.File.Account} / {job.File.Folder} : {ex.Message}");
                }
                Interlocked.Increment(ref filesDone);
            });
        }
        finally
        {
            queue.CompleteAdding();
            writer.Wait();
        }
        Report();

        return new IndexResult(files.Count, jobs.Count, removed, added, msgRemoved + expunged, sw.Elapsed, warnings);
    }

    static void ProcessJob(Job job, BlockingCollection<object> queue, CancellationToken ct, Action<long, long> tick)
    {
        var path = job.File.Path;

        if (job.FlagScan is { Count: > 0 })
        {
            var gone = ScanExpunged(path, job.FlagScan);
            if (gone.Count > 0) queue.Add(new ExpungedItem(gone), ct);
        }

        long? resumeOffset = null;
        string? resumeSig = null;
        if (job.StartOffset >= 0 && job.File.IsMaildirMessage)
        {
            using var fs = MailStore.OpenShared(path, sequential: true);
            var msg = new MimeParser(fs, MimeFormat.Entity, persistent: true).ParseMessage(ct);
            if (!MessageExtract.IsExpunged(msg))
                queue.Add(new MsgItem(job.FileId, MessageExtract.ToRecord(msg, 0, ct: ct)), ct);
            tick(job.Size, 1);
        }
        else if (job.StartOffset >= 0)
        {
            var slices = MboxReader.Scan(path, job.StartOffset, ct);
            // Les gros dossiers sont découpés entre plusieurs threads (extraction des PJ coûteuse en CPU)
            Parallel.ForEach(slices,
                new ParallelOptions { MaxDegreeOfParallelism = slices.Count > 200 ? 4 : 1, CancellationToken = ct },
                () => MailStore.OpenShared(path),
                (s, _, fs) =>
                {
                    try
                    {
                        var msg = MboxReader.Parse(fs, s, persistent: true, ct);
                        if (!MessageExtract.IsExpunged(msg))
                            queue.Add(new MsgItem(job.FileId, MessageExtract.ToRecord(msg, s.Start, ct: ct)), ct);
                    }
                    catch (FormatException) { }
                    tick(s.End - s.Start, 1);
                    return fs;
                },
                fs => fs.Dispose());
            if (slices.Count > 0)
            {
                resumeOffset = slices[^1].Start;
                resumeSig = SignatureAt(path, slices[^1].Start);
            }
        }
        queue.Add(new DoneItem(job.FileId, job.Size, job.Mtime, ReadHead(path),
            resumeOffset, resumeSig), ct);
    }

    static long CountFrom(IndexDb db, long fileId, long offset) =>
        (long)db.Scalar("SELECT count(*) FROM messages WHERE file_id=$f AND offset>=$o", ("$f", fileId), ("$o", offset))!;

    static List<(long, long)> LoadOffsets(IndexDb db, long fileId, long before)
    {
        var l = new List<(long, long)>();
        using var c = db.Cmd("SELECT id, offset FROM messages WHERE file_id=$f AND offset<$o", ("$f", fileId), ("$o", before));
        using var r = c.ExecuteReader();
        while (r.Read()) l.Add((r.GetInt64(0), r.GetInt64(1)));
        return l;
    }

    /// <summary>Signature du premier message : change si le fichier a été compacté ou remplacé.</summary>
    public static string ReadHead(string path) => SignatureAt(path, 0);
    static string Signature(MimeMessage m) => $"{m.MessageId}|{m.Date.UtcTicks}|{m.Subject}";

    static string SignatureAt(string path, long offset)
    {
        try { return Signature(MboxReader.LoadAt(path, offset)); }
        catch { return "?"; }
    }
    static readonly byte[] StatusTag = Encoding.ASCII.GetBytes("\nX-Mozilla-Status: ");

    /// <summary>Lit uniquement le début de chaque message pour repérer le drapeau "supprimé".</summary>
    static List<long> ScanExpunged(string path, List<(long Id, long Offset)> items)
    {
        var gone = new List<long>();
        using var fs = MailStore.OpenShared(path);
        var buf = new byte[2048];
        foreach (var (id, off) in items.OrderBy(i => i.Offset))
        {
            if (off >= fs.Length) { gone.Add(id); continue; }
            fs.Seek(off, SeekOrigin.Begin);
            int n = fs.Read(buf, 0, buf.Length);
            if (n < 5 || !buf.AsSpan(0, 5).SequenceEqual("From "u8)) continue; // structure inattendue : on ne touche pas
            int i = buf.AsSpan(0, n).IndexOf(StatusTag);
            if (i < 0 || i + StatusTag.Length + 4 > n) continue;
            var hex = Encoding.ASCII.GetString(buf, i + StatusTag.Length, 4);
            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var f) && (f & 0x0008) != 0)
                gone.Add(id);
        }
        return gone;
    }
}
