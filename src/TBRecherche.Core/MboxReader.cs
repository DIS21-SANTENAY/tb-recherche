using MimeKit;
using MimeKit.IO;

namespace TBRecherche.Core;

/// <summary>
/// Découpage d'un mbox selon la règle de Thunderbird : toute ligne commençant par "From " ouvre un message
/// (Thunderbird échappe en ">From " celles du corps). Gère aussi le séparateur nu "From " des versions récentes,
/// que MimeKit ne reconnaît pas.
/// </summary>
public static class MboxReader
{
    public readonly record struct Slice(long Start, long BodyStart, long End);

    static ReadOnlySpan<byte> Marker => "\nFrom "u8;

    /// <summary>Liste les messages à partir d'une position qui doit être le début d'une ligne "From ".</summary>
    public static List<Slice> Scan(string path, long from, CancellationToken ct = default)
    {
        var starts = new List<long>();
        using var fs = MailStore.OpenShared(path, sequential: true);
        long len = fs.Length;
        if (from >= len) return new();
        fs.Seek(from, SeekOrigin.Begin);

        const int Chunk = 1 << 20;
        var buf = new byte[Chunk + 8];
        int carry = 0;
        long bufPos = from;   // position fichier de buf[0]

        // Le point de départ est un début de message s'il commence par "From "
        var head = new byte[5];
        int hn = fs.Read(head, 0, 5);
        if (hn == 5 && head.AsSpan().SequenceEqual("From "u8)) starts.Add(from);
        fs.Seek(from, SeekOrigin.Begin);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int n = fs.Read(buf, carry, Chunk);
            if (n <= 0) break;
            int total = carry + n;
            var span = buf.AsSpan(0, total);
            int i = 0;
            while (true)
            {
                int k = span[i..].IndexOf(Marker);
                if (k < 0) break;
                long pos = bufPos + i + k + 1;
                if (pos > from) starts.Add(pos);
                i += k + 1;
            }
            // Garder la fin pour un marqueur à cheval sur deux blocs
            int keep = Math.Min(Marker.Length - 1, total);
            Buffer.BlockCopy(buf, total - keep, buf, 0, keep);
            bufPos += total - keep;
            carry = keep;
        }

        var slices = new List<Slice>(starts.Count);
        for (int s = 0; s < starts.Count; s++)
        {
            long end = s + 1 < starts.Count ? starts[s + 1] : len;
            slices.Add(new Slice(starts[s], LineEnd(fs, starts[s], end), end));
        }
        return slices;
    }

    static long LineEnd(FileStream fs, long start, long limit)
    {
        fs.Seek(start, SeekOrigin.Begin);
        Span<byte> b = stackalloc byte[512];
        int n = fs.Read(b);
        int k = b[..n].IndexOf((byte)'\n');
        return Math.Min(limit, k < 0 ? start + n : start + k + 1);
    }

    /// <summary>Bornes du message qui commence à <paramref name="start"/> (fin = prochain "\nFrom " ou fin de fichier).</summary>
    public static Slice SliceAt(FileStream fs, long start)
    {
        long len = fs.Length;
        long bodyStart = LineEnd(fs, start, len);
        fs.Seek(bodyStart, SeekOrigin.Begin);
        var buf = new byte[1 << 16];
        long bufPos = bodyStart - 1; // inclut le '\n' de fin de ligne "From "
        fs.Seek(bufPos, SeekOrigin.Begin);
        int carry = 0;
        while (true)
        {
            int n = fs.Read(buf, carry, buf.Length - carry);
            if (n <= 0) return new Slice(start, bodyStart, len);
            int total = carry + n;
            int k = buf.AsSpan(0, total).IndexOf(Marker);
            if (k >= 0) return new Slice(start, bodyStart, bufPos + k + 1);
            int keep = Math.Min(Marker.Length - 1, total);
            Buffer.BlockCopy(buf, total - keep, buf, 0, keep);
            bufPos += total - keep;
            carry = keep;
        }
    }

    public static MimeMessage Parse(FileStream fs, Slice s, bool persistent, CancellationToken ct = default)
    {
        var bound = new BoundStream(fs, s.BodyStart, s.End, leaveOpen: true);
        var parser = new MimeParser(bound, MimeFormat.Entity, persistent);
        return parser.ParseMessage(ct);
    }

    public static MimeMessage LoadAt(string path, long start)
    {
        using var fs = MailStore.OpenShared(path);
        var s = SliceAt(fs, start);
        return Parse(fs, s, persistent: false);
    }
}
