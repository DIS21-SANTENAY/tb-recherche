using System.Globalization;
using System.Windows;
using System.Windows.Media;
using TBRecherche.Core;

namespace TBRecherche;

/// <summary>Couleurs et formats partagés par la liste et le volet de lecture.</summary>
public static class Look
{
    static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    // Teintes douces pour les avatars et pastilles de comptes
    static readonly Color[] Palette =
    {
        Color.FromRgb(0xB3, 0x26, 0x4A), Color.FromRgb(0x2F, 0x6F, 0xA8), Color.FromRgb(0x3C, 0x8D, 0x6B),
        Color.FromRgb(0x4E, 0x7D, 0x3A), Color.FromRgb(0x7A, 0x4F, 0xB8), Color.FromRgb(0x1F, 0x8A, 0x92),
        Color.FromRgb(0x2E, 0x5F, 0x78), Color.FromRgb(0x55, 0x60, 0x7A), Color.FromRgb(0x9A, 0x3B, 0x87),
    };

    static readonly Dictionary<string, SolidColorBrush> Cache = new();

    public static SolidColorBrush ColorFor(string key)
    {
        key = (key ?? "").ToLowerInvariant();
        if (Cache.TryGetValue(key, out var b)) return b;
        uint h = 2166136261;
        foreach (var ch in key) h = (h ^ ch) * 16777619;
        b = new SolidColorBrush(Palette[h % (uint)Palette.Length]);
        b.Freeze();
        return Cache[key] = b;
    }

    public static (string Name, string Address) SplitSender(string sender)
    {
        var first = sender.Split(", ")[0].Trim();
        int lt = first.LastIndexOf('<');
        if (lt > 0 && first.EndsWith('>'))
            return (first[..lt].Trim().Trim('"', '\''), first[(lt + 1)..^1]);
        return (first, first);
    }

    public static string Initials(string name)
    {
        var parts = name.Split(new[] { ' ', '.', '-', '_', '@' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => char.IsLetterOrDigit(p[0])).ToArray();
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant();
        return (parts[0][0].ToString() + parts[1][0]).ToUpperInvariant();
    }

    public static string SmartDate(DateTime d)
    {
        if (d == DateTime.MinValue) return "";
        var today = DateTime.Today;
        if (d.Date > today) return d.ToString("dd/MM/yyyy", Fr); // date future : jamais un simple jour de semaine
        if (d.Date == today) return d.ToString("HH:mm", Fr);
        if (d.Date == today.AddDays(-1)) return "Hier";
        if (d.Date > today.AddDays(-7)) return Fr.TextInfo.ToTitleCase(d.ToString("dddd", Fr));
        if (d.Year == today.Year) return d.ToString("d MMM", Fr);
        return d.ToString("dd/MM/yyyy", Fr);
    }

    public static string LongDate(DateTime d) =>
        d == DateTime.MinValue ? "" : d.ToString("dddd d MMMM yyyy 'à' HH:mm", Fr);

    public static string FileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} o",
        < 1024 * 1024 => $"{bytes / 1024.0:0} Ko",
        _ => $"{bytes / 1048576.0:0.0} Mo",
    };

    /// <summary>Pastille de type de fichier : (texte, couleur).</summary>
    public static (string Label, SolidColorBrush Brush) FileBadge(string name)
    {
        var ext = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        Color c = ext switch
        {
            "pdf" => Color.FromRgb(0xD3, 0x3A, 0x2C),
            "doc" or "docx" or "odt" or "rtf" => Color.FromRgb(0x2B, 0x5C, 0xB8),
            "xls" or "xlsx" or "ods" or "csv" => Color.FromRgb(0x1E, 0x7D, 0x4A),
            "ppt" or "pptx" or "odp" => Color.FromRgb(0xA8, 0x32, 0x6E),
            "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "heic" or "tif" or "tiff" => Color.FromRgb(0x7A, 0x4F, 0xB8),
            "zip" or "rar" or "7z" => Color.FromRgb(0x6B, 0x5B, 0x3E),
            "eml" or "msg" => Color.FromRgb(0x1F, 0x8A, 0x92),
            _ => Color.FromRgb(0x6E, 0x6A, 0x66),
        };
        var b = new SolidColorBrush(c);
        b.Freeze();
        var label = ext.Length == 0 ? "?" : ext.Length > 4 ? ext[..4] : ext;
        return (label.ToUpperInvariant(), b);
    }
}

public sealed class ResultItem
{
    public SearchHit Hit { get; }
    public string SenderName { get; }
    public string Initials { get; }
    public Brush AvatarBrush { get; }
    public string SmartDate { get; }
    public string Subject { get; }
    public string Snippet { get; }
    public Visibility AttachVisibility { get; }
    public Visibility InAttachmentVisibility { get; }
    public string Account => Hit.Account;
    public Brush AccountBrush { get; }
    public string FolderShort { get; }

    public ResultItem(SearchHit h)
    {
        Hit = h;
        var (name, addr) = Look.SplitSender(h.Sender);
        SenderName = string.IsNullOrWhiteSpace(name) ? (string.IsNullOrWhiteSpace(addr) ? "(expéditeur inconnu)" : addr) : name;
        Initials = Look.Initials(SenderName);
        AvatarBrush = Look.ColorFor(addr.Length > 0 ? addr : SenderName);
        SmartDate = Look.SmartDate(h.Date);
        Subject = string.IsNullOrWhiteSpace(h.Subject) ? "(sans objet)" : h.Subject;
        Snippet = System.Text.RegularExpressions.Regex.Replace(h.Snippet, @"[-_=*~#.]{4,}", " ").Trim();
        AttachVisibility = h.Attach > 0 ? Visibility.Visible : Visibility.Collapsed;
        InAttachmentVisibility = h.InAttachment ? Visibility.Visible : Visibility.Collapsed;
        AccountBrush = Look.ColorFor("compte:" + h.Account);
        var parts = h.Folder.Split('/');
        FolderShort = parts.Length > 2 ? "… / " + string.Join(" / ", parts[^2..]) : string.Join(" / ", parts);
    }
}

public sealed class AccountItem
{
    public string? Name { get; init; }          // null = tous les comptes
    public string Label { get; init; } = "";
    public string CountText { get; init; } = "";
    public Brush DotBrush { get; init; } = Brushes.Transparent;
}

public sealed class PeriodItem
{
    public string Label { get; init; } = "";
    public Func<DateTime?>? From { get; init; }
    public bool Custom { get; init; }
}

public sealed class AttachmentChip
{
    public string Name { get; init; } = "";
    public string Size { get; init; } = "";
    public string Badge { get; init; } = "";
    public Brush BadgeBrush { get; init; } = Brushes.Gray;
    public int Index { get; init; }
    public long MessageId { get; init; }
    public bool Matches { get; set; }
    public Thickness Ring => Matches ? new Thickness(2) : new Thickness(1);
    public Brush RingBrush => Matches ? (Brush)Application.Current.FindResource("Accent") : (Brush)Application.Current.FindResource("Border");
    public Visibility MatchVisibility => Matches ? Visibility.Visible : Visibility.Collapsed;
}
