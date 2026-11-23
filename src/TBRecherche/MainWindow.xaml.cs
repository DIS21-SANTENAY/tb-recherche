using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using MimeKit;
using TBRecherche.Core;

namespace TBRecherche;

public partial class MainWindow : Window
{
    readonly string dbPath;
    readonly string? profileDir;
    readonly string? thunderbirdExe = ThunderbirdProfile.FindThunderbirdExe();
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(220) };
    readonly DispatcherTimer autoIndex = new() { Interval = TimeSpan.FromMinutes(5) };
    readonly DispatcherTimer previewDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    CancellationTokenSource? searchCts;
    readonly string tempDir = Path.Combine(Path.GetTempPath(), "TBRecherche");
    readonly HashSet<long> remoteAllowed = new();

    CancellationTokenSource? indexCts;
    bool indexing, loading = true, webReady;
    int searchSeq, previewSeq, renderCount;
    SearchHit? current;
    MimeMessage? currentMsg;
    List<string> highlight = new();
    string? currentViewFile;

    public MainWindow(string? profileOverride, string? dbOverride = null)
    {
        InitializeComponent();
        dbPath = dbOverride != null ? Path.GetFullPath(dbOverride) : IndexDb.DefaultPath;
        profileDir = profileOverride ?? ThunderbirdProfile.FindDefaultProfile();

        PeriodList.ItemsSource = new List<PeriodItem>
        {
            new() { Label = "Toute la période" },
            new() { Label = "Aujourd'hui", From = () => DateTime.Today },
            new() { Label = "7 derniers jours", From = () => DateTime.Today.AddDays(-6) },
            new() { Label = "30 derniers jours", From = () => DateTime.Today.AddDays(-29) },
            new() { Label = "12 derniers mois", From = () => DateTime.Today.AddMonths(-12) },
            new() { Label = "Dates précises...", Custom = true },
        };
        PeriodList.SelectedIndex = 0;
        BuildHelp();

        debounce.Tick += (_, _) => { debounce.Stop(); _ = RunSearch(); };
        previewDelay.Tick += (_, _) => LoadSelected();
        autoIndex.Tick += (_, _) => { if (!indexing) _ = RunIndex(full: false, quiet: true); };
        Loaded += OnLoaded;
        Closing += (_, _) => indexCts?.Cancel();
        StateChanged += (_, _) => SyncWindowState();
        PreviewKeyDown += Window_PreviewKeyDown;
    }

    async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (profileDir == null || !Directory.Exists(profileDir))
            ShowIndexState("Profil introuvable", "Lancez avec --profile \"chemin du profil\"", error: true);

        _ = InitWebView();
        RefreshAccounts();
        loading = false;
        await RunSearch();
        SearchBox.Focus();

        if (profileDir != null && Directory.Exists(profileDir))
        {
            await RunIndex(full: false, quiet: true);
            autoIndex.Start();
        }
    }

    // ---------- Fenêtre ----------

    void SyncWindowState()
    {
        // Une fenêtre sans bordure maximisée déborde de l'écran : on compense
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object s, RoutedEventArgs e) => Close();

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    // ---------- Aide ----------

    void BuildHelp()
    {
        var rows = new (string, string)[]
        {
            ("facture dupont", "les deux mots"),
            ("\"bon de commande\"", "l'expression exacte"),
            ("devis OU facture", "l'un ou l'autre"),
            ("facture -relance", "sans le mot « relance »"),
            ("de:dupont", "expéditeur"),
            ("a:martin", "destinataires (À et Cc)"),
            ("objet:commande", "dans l'objet"),
            ("corps:palette", "dans le texte du mail"),
            ("pj:pdf", "nom d'une pièce jointe"),
            ("contenu:siret", "dans le texte des pièces jointes"),
            ("dossier:douanes", "dans un dossier précis"),
        };
        for (int i = 0; i < rows.Length; i++)
        {
            HelpGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var code = new Border
            {
                Background = (Brush)FindResource("Hover"),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 3),
                Margin = new Thickness(0, 3, 10, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = rows[i].Item1, FontFamily = new FontFamily("Consolas"), FontSize = 12.5 },
            };
            var desc = new TextBlock { Text = rows[i].Item2, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Ink2") };
            Grid.SetRow(code, i);
            Grid.SetRow(desc, i);
            Grid.SetColumn(desc, 1);
            HelpGrid.Children.Add(code);
            HelpGrid.Children.Add(desc);
        }
    }

    void Help_Click(object sender, RoutedEventArgs e) => HelpPopup.IsOpen = !HelpPopup.IsOpen;

    // ---------- Comptes ----------

    void RefreshAccounts()
    {
        var sel = (AccountList.SelectedItem as AccountItem)?.Name;
        var items = new List<AccountItem>();
        try
        {
            using var db = IndexDb.OpenRead(dbPath);
            var counts = Search.AccountCounts(db);
            items.Add(new AccountItem
            {
                Name = null,
                Label = "Tous les comptes",
                CountText = counts.Sum(c => c.Count).ToString("N0"),
                DotBrush = (Brush)FindResource("SideInk"),
            });
            items.AddRange(counts.Select(c => new AccountItem
            {
                Name = c.Name,
                Label = c.Name,
                CountText = c.Count.ToString("N0"),
                DotBrush = Look.ColorFor("compte:" + c.Name),
            }));
        }
        catch
        {
            items.Add(new AccountItem { Label = "Tous les comptes", DotBrush = (Brush)FindResource("SideInk") });
        }

        var was = loading;
        loading = true;
        AccountList.ItemsSource = items;
        AccountList.SelectedItem = items.FirstOrDefault(i => i.Name == sel) ?? items[0];
        loading = was;
    }

    // ---------- Recherche ----------

    SearchQuery BuildQuery()
    {
        var period = PeriodList.SelectedItem as PeriodItem;
        var q = new SearchQuery
        {
            Text = SearchBox.Text,
            Account = (AccountList.SelectedItem as AccountItem)?.Name,
            AttachOnly = AttachOnly.IsChecked == true,
            Sort = SortRelevance.IsChecked == true ? SortMode.Pertinence : SortOld.IsChecked == true ? SortMode.DateAsc : SortMode.DateDesc,
            Limit = 500,
        };
        if (period?.Custom == true) { q.From = FromDate.SelectedDate; q.To = ToDate.SelectedDate; }
        else q.From = period?.From?.Invoke();
        return q;
    }

    async Task RunSearch()
    {
        if (loading) return;
        var q = BuildQuery();
        var seq = ++searchSeq;
        var keepId = current?.Id;
        var sw = Stopwatch.StartNew();

        // Une nouvelle frappe annule la recherche en cours au lieu d'attendre derrière elle
        searchCts?.Cancel();
        var cts = searchCts = new CancellationTokenSource();
        var ct = cts.Token;
        // Indicateur affiché seulement si la recherche dépasse 100 ms (pas de clignotement).
        // "finished" évite qu'il s'affiche après coup quand la recherche a été plus rapide.
        var finished = false;
        _ = Task.Delay(100).ContinueWith(_ =>
        {
            if (finished || seq != searchSeq || ct.IsCancellationRequested) return;
            SearchProgress.Visibility = Visibility.Visible;
            ResultInfo.Text = "Recherche en cours…";
        }, TaskScheduler.FromCurrentSynchronizationContext());
        try
        {
            var (hits, parsed) = await Task.Run(() =>
            {
                using var db = IndexDb.OpenRead(dbPath);
                var h = Search.Run(db, q, out var p, ct);
                return (h.Select(x => new ResultItem(x)).ToList(), p);
            }, ct);
            if (seq != searchSeq || ct.IsCancellationRequested) return;
            SearchProgress.Visibility = Visibility.Hidden;

            highlight = parsed.HighlightTerms;
            Results.ItemsSource = hits;
            var n = hits.Count;
            ResultCount.Text = n == 0 ? "Aucun résultat" : n >= q.Limit ? $"{q.Limit:N0}+ messages" : $"{n:N0} message{(n > 1 ? "s" : "")}";
            var inPj = hits.Count(h => h.Hit.InAttachment);
            ResultInfo.Text = $"en {sw.ElapsedMilliseconds} ms" + (inPj > 0 ? $"  ·  {inPj} trouvé{(inPj > 1 ? "s" : "")} dans des pièces jointes" : "")
                              + (n >= q.Limit ? "  ·  affinez la recherche" : "");
            EmptyResults.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;

            var again = keepId == null ? null : hits.FirstOrDefault(h => h.Hit.Id == keepId);
            if (again != null) { Results.SelectedItem = again; ShowCurrentAgain(); }
            else if (n > 0) Results.SelectedIndex = 0;
            else ClearReader();
            if (Results.SelectedItem != null) Results.ScrollIntoView(Results.SelectedItem);
        }
        catch (OperationCanceledException) { }
        catch (SqliteException) when (ct.IsCancellationRequested) { }
        catch (SqliteException)
        {
            if (seq == searchSeq) SearchProgress.Visibility = Visibility.Hidden;
            if (seq == searchSeq) { ResultCount.Text = "Recherche incomplète"; ResultInfo.Text = "Vérifiez les guillemets et les opérateurs."; }
        }
        catch (Exception ex)
        {
            if (seq == searchSeq) SearchProgress.Visibility = Visibility.Hidden;
            if (seq == searchSeq) { ResultCount.Text = "Erreur"; ResultInfo.Text = ex.Message; }
        }
        finally { finished = true; }
    }

    /// <summary>Même message après une nouvelle recherche : on réapplique seulement le surlignage.</summary>
    void ShowCurrentAgain()
    {
        if (current != null && currentMsg != null) _ = RenderBody(current, currentMsg, previewSeq);
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchChrome();
        debounce.Stop();
        debounce.Start();
    }

    void SearchBox_Focus(object sender, RoutedEventArgs e) => UpdateSearchChrome();

    void UpdateSearchChrome()
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchShell.BorderBrush = (Brush)FindResource(SearchBox.IsKeyboardFocused ? "Accent" : "Border");
    }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                debounce.Stop();
                _ = RunSearch();
                e.Handled = true;
                break;
            case Key.Escape:
                SearchBox.Clear();
                e.Handled = true;
                break;
            case Key.Down or Key.Up when Results.Items.Count > 0:
                var i = Results.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                Results.SelectedIndex = Math.Clamp(i, 0, Results.Items.Count - 1);
                Results.ScrollIntoView(Results.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => _ = RunSearch();

    void Period_Changed(object sender, SelectionChangedEventArgs e)
    {
        CustomDates.Visibility = (PeriodList.SelectedItem as PeriodItem)?.Custom == true ? Visibility.Visible : Visibility.Collapsed;
        _ = RunSearch();
    }

    // ---------- Lecture ----------

    void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Aperçu différé : en tapant vite, on ne charge pas le premier message à chaque lettre
        previewDelay.Stop();
        previewDelay.Start();
    }

    async void LoadSelected()
    {
        previewDelay.Stop();
        if (Results.SelectedItem is not ResultItem item) return;
        if (current?.Id == item.Hit.Id && currentMsg != null) return;
        var hit = item.Hit;
        current = hit;
        var seq = ++previewSeq;
        try
        {
            var msg = await Task.Run(() => MessageExtract.Load(hit.Path, hit.Offset, hit.Maildir));
            if (seq != previewSeq) return;
            currentMsg = msg;
            ShowHeader(item, msg);
            await RenderBody(hit, msg, seq);
            _ = MarkMatchingAttachments(hit.Id, msg, seq);
        }
        catch (Exception ex)
        {
            if (seq != previewSeq) return;
            currentMsg = null;
            ClearReader();
            ReaderEmptyHint.Text = "Ce message a été déplacé ou compacté par Thunderbird. Cliquez sur « Actualiser ». (" + ex.Message + ")";
        }
    }

    void ShowHeader(ResultItem item, MimeMessage msg)
    {
        var hit = item.Hit;
        ReaderEmpty.Visibility = Visibility.Collapsed;
        ReaderHeader.Visibility = Visibility.Visible;

        PvAccount.Text = hit.Account;
        PvAccountDot.Fill = item.AccountBrush;
        PvFolder.Text = hit.Folder.Replace("/", "  ›  ");
        PvSubject.Text = item.Subject;

        var from = msg.From.Mailboxes.FirstOrDefault();
        var name = from == null ? item.SenderName : string.IsNullOrWhiteSpace(from.Name) ? from.Address : from.Name;
        PvFromName.Text = name;
        PvFromAddr.Text = from?.Address ?? "";
        PvInitials.Text = Look.Initials(name);
        PvAvatar.Background = Look.ColorFor(from?.Address ?? name);
        PvDate.Text = Look.LongDate(hit.Date);

        var to = MessageExtract.FormatAddresses(msg.To);
        var cc = MessageExtract.FormatAddresses(msg.Cc);
        PvTo.Text = "À  " + (to.Length > 0 ? to : "(non renseigné)") + (cc.Length > 0 ? "     Cc  " + cc : "");
        PvTo.ToolTip = PvTo.Text;

        PvAttachments.ItemsSource = BuildChips(hit.Id, msg, null);
        RemoteButton.Visibility = Visibility.Collapsed;
    }

    static List<AttachmentChip> BuildChips(long id, MimeMessage msg, HashSet<int>? matches)
    {
        var chips = new List<AttachmentChip>();
        int i = 0;
        foreach (var a in msg.Attachments)
        {
            string name;
            long size = 0;
            if (a is MessagePart mp) name = (mp.Message?.Subject ?? "message") + ".eml";
            else
            {
                var p = (MimePart)a;
                name = string.IsNullOrWhiteSpace(p.FileName) ? "(sans nom)" : p.FileName;
                size = EstimateSize(p);
            }
            var (badge, brush) = Look.FileBadge(name);
            chips.Add(new AttachmentChip
            {
                Name = name,
                Size = size > 0 ? Look.FileSize(size) : "",
                Badge = badge,
                BadgeBrush = brush,
                Index = i,
                MessageId = id,
                Matches = matches?.Contains(i) == true,
            });
            i++;
        }
        return chips;
    }

    /// <summary>Taille approximative sans décoder : base64 = 3/4 des octets encodés.</summary>
    static long EstimateSize(MimePart p)
    {
        try
        {
            if (p.ContentDisposition?.Size is long s and > 0) return s;
            if (p.Content?.Stream is { CanSeek: true } st)
                return p.ContentTransferEncoding == ContentEncoding.Base64 ? st.Length * 3 / 4 : st.Length;
        }
        catch { }
        return 0;
    }

    /// <summary>En tâche de fond : repère les pièces jointes dont le texte contient un terme cherché.</summary>
    async Task MarkMatchingAttachments(long id, MimeMessage msg, int seq)
    {
        var terms = highlight.Select(Search.Fold).Where(t => t.Length >= 3).Distinct().ToList();
        if (terms.Count == 0 || !msg.Attachments.Any()) return;
        var matches = await Task.Run(() =>
        {
            var set = new HashSet<int>();
            int i = 0;
            foreach (var a in msg.Attachments)
            {
                if (a is MimePart p && AttachmentText.IsSupported(p.FileName))
                {
                    var text = Search.Fold(AttachmentText.Extract(p));
                    if (terms.Any(t => text.Contains(t, StringComparison.Ordinal))) set.Add(i);
                }
                i++;
            }
            return set;
        });
        if (seq == previewSeq && matches.Count > 0 && current?.Id == id)
            PvAttachments.ItemsSource = BuildChips(id, msg, matches);
    }

    async Task RenderBody(SearchHit hit, MimeMessage msg, int seq)
    {
        if (webReady)
        {
            var terms = highlight.ToList();
            var allow = remoteAllowed.Contains(hit.Id);
            var (html, hasRemote) = await Task.Run(() => (MailHtml.Build(msg, terms, allow, out var r), r));
            if (seq != previewSeq) return;
            RemoteButton.Visibility = hasRemote && !allow ? Visibility.Visible : Visibility.Collapsed;

            // Fichier temporaire : NavigateToString est limité à 2 Mo (images intégrées)
            var dir = Path.Combine(tempDir, "vue");
            Directory.CreateDirectory(dir);
            // Nom unique à chaque rendu : réafficher le même message (nouvelle recherche, images autorisées)
            // ne doit pas supprimer juste après le fichier qu'on vient d'écrire.
            var file = Path.Combine(dir, $"m{hit.Id}-{++renderCount}.html");
            await File.WriteAllTextAsync(file, html);
            var old = currentViewFile;
            currentViewFile = file;
            Web.Visibility = Visibility.Visible;
            Web.CoreWebView2.Navigate(new Uri(file).AbsoluteUri);
            if (old != null) try { File.Delete(old); } catch { }
        }
        else
        {
            var body = await Task.Run(() => MessageExtract.BodyText(msg, 200_000));
            if (seq != previewSeq) return;
            Fallback.Visibility = Visibility.Visible;
            Fallback.Document = BuildDocument(body, highlight);
        }
    }

    void ClearReader()
    {
        current = null;
        currentMsg = null;
        ReaderHeader.Visibility = Visibility.Collapsed;
        ReaderEmpty.Visibility = Visibility.Visible;
        ReaderEmptyHint.Text = "Son contenu s'affichera ici, avec vos mots surlignés.";
        Web.Visibility = Visibility.Collapsed;
        Fallback.Visibility = Visibility.Collapsed;
    }

    void Remote_Click(object sender, RoutedEventArgs e)
    {
        if (current == null || currentMsg == null) return;
        remoteAllowed.Add(current.Id);
        _ = RenderBody(current, currentMsg, previewSeq);
    }

    // ---------- WebView2 ----------

    async Task InitWebView()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Path.GetDirectoryName(dbPath)!, "WebView2"));
            await Web.EnsureCoreWebView2Async(env);
            var s = Web.CoreWebView2.Settings;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = true;
            s.AreDefaultContextMenusEnabled = true;
            s.IsGeneralAutofillEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            Web.CoreWebView2.NavigationStarting += (_, a) =>
            {
                if (a.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return;
                a.Cancel = true;
                OpenExternal(a.Uri);
            };
            Web.CoreWebView2.NewWindowRequested += (_, a) => { a.Handled = true; OpenExternal(a.Uri); };
            webReady = true;
            if (current != null && currentMsg != null) await RenderBody(current, currentMsg, previewSeq);
        }
        catch
        {
            // Pas de moteur WebView2 : affichage en texte simple
            webReady = false;
        }
    }

    static void OpenExternal(string uri)
    {
        if (!(uri.StartsWith("http://") || uri.StartsWith("https://") || uri.StartsWith("mailto:"))) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    static readonly Brush MarkBrush = new SolidColorBrush(Color.FromRgb(0xF6, 0xD2, 0xDC));

    /// <summary>Repli sans WebView2 : texte brut avec surlignage.</summary>
    static FlowDocument BuildDocument(string text, List<string> terms)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(4), FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
        var para = new Paragraph { Margin = new Thickness(0) };
        doc.Blocks.Add(para);
        var ranges = new List<(int Start, int Len)>();
        if (terms.Count > 0 && text.Length > 0)
        {
            var folded = Search.Fold(text);
            foreach (var t in terms.Select(Search.Fold).Where(t => t.Length > 0).Distinct())
            {
                int idx = 0;
                while ((idx = folded.IndexOf(t, idx, StringComparison.Ordinal)) >= 0 && ranges.Count < 5000)
                {
                    if (idx == 0 || !char.IsLetterOrDigit(folded[idx - 1])) ranges.Add((idx, t.Length));
                    idx += t.Length;
                }
            }
        }
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        int pos = 0;
        foreach (var (start, len) in ranges)
        {
            if (start < pos) continue;
            if (start > pos) para.Inlines.Add(new Run(text[pos..start]));
            para.Inlines.Add(new Run(text.Substring(start, len)) { Background = MarkBrush, FontWeight = FontWeights.SemiBold });
            pos = start + len;
        }
        if (pos < text.Length) para.Inlines.Add(new Run(text[pos..]));
        return doc;
    }

    // ---------- Actions ----------

    void OpenInThunderbird()
    {
        if (current == null || currentMsg == null) return;
        try
        {
            Directory.CreateDirectory(tempDir);
            var file = Path.Combine(tempDir, $"message-{current.Id}.eml");
            currentMsg.WriteTo(file);
            if (thunderbirdExe != null)
                Process.Start(new ProcessStartInfo(thunderbirdExe) { ArgumentList = { file }, UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Ouverture impossible : " + ex.Message, "TB Recherche", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void OpenTb_Click(object sender, RoutedEventArgs e) => OpenInThunderbird();

    void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem != null) OpenInThunderbird();
    }

    void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenInThunderbird(); e.Handled = true; }
    }

    void SaveEml_Click(object sender, RoutedEventArgs e)
    {
        if (current == null || currentMsg == null) return;
        var name = string.Join("_", (currentMsg.Subject ?? "message").Split(Path.GetInvalidFileNameChars())).Trim();
        if (name.Length > 80) name = name[..80];
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = (name.Length == 0 ? "message" : name) + ".eml", Filter = "Message (*.eml)|*.eml" };
        if (dlg.ShowDialog(this) == true) currentMsg.WriteTo(dlg.FileName);
    }

    void Attachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AttachmentChip chip || currentMsg == null || current?.Id != chip.MessageId) return;
        try
        {
            var att = currentMsg.Attachments.ElementAt(chip.Index);
            var dir = Path.Combine(tempDir, "pj-" + chip.MessageId);
            Directory.CreateDirectory(dir);
            string file;
            if (att is MessagePart { Message: not null } mp)
            {
                file = Path.Combine(dir, $"message-joint-{chip.Index}.eml");
                mp.Message.WriteTo(file);
            }
            else if (att is MimePart { Content: not null } part)
            {
                var fname = string.Join("_", (part.FileName ?? $"piece-{chip.Index}").Split(Path.GetInvalidFileNameChars()));
                file = Path.Combine(dir, fname);
                using (var fs = File.Create(file)) part.Content.DecodeTo(fs);
            }
            else return;
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Impossible d'ouvrir la pièce jointe : " + ex.Message, "TB Recherche", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Indexation ----------

    void ShowIndexState(string status, string detail, bool busy = false, bool error = false)
    {
        IndexStatus.Text = status;
        IndexDetail.Text = detail;
        IndexDot.Fill = new SolidColorBrush(error ? Color.FromRgb(0xE0, 0x5A, 0x4A) : busy ? Color.FromRgb(0x4F, 0x8F, 0xD9) : Color.FromRgb(0x3F, 0xB3, 0x7F));
    }

    void Index_Click(object sender, RoutedEventArgs e) => _ = RunIndex(full: false, quiet: false);

    void FullIndex_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reconstruire entièrement l'index ?\n\nAvec le texte des pièces jointes, cela prend une quinzaine de minutes. La recherche reste utilisable pendant ce temps, avec des résultats partiels.",
                "TB Recherche", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _ = RunIndex(full: true, quiet: false);
    }

    async Task RunIndex(bool full, bool quiet)
    {
        if (indexing || profileDir == null) return;
        indexing = true;
        IndexButton.IsEnabled = RebuildButton.IsEnabled = false;
        indexCts = new CancellationTokenSource();
        var ct = indexCts.Token;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        ShowIndexState("Recherche des nouveautés...", "", busy: true);
        var lastRefresh = Stopwatch.StartNew();

        var progress = new Progress<IndexProgress>(p =>
        {
            if (p.BytesTotal <= 0) return;
            Progress.IsIndeterminate = false;
            Progress.Value = 100.0 * p.BytesDone / p.BytesTotal;
            ShowIndexState($"Indexation {Progress.Value:0} %", $"{p.Messages:N0} messages lus, {p.FilesDone}/{p.FilesTotal} dossiers", busy: true);
            // Pendant une longue indexation, la liste se complète au fil de l'eau
            if (lastRefresh.Elapsed > TimeSpan.FromSeconds(20)) { lastRefresh.Restart(); _ = RunSearch(); }
        });

        try
        {
            var accounts = ThunderbirdProfile.LoadAccounts(profileDir);
            var res = await Task.Run(() => Indexer.Run(dbPath, accounts, progress, ct, full), ct);
            var (msgs, _) = await Task.Run(() => { using var db = IndexDb.OpenRead(dbPath); return Search.Stats(db); });
            ShowIndexState($"{msgs:N0} messages", $"À jour à {DateTime.Now:HH:mm}" + (res.FilesChanged > 0 ? $"  ·  +{res.MessagesAdded} / -{res.MessagesRemoved}" : ""));
            if (res.FilesChanged > 0 || res.FilesRemoved > 0 || full)
            {
                RefreshAccounts();
                await RunSearch();
            }
            if (!quiet && res.Warnings.Count > 0)
                MessageBox.Show(this, string.Join("\n", res.Warnings.Take(15)), "Remarques d'indexation", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { ShowIndexState("Indexation interrompue", "Elle reprendra au prochain lancement.", error: true); }
        catch (Exception ex)
        {
            ShowIndexState("Erreur d'indexation", ex.Message, error: true);
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            IndexButton.IsEnabled = RebuildButton.IsEnabled = true;
            indexing = false;
        }
    }
}
