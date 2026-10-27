using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeshControl.Core;
using MeshControl.UI;

namespace MeshControl.Files;

public partial class FilesWindow : MeshControl.UI.ChromeWindow, MeshControl.UI.ISessionWindow
{
    /// <summary>Empêche un double démarrage (Loaded et hôte d'onglet).</summary>
    private bool _sessionStarted;

    /// <summary>
    /// Fenêtre à utiliser comme propriétaire des boîtes de dialogue. En mode
    /// onglets, cette fenêtre n'est jamais affichée : WPF refuserait alors de
    /// l'accepter comme propriétaire. On remonte donc à la fenêtre hôte.
    /// </summary>
    private Window DialogOwner
    {
        get
        {
            if (IsLoaded && IsVisible) return this;
            var host = RootPanel is null ? null : Window.GetWindow(RootPanel);
            return host ?? Application.Current.MainWindow ?? this;
        }
    }

    /// <summary>Démarre la session ; sans effet si elle l'est déjà.</summary>
    public async Task StartSessionAsync()
    {
        if (_sessionStarted) return;
        _sessionStarted = true;
        await _session.StartAsync();
    }

    private readonly FilesSession _session;
    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;
    private readonly AppSettings? _settings;

    /// <param name="settings">
    /// Réglages où retenir le mode double volet et le dernier dossier local.
    /// Facultatif : sans eux, la fenêtre s'ouvre en vue simple.
    /// </param>
    public FilesWindow(MeshServerConnection connection, MeshNode node, AppSettings? settings = null)
    {
        _connection = connection;
        _node = node;
        _settings = settings;
        _session = new FilesSession(connection, node, Dispatcher);

        InitializeComponent();

        TitleText.Text = node.Name;
        RemotePaneTitleText.Text = node.Name;
        Title = $"{node.Name} — Fichiers";
        SetStatus("Connexion…", "#EFEFF4", "#8A8A8E");
        ShowEmptyState("\uF12B", "Connexion à la machine…", "", loading: true);
        TransferList.ItemsSource = _transfers;
        UpdateBreadcrumb();

        // Glisser d'un volet à l'autre : fichiers locaux vers la machine,
        // fichiers distants vers le dossier local affiché.
        _ = new ListDragSource(LocalList, CreateLocalDragData);
        _ = new ListDragSource(FileList, CreateRemoteDragData);

        _localPath = settings?.FilesLocalFolder is { } saved && Directory.Exists(saved)
            ? saved : DefaultLocalFolder();
        if (settings?.FilesDualPane == true) SetDualPane(true, remember: false);
        UpdateBridgeButtons();

        _session.StateChanged += OnStateChanged;
        _session.EntriesChanged += OnEntriesChanged;
        _session.TransferChanged += OnTransferChanged;

        // En mode onglets, la fenêtre n'est jamais affichée : Loaded ne se
        // déclenche pas, l'hôte appelle StartSessionAsync() explicitement.
        Loaded += async (_, _) => await StartSessionAsync();
        Closed += (_, _) =>
        {
            // Un lot en cours doit s'arrêter avec la fenêtre : plus rien n'en
            // afficherait la progression, et la session va se fermer.
            CancelQueue();
            _session.Stop();
        };
    }

    // MARK: - Session

    private void OnStateChanged()
    {
        UpdateBridgeButtons();
        switch (_session.State)
        {
            case FilesSessionState.Connecting:
                SetStatus("Connexion…", "#EFEFF4", "#8A8A8E");
                break;
            case FilesSessionState.Ready:
                SetStatus("Connecté", "#E4F8E8", "#248A3D");
                break;
            case FilesSessionState.Closed:
                SetStatus("Session fermée", "#FDE8E7", "#C0392B");
                ShowEmptyState("\uE783", "Session fermée",
                    _session.CloseReason ?? "La connexion à la machine a pris fin.");
                FileList.ItemsSource = null;
                if (!string.IsNullOrEmpty(_session.CloseReason))
                    MessageBox.Show(_session.CloseReason, "Mesh Control",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    private void OnEntriesChanged()
    {
        FileList.ItemsSource = _session.Entries.Select(e => new FileRow(e)).ToList();
        UpButton.IsEnabled = _session.PathComponents.Count > 0;
        UpdateBreadcrumb();
        UpdateBridgeButtons();

        if (_session.LastError is { } error)
        {
            SetStatus(error, "#FFF4E5", "#B25E09");
            ShowEmptyState("\uE72E", "Ce dossier n'a pas pu être lu",
                "Droits insuffisants, ou le chemin n'existe plus.");
        }
        else
        {
            if (_session.State == FilesSessionState.Ready)
                SetStatus(Plural(_session.Entries.Count, "élément", "éléments"), "#E4F8E8", "#248A3D");

            if (_session.Entries.Count == 0)
                ShowEmptyState("\uF12B", "Ce dossier est vide",
                    CanUploadHereSilently()
                        ? "Glissez des fichiers ou des dossiers ici pour les envoyer."
                        : "");
            else
                EmptyState.Visibility = Visibility.Collapsed;
        }
    }

    private void OnTransferChanged()
    {
        if (_current is { } item && _session.Transfer is { } progress) item.Report(progress);
        UpdateTransferPanel();
    }

    private void SetStatus(string text, string background, string foreground)
    {
        StatusLabel.Text = text;
        StatusBadge.Background = MakeBrush(background);
        StatusLabel.Foreground = MakeBrush(foreground);
    }

    private void ShowEmptyState(string glyph, string title, string hint, bool loading = false)
    {
        EmptyGlyph.Text = glyph;
        EmptyTitle.Text = title;
        EmptyHint.Text = hint;
        EmptyHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        EmptyProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush MakeBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(MeshControl.UI.Palette.Adapt(hex)));

    private static string Plural(int count, string singular, string plural)
        => $"{count} {(count > 1 ? plural : singular)}";

    // MARK: - Navigation

    private FileRow? SelectedRow => FileList.SelectedItem as FileRow;
    private List<FileRow> SelectedRows => FileList.SelectedItems.Cast<FileRow>().ToList();

    private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => await OpenSelectionAsync();

    private async void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await OpenSelectionAsync();
                break;
            case Key.Back:
                e.Handled = true;
                await _session.GoUpAsync();
                break;
            case Key.Delete:
                e.Handled = true;
                MenuDelete_Click(sender, e);
                break;
            case Key.F5:
                e.Handled = true;
                await _session.ListAsync();
                break;
        }
    }

    private async Task OpenSelectionAsync()
    {
        if (SelectedRow is not { } row) return;
        if (row.IsDirectory) await _session.EnterAsync(row.Entry);
        else Download_Click(this, new RoutedEventArgs());   // un fichier : on le télécharge
    }

    private async void Up_Click(object sender, RoutedEventArgs e) => await _session.GoUpAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _session.ListAsync();

    private async void MenuOpen_Click(object sender, RoutedEventArgs e)
        => await OpenSelectionAsync();

    /// <summary>
    /// Fil d'Ariane : chaque dossier du chemin est un bouton qui y ramène
    /// directement, sans remonter un niveau à la fois.
    /// </summary>
    private void UpdateBreadcrumb()
    {
        var components = _session.PathComponents;
        BuildBreadcrumb(BreadcrumbPanel, BreadcrumbScroller,
            _session.IsWindows ? "Cet ordinateur" : "/", _session.IsWindows ? "\uE7F8" : "\uEDA2",
            components, index => _session.GoToAsync(index),
            index => string.Join("/", components.Take(index + 1)));
    }

    /// <summary>
    /// Compose un fil d'Ariane : la racine, puis un bouton par dossier du chemin.
    /// Sert aux deux volets.
    /// </summary>
    /// <param name="navigate">Navigation vers le composant d'indice donn\u00E9 ; -1 pour la racine.</param>
    private void BuildBreadcrumb(Panel panel, ScrollViewer scroller, string rootLabel, string rootGlyph,
                                 IReadOnlyList<string> components, Func<int, Task> navigate,
                                 Func<int, string> tooltip)
    {
        panel.Children.Clear();
        panel.Children.Add(Crumb(rootLabel, rootGlyph, components.Count == 0, null, () => navigate(-1)));

        for (var index = 0; index < components.Count; index++)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "\uE76C",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 8,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 1, 1, 0),
            });
            var target = index;
            panel.Children.Add(Crumb(components[index], null, index == components.Count - 1,
                                     tooltip(index), () => navigate(target)));
        }

        scroller.ScrollToRightEnd();
    }

    private Button Crumb(string label, string? glyph, bool isLast, string? tooltip, Func<Task> navigate)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null)
            content.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button
        {
            Content = content,
            Style = (Style)FindResource("CrumbButton"),
            ToolTip = tooltip,
        };
        if (isLast)
        {
            button.FontWeight = FontWeights.SemiBold;
            button.Foreground = (Brush)FindResource("TextPrimaryBrush");
        }
        button.Click += async (_, _) => await navigate();
        return button;
    }

    // MARK: - File de transferts

    /// <summary>
    /// Transferts de la session, en cours, à venir et passés. Le protocole n'en
    /// accepte qu'un à la fois (l'agent suit un seul envoi par tunnel) : la file
    /// les enchaîne sans que l'utilisateur ait à attendre devant chaque fichier.
    /// </summary>
    private readonly ObservableCollection<TransferItem> _transfers = new();

    private bool _queueRunning;
    private TransferItem? _current;
    private bool _transfersExpanded = true;

    /// <summary>
    /// Dossiers distants où le lot a écrit. Sert à ne relire la liste que si
    /// c'est le dossier affiché — l'agent ne signale pas ses propres écritures.
    /// </summary>
    private readonly HashSet<string> _queueWrittenPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dossiers distants dont l'existence a été vérifiée pendant ce lot.</summary>
    private readonly HashSet<string> _ensuredDirectories = new(StringComparer.OrdinalIgnoreCase);

    private void Enqueue(IReadOnlyList<TransferItem> items)
    {
        if (items.Count == 0) return;

        // Un nouveau lot, file au repos : les transferts réussis du lot
        // précédent n'ont plus rien à dire. Les échecs restent, pour être relancés.
        if (!_queueRunning)
            foreach (var done in _transfers.Where(t => t.State == TransferState.Done).ToList())
                _transfers.Remove(done);

        foreach (var item in items) _transfers.Add(item);
        UpdateTransferPanel();

        if (!_queueRunning) _ = RunQueueAsync();
    }

    /// <summary>
    /// Traite la file, un transfert après l'autre. Un échec n'arrête pas le lot :
    /// il est relevé sur sa ligne et le fichier suivant part — sinon un seul
    /// fichier verrouillé annulerait tout le reste.
    /// </summary>
    private async Task RunQueueAsync()
    {
        _queueRunning = true;
        _ensuredDirectories.Clear();

        try
        {
            while (_transfers.FirstOrDefault(t => t.State == TransferState.Pending) is { } item)
            {
                await RunTransferAsync(item);
                UpdateTransferPanel();
            }
        }
        finally
        {
            _queueRunning = false;
            _current = null;
            UpdateTransferPanel();
        }

        await ReportQueueResultAsync();
    }

    private async Task RunTransferAsync(TransferItem item)
    {
        using var cancellation = new CancellationTokenSource();
        item.Cancellation = cancellation;
        _current = item;
        item.Start();
        UpdateTransferPanel();

        try
        {
            if (item.IsUpload)
            {
                if (item.EnsureRemoteDirectory && !_ensuredDirectories.Contains(item.RemotePath))
                {
                    await _session.EnsureDirectoryAsync(item.RemotePath, cancellation.Token);
                    _ensuredDirectories.Add(item.RemotePath);
                }
                await _session.UploadAsync(item.LocalPath, item.RemotePath, cancellation.Token);
                _queueWrittenPaths.Add(item.RemotePath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
                await _session.DownloadAsync(item.Entry!, item.RemotePath, item.LocalPath,
                                             cancellation.Token);
                _localWritten = true;
            }
            item.Complete();
        }
        catch (OperationCanceledException)
        {
            item.Cancel();
        }
        catch (Exception ex)
        {
            item.Fail(ex.Message);
            Log.Warning($"Transfert de {item.Name} : {ex.Message}", "Fichiers");
        }
        finally
        {
            item.Cancellation = null;
            _current = null;
        }
    }

    /// <summary>
    /// Compte rendu du lot, puis relecture du dossier si le lot y a écrit. Les
    /// échecs ne passent plus par une boîte de dialogue : ils restent visibles
    /// sur leur ligne, avec de quoi les relancer.
    /// </summary>
    private async Task ReportQueueResultAsync()
    {
        var current = NormalizeRemote(_session.CurrentPath);
        // Écrire dans un sous-dossier change aussi le dossier affiché : il peut
        // y être apparu.
        var refresh = _queueWrittenPaths.Any(path =>
        {
            var written = NormalizeRemote(path);
            return written.Equals(current, StringComparison.OrdinalIgnoreCase)
                   || written.StartsWith(current + "/", StringComparison.OrdinalIgnoreCase);
        });
        _queueWrittenPaths.Clear();

        var failed = _transfers.Count(t => t.State == TransferState.Failed);
        if (failed > 0)
        {
            SetStatus($"{Plural(failed, "transfert", "transferts")} en échec", "#FFF4E5", "#B25E09");
            // Le détail doit se voir : c'est là que se lisent les causes.
            SetTransfersExpanded(true);
        }

        if (refresh && _session.State == FilesSessionState.Ready) await _session.ListAsync();

        // Les fichiers reçus doivent apparaître dans le volet local.
        if (_localWritten && _dualPane) await LoadLocalAsync(_localPath);
        _localWritten = false;
    }

    private static string NormalizeRemote(string path) => path.Replace('\\', '/').TrimEnd('/');

    private void CancelTransfer_Click(object sender, RoutedEventArgs e) => CancelQueue();

    /// <summary>
    /// Interrompt le transfert en cours et vide la file : quand on annule un lot,
    /// c'est le lot entier que l'on veut arrêter, pas seulement le fichier courant.
    /// </summary>
    private void CancelQueue()
    {
        foreach (var item in _transfers.Where(t => t.State == TransferState.Pending)) item.Cancel();
        _current?.Cancellation?.Cancel();
        UpdateTransferPanel();
    }

    /// <summary>Action de la ligne : annuler, réessayer, ou montrer le fichier reçu.</summary>
    private void TransferAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TransferItem item) return;

        switch (item.State)
        {
            case TransferState.Pending:
                item.Cancel();
                break;

            case TransferState.Running:
                item.Cancellation?.Cancel();
                break;

            case TransferState.Failed or TransferState.Cancelled:
                item.Requeue();
                if (!_queueRunning) _ = RunQueueAsync();
                break;

            case TransferState.Done when !item.IsUpload:
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.LocalPath}\"");
                }
                catch { /* Explorateur indisponible : sans conséquence */ }
                break;
        }
        UpdateTransferPanel();
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _transfers.Where(t => t.IsFinished).ToList()) _transfers.Remove(item);
        UpdateTransferPanel();
    }

    private void ToggleTransfers_Click(object sender, RoutedEventArgs e)
        => SetTransfersExpanded(!_transfersExpanded);

    private void SetTransfersExpanded(bool expanded)
    {
        _transfersExpanded = expanded;
        TransferListHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ToggleTransfersButton.Content = expanded ? "\uE70D" : "\uE70E";
        ToggleTransfersButton.ToolTip = expanded ? "Masquer le détail" : "Afficher le détail";
    }

    /// <summary>
    /// Résumé du panneau des transferts : où en est le lot, à quel débit, pour
    /// combien de temps encore.
    /// </summary>
    private void UpdateTransferPanel()
    {
        if (_transfers.Count == 0)
        {
            TransferPanel.Visibility = Visibility.Collapsed;
            return;
        }
        TransferPanel.Visibility = Visibility.Visible;

        var active = _transfers.Where(t => t.State != TransferState.Cancelled).ToList();
        var done = _transfers.Count(t => t.State == TransferState.Done);
        var failed = _transfers.Count(t => t.State == TransferState.Failed);
        var waiting = _transfers.Count(t => t.State is TransferState.Pending or TransferState.Running);

        long totalBytes = active.Sum(t => t.Size);
        long doneBytes = active.Sum(t => t.State == TransferState.Done ? t.Size
                                       : t.State == TransferState.Running ? t.Transferred : 0);

        TransferBar.IsIndeterminate = false;
        TransferBar.Value = totalBytes > 0 ? (double)doneBytes / totalBytes
                          : active.Count > 0 ? (double)done / active.Count : 0;

        CancelTransferButton.Visibility = _queueRunning ? Visibility.Visible : Visibility.Collapsed;
        ClearFinishedButton.Visibility = _queueRunning ? Visibility.Collapsed : Visibility.Visible;

        var uploads = _transfers.All(t => t.IsUpload);
        var downloads = _transfers.All(t => !t.IsUpload);

        if (_queueRunning)
        {
            var verb = uploads ? "Envoi" : downloads ? "Téléchargement" : "Transfert";
            var total = done + failed + waiting;
            TransferTitle.Text = $"{verb} en cours · {Math.Min(done + failed + 1, total)} sur {total}";

            var speed = _current?.BytesPerSecond ?? 0;
            var parts = new List<string>
            {
                $"{FileRow.FormatBytes(doneBytes)} sur {FileRow.FormatBytes(totalBytes)}",
            };
            if (FileRow.FormatSpeed(speed) is { Length: > 0 } rate) parts.Add(rate);
            if (FileRow.FormatRemaining(totalBytes - doneBytes, speed) is { Length: > 0 } eta) parts.Add(eta);
            TransferSubtitle.Text = string.Join(" · ", parts);

            SetTransferBadge(uploads || !downloads ? "\uE898" : "\uE896", "#E8F2FE", "#0A84FF");
            TransferBar.Foreground = (Brush)FindResource("AccentBrush");
        }
        else if (failed > 0)
        {
            TransferTitle.Text = $"{Plural(done, "fichier transféré", "fichiers transférés")}, "
                               + $"{failed} en échec";
            TransferSubtitle.Text = "Survolez une ligne pour le détail, ou relancez-la avec ↻.";
            SetTransferBadge("\uE7BA", "#FFF4E5", "#B25E09");
            TransferBar.Foreground = MakeBrush("#FF9F0A");
        }
        else if (done > 0)
        {
            TransferTitle.Text = uploads ? $"{Plural(done, "fichier envoyé", "fichiers envoyés")}"
                               : downloads ? $"{Plural(done, "fichier téléchargé", "fichiers téléchargés")}"
                               : $"{Plural(done, "fichier transféré", "fichiers transférés")}";
            TransferSubtitle.Text = $"{FileRow.FormatBytes(totalBytes)} au total";
            SetTransferBadge("\uE73E", "#E4F8E8", "#248A3D");
            TransferBar.Foreground = (Brush)FindResource("OnlineBrush");
        }
        else
        {
            TransferTitle.Text = "Transferts annulés";
            TransferSubtitle.Text = "Relancez un fichier avec ↻, ou effacez la liste.";
            SetTransferBadge("\uE711", "#EFEFF4", "#8A8A8E");
            TransferBar.Foreground = (Brush)FindResource("OfflineBrush");
        }
    }

    private void SetTransferBadge(string glyph, string background, string foreground)
    {
        TransferHeaderGlyph.Text = glyph;
        TransferHeaderGlyph.Foreground = MakeBrush(foreground);
        TransferHeaderBadge.Background = MakeBrush(background);
    }

    // MARK: - Envoi

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUploadHere()) return;

        var dialog = new OpenFileDialog
        {
            Title = "Choisir les fichiers à envoyer",
            Multiselect = true,
        };
        if (dialog.ShowDialog(DialogOwner) != true) return;

        EnqueueUploads(dialog.FileNames);
    }

    /// <summary>
    /// L'envoi exige un dossier réel : la racine Windows (« Cet ordinateur ») ne
    /// contient que des lecteurs, et l'agent n'y écrit rien.
    /// </summary>
    private bool CanUploadHereSilently()
        => _session.PathComponents.Count > 0 || _node.OsFamily != OSFamily.Windows;

    private bool CanUploadHere()
    {
        if (CanUploadHereSilently()) return true;

        MessageBox.Show("Ouvrez d'abord un lecteur : on ne peut pas déposer un fichier "
                      + "à la racine de « Cet ordinateur ».",
            "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    /// <summary>
    /// Met en file l'envoi de ces fichiers et dossiers vers le dossier affiché.
    /// Un dossier est envoyé avec toute son arborescence : les sous-dossiers
    /// sont recréés sur la machine au fil de l'envoi.
    /// </summary>
    private async void EnqueueUploads(IReadOnlyList<string> paths)
    {
        var target = _session.CurrentPath;
        var hasDirectories = paths.Any(Directory.Exists);
        if (hasDirectories) SetStatus("Préparation de l'envoi…", "#E8F2FE", "#0A84FF");

        // Parcourir un gros dossier peut prendre un moment : hors du fil d'interface.
        var (items, unreadable) = await Task.Run(() => PlanUploads(paths, target));

        Enqueue(items);

        if (unreadable > 0)
            SetStatus($"{Plural(unreadable, "élément illisible ignoré", "éléments illisibles ignorés")}",
                      "#FFF4E5", "#B25E09");
        else if (items.Count == 0 && hasDirectories)
            SetStatus("Aucun fichier à envoyer dans ce dossier", "#EFEFF4", "#8A8A8E");
    }

    private (List<TransferItem> Items, int Unreadable) PlanUploads(IEnumerable<string> paths, string target)
    {
        var items = new List<TransferItem>();
        var unreadable = 0;

        // Fichiers cachés compris (un .htaccess compte), mais ni fichiers
        // système, ni liens symboliques qui pourraient boucler.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
        };

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    var root = new DirectoryInfo(path);
                    var remoteRoot = _session.CombinePath(target, root.Name);

                    foreach (var file in root.EnumerateFiles("*", options))
                    {
                        var remoteDirectory = remoteRoot;
                        var relative = Path.GetRelativePath(root.FullName, file.DirectoryName!);
                        if (relative != ".")
                            foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
                                remoteDirectory = _session.CombinePath(remoteDirectory, segment);

                        items.Add(TransferItem.Upload(file.FullName, remoteDirectory, file.Length,
                                                      ensureRemoteDirectory: true));
                    }
                }
                else if (File.Exists(path))
                {
                    items.Add(TransferItem.Upload(path, target, new FileInfo(path).Length));
                }
            }
            catch
            {
                unreadable++;
            }
        }
        return (items, unreadable);
    }

    // MARK: - Envoi vers plusieurs appareils

    private void SendToDevices_Click(object sender, RoutedEventArgs e)
    {
        var window = new SendFilesWindow(
            _connection, settings: null, preselected: [_node],
            remotePathHint: CanUploadHereSilently() ? _session.CurrentPath : null)
        {
            Owner = DialogOwner,
        };
        window.Show();
    }

    // MARK: - Téléchargement

    /// <summary>Au-delà, un dossier est sans doute choisi par erreur (un lecteur entier…).</summary>
    private const int MaxFolderDownloadFiles = 10_000;

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var selection = DownloadableSelection();
        if (selection is null) return;

        // Double volet : le dossier d'arrivée est celui affiché à gauche. On
        // l'a sous les yeux, inutile de le redemander.
        if (_dualPane && _localPath is not null)
        {
            await DownloadToFolderAsync(selection, _localPath);
            return;
        }

        // Un seul fichier : on laisse choisir son nom, comme tout « Enregistrer
        // sous ». Sinon : on demande le dossier d'arrivée.
        if (selection is [{ IsDirectory: false } single])
        {
            var dialog = new SaveFileDialog
            {
                FileName = single.Name,
                Title = "Enregistrer le fichier",
            };
            if (dialog.ShowDialog(DialogOwner) != true) return;

            Enqueue([TransferItem.Download(single.Entry, _session.FullPathOf(single.Entry),
                                           dialog.FileName)]);
            return;
        }

        var folder = new OpenFolderDialog { Title = "Choisir le dossier de destination" };
        if (folder.ShowDialog(DialogOwner) != true) return;

        await DownloadToFolderAsync(selection, folder.FolderName);
    }

    /// <summary>
    /// Fichiers et dossiers distants sélectionnés, lecteurs exclus. Null, après
    /// explication, s'il n'y a rien à télécharger.
    /// </summary>
    private List<FileRow>? DownloadableSelection()
    {
        var selection = SelectedRows.Where(r => r.Entry.Kind != FileKind.Drive).ToList();
        if (selection.Count > 0) return selection;

        MessageBox.Show(
            SelectedRows.Count > 0
                ? "Un lecteur entier ne se télécharge pas : ouvrez-le et sélectionnez "
                  + "les fichiers ou dossiers voulus."
                : "Sélectionnez au moins un fichier ou un dossier à télécharger.",
            "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    /// <summary>
    /// Met en file le téléchargement de ces éléments dans un dossier local ; un
    /// dossier distant y est recréé avec toute son arborescence.
    /// </summary>
    private async Task DownloadToFolderAsync(List<FileRow> selection, string folder)
    {
        // Sur un lot, on ne peut pas poser une question par fichier déjà
        // présent : les doublons sont numérotés, comme le fait un navigateur.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<TransferItem>();

        foreach (var row in selection.Where(r => !r.IsDirectory))
            items.Add(TransferItem.Download(row.Entry, _session.FullPathOf(row.Entry),
                UniqueLocalPath(folder, SafeLocalName(row.Name), reserved)));

        var directories = selection.Where(r => r.IsDirectory).ToList();
        if (directories.Count > 0)
        {
            SetStatus("Préparation du téléchargement…", "#E8F2FE", "#0A84FF");
            try
            {
                foreach (var row in directories)
                {
                    var localRoot = UniqueLocalPath(folder, SafeLocalName(row.Name), reserved);
                    await CollectRemoteFilesAsync(_session.FullPathOf(row.Entry), localRoot, items);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Préparation interrompue", "#FFF4E5", "#B25E09");
                MessageBox.Show(ex.Message, "Mesh Control",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SetStatus(Plural(_session.Entries.Count, "élément", "éléments"), "#E4F8E8", "#248A3D");
        }

        if (items.Count == 0)
        {
            MessageBox.Show("Les dossiers sélectionnés ne contiennent aucun fichier.",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Enqueue(items);
    }

    /// <summary>
    /// Parcourt un dossier distant et prépare le téléchargement de chacun de ses
    /// fichiers, en reproduisant l'arborescence sous <paramref name="localDirectory"/>.
    /// </summary>
    private async Task CollectRemoteFilesAsync(string remoteDirectory, string localDirectory,
                                               List<TransferItem> items)
    {
        var entries = await _session.ListDirectoryAsync(remoteDirectory)
                      ?? throw new MeshException($"Le dossier « {remoteDirectory} » n'a pas pu être lu.");

        foreach (var entry in entries)
        {
            var remotePath = _session.CombinePath(remoteDirectory, entry.Name);
            var localPath = Path.Combine(localDirectory, SafeLocalName(entry.Name));

            if (entry.Kind == FileKind.File)
            {
                items.Add(TransferItem.Download(entry, remotePath, localPath));
                if (items.Count > MaxFolderDownloadFiles)
                    throw new MeshException(
                        $"Plus de {MaxFolderDownloadFiles:N0} fichiers : choisissez des dossiers plus précis.");
            }
            else if (entry.Kind == FileKind.Directory)
            {
                await CollectRemoteFilesAsync(remotePath, localPath, items);
            }
        }
    }

    /// <summary>
    /// Nom utilisable sous Windows. Un nom Linux peut contenir « : » ou « ? »,
    /// que l'Explorateur refuse.
    /// </summary>
    private static string SafeLocalName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return safe.Length > 0 ? safe : "_";
    }

    /// <summary>Chemin local encore libre pour ce nom de fichier ou de dossier.</summary>
    private static string UniqueLocalPath(string directory, string name, HashSet<string> reserved)
    {
        static bool Taken(string path) => File.Exists(path) || Directory.Exists(path);

        var candidate = Path.Combine(directory, name);
        if (!Taken(candidate) && reserved.Add(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!Taken(candidate) && reserved.Add(candidate)) return candidate;
        }
    }

    // MARK: - Double volet

    private bool _dualPane;

    /// <summary>Dossier affiché dans le volet local ; null pour « Ce PC », la liste des lecteurs.</summary>
    private string? _localPath;
    private bool _localLoaded;

    /// <summary>Le lot a déposé des fichiers sur ce poste : le volet local est à relire.</summary>
    private bool _localWritten;

    /// <summary>
    /// Format des fichiers distants glissés vers le volet local. Les données
    /// ne portent rien : le dépôt reprend la sélection de la liste distante.
    /// </summary>
    private const string RemoteDragFormat = "MeshControl.RemoteFiles";

    /// <summary>Largeur visée en double volet : deux listes lisibles côte à côte.</summary>
    private const double DualPaneWidth = 1260;

    private static string DefaultLocalFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        return Directory.Exists(downloads) ? downloads : profile;
    }

    private void DualPane_Click(object sender, RoutedEventArgs e) => SetDualPane(!_dualPane);

    /// <param name="remember">Retenir le choix pour les prochaines fenêtres.</param>
    private void SetDualPane(bool dual, bool remember = true)
    {
        _dualPane = dual;

        var visibility = dual ? Visibility.Visible : Visibility.Collapsed;
        LocalPane.Visibility = visibility;
        BridgePanel.Visibility = visibility;
        RemotePaneTitle.Visibility = visibility;
        LocalColumn.Width = dual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        BridgeColumn.Width = dual ? new GridLength(64) : new GridLength(0);
        DualPaneButton.Style = (Style)FindResource(dual ? "AccentToolButton" : "HeaderButton");

        // Chaque volet n'a plus que la moitié de la place : colonnes resserrées.
        SetColumnWidths(FileList, dual ? [250, 80, 125, 110] : [430, 110, 150, 150]);

        if (dual) EnsureWidth();
        // Par le répartiteur : appelé depuis le constructeur, le chargement doit
        // reprendre sur le fil d'interface une fois le dossier lu.
        if (dual && !_localLoaded) _ = Dispatcher.InvokeAsync(() => LoadLocalAsync(_localPath));

        if (remember && _settings is not null)
        {
            _settings.FilesDualPane = dual;
            _settings.Save();
        }
        UpdateBridgeButtons();
    }

    private static void SetColumnWidths(ListView list, double[] widths)
    {
        var columns = ((GridView)list.View).Columns;
        for (var index = 0; index < columns.Count && index < widths.Length; index++)
            columns[index].Width = widths[index];
    }

    /// <summary>
    /// Élargit la fenêtre pour loger deux volets, sans déborder de l'écran. En
    /// onglet, la fenêtre n'est pas affichée : c'est l'hôte qui décide de la taille.
    /// </summary>
    private void EnsureWidth()
    {
        if (WindowState != WindowState.Normal) return;

        var work = SystemParameters.WorkArea;
        var target = Math.Min(DualPaneWidth, work.Width - 40);
        if (Width >= target) return;

        if (!IsLoaded)
        {
            Width = target;
            return;
        }
        if (!IsVisible) return;

        // On s'élargit des deux côtés, pour rester centré sur la même position.
        var left = Left - (target - Width) / 2;
        Left = Math.Max(work.Left + 20, Math.Min(left, work.Right - target - 20));
        Width = target;
    }

    /// <summary>
    /// Active les boutons de la passerelle selon ce qui est sélectionné de
    /// chaque côté, et ce que la destination permet.
    /// </summary>
    private void UpdateBridgeButtons()
    {
        if (SendLocalButton is null) return;   // pendant InitializeComponent

        var ready = _session.State == FilesSessionState.Ready;
        SendLocalButton.IsEnabled = ready && CanUploadHereSilently() && LocalSelectedPaths().Count > 0;
        DownloadHereButton.IsEnabled = ready && _localPath is not null
                                       && SelectedRows.Any(r => r.Entry.Kind != FileKind.Drive);
    }

    private void PaneSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateBridgeButtons();

    /// <summary>Passerelle →, et « Envoyer vers la machine » du volet local.</summary>
    private void SendLocal_Click(object sender, RoutedEventArgs e)
    {
        var paths = LocalSelectedPaths();
        if (paths.Count == 0 || !CanUploadHere()) return;
        EnqueueUploads(paths);
    }

    /// <summary>Passerelle ← : la sélection distante arrive dans le dossier local affiché.</summary>
    private async void DownloadHere_Click(object sender, RoutedEventArgs e)
    {
        if (_localPath is null)
        {
            MessageBox.Show("Ouvrez d'abord, à gauche, le dossier de ce poste où déposer les fichiers.",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (DownloadableSelection() is { } selection) await DownloadToFolderAsync(selection, _localPath);
    }

    // MARK: - Volet local

    private List<FileRow> LocalSelectedRows => LocalList.SelectedItems.Cast<FileRow>().ToList();

    private string LocalPathOf(FileRow row)
        => _localPath is null ? row.Name : Path.Combine(_localPath, row.Name);

    /// <summary>Chemins des fichiers et dossiers locaux sélectionnés, lecteurs exclus.</summary>
    private List<string> LocalSelectedPaths()
        => LocalSelectedRows.Where(r => r.Entry.Kind != FileKind.Drive).Select(LocalPathOf).ToList();

    /// <summary>
    /// Affiche un dossier de ce poste. La lecture se fait hors du fil
    /// d'interface : un lecteur réseau qui ne répond pas ne doit pas figer la fenêtre.
    /// </summary>
    private async Task LoadLocalAsync(string? path)
    {
        List<FileEntry> entries;
        try
        {
            entries = await Task.Run(() => ReadLocalDirectory(path));
        }
        catch (Exception ex)
        {
            SetStatus("Dossier local illisible", "#FFF4E5", "#B25E09");
            Log.Warning($"Lecture de {path} : {ex.Message}", "Fichiers");
            // Premier affichage d'un dossier disparu depuis : on repart des lecteurs.
            if (!_localLoaded && path is not null) await LoadLocalAsync(null);
            return;
        }

        _localLoaded = true;
        _localPath = path;
        LocalList.ItemsSource = entries.Select(entry => new FileRow(entry)).ToList();
        LocalUpButton.IsEnabled = path is not null;
        UpdateLocalBreadcrumb();

        LocalEmptyText.Text = "Ce dossier est vide";
        LocalEmptyState.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_settings is not null && path is not null)
        {
            _settings.FilesLocalFolder = path;
            _settings.Save();
        }
        UpdateBridgeButtons();
    }

    /// <summary>
    /// Contenu d'un dossier local, dans le même format que les entrées
    /// distantes : les deux volets partagent ainsi lignes et pastilles. Les
    /// fichiers cachés et système sont omis, comme dans l'Explorateur.
    /// </summary>
    private static List<FileEntry> ReadLocalDirectory(string? path)
    {
        if (path is null)
            return DriveInfo.GetDrives().Select(drive => new FileEntry
            {
                Name = drive.Name,
                Kind = FileKind.Drive,
                DriveType = drive.DriveType switch
                {
                    DriveType.Fixed => "local",
                    DriveType.Removable => "amovible",
                    DriveType.Network => "réseau",
                    DriveType.CDRom => "optique",
                    _ => null,
                },
                FreeBytes = FreeSpace(drive),
            }).ToList();

        var options = new EnumerationOptions { IgnoreInaccessible = true };
        var entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", options)
            .Select(info => new FileEntry
            {
                Name = info.Name,
                Kind = info is DirectoryInfo ? FileKind.Directory : FileKind.File,
                Size = (info as FileInfo)?.Length ?? 0,
                Modified = info.LastWriteTime,
            })
            .ToList();
        entries.Sort(FileEntry.CompareForDisplay);
        return entries;
    }

    /// <summary>Espace libre, ou null pour un lecteur vide ou déconnecté.</summary>
    private static long? FreeSpace(DriveInfo drive)
    {
        try { return drive.IsReady ? drive.AvailableFreeSpace : null; }
        catch { return null; }
    }

    private void UpdateLocalBreadcrumb()
    {
        var path = _localPath;
        var root = path is null ? null : Path.GetPathRoot(path);
        var components = new List<string>();
        if (path is not null && root is not null)
        {
            components.Add(root.TrimEnd('\\', '/'));
            components.AddRange(path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries));
        }

        string PathAt(int index) => index == 0
            ? root!
            : Path.Combine([root!, .. components.Skip(1).Take(index)]);

        BuildBreadcrumb(LocalBreadcrumbPanel, LocalBreadcrumbScroller, "Ce PC", "\uE7F8",
            components, index => LoadLocalAsync(index < 0 ? null : PathAt(index)), PathAt);
    }

    private async void LocalUp_Click(object sender, RoutedEventArgs e) => await LocalGoUpAsync();

    private async void LocalRefresh_Click(object sender, RoutedEventArgs e) => await LoadLocalAsync(_localPath);

    /// <summary>Remonte d'un niveau ; depuis la racine d'un lecteur, revient à la liste des lecteurs.</summary>
    private Task LocalGoUpAsync()
        => _localPath is null ? Task.CompletedTask
                              : LoadLocalAsync(Directory.GetParent(_localPath)?.FullName);

    private async void LocalList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => await OpenLocalSelectionAsync();

    private async void LocalList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await OpenLocalSelectionAsync();
                break;
            case Key.Back:
                e.Handled = true;
                await LocalGoUpAsync();
                break;
            case Key.F5:
                e.Handled = true;
                await LoadLocalAsync(_localPath);
                break;
        }
    }

    /// <summary>Un dossier s'ouvre dans le volet ; un fichier, avec son application.</summary>
    private async Task OpenLocalSelectionAsync()
    {
        if (LocalList.SelectedItem is not FileRow row) return;

        if (row.IsDirectory)
        {
            await LoadLocalAsync(LocalPathOf(row));
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(LocalPathOf(row)) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void LocalMenuOpen_Click(object sender, RoutedEventArgs e) => await OpenLocalSelectionAsync();

    private void LocalMenuReveal_Click(object sender, RoutedEventArgs e)
    {
        if (LocalList.SelectedItem is not FileRow row) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{LocalPathOf(row)}\""); }
        catch { /* Explorateur indisponible : sans conséquence */ }
    }

    private void LocalMenu_Opened(object sender, RoutedEventArgs e)
    {
        var sendable = LocalSelectedPaths().Count;
        LocalMenuSend.IsEnabled = sendable > 0 && _session.State == FilesSessionState.Ready
                                  && CanUploadHereSilently();
        LocalMenuSend.Header = sendable > 1 ? $"Envoyer ces {sendable} éléments vers la machine"
                                            : "Envoyer vers la machine";
        LocalMenuOpen.IsEnabled = LocalList.SelectedItems.Count == 1;
        LocalMenuReveal.IsEnabled = LocalList.SelectedItems.Count == 1 && _localPath is not null;
    }

    // MARK: - Glisser d'un volet à l'autre

    /// <summary>
    /// Fichiers locaux glissés : un dépôt de fichiers ordinaire, que le volet
    /// distant accueille comme ceux de l'Explorateur — et que l'Explorateur
    /// accepte aussi.
    /// </summary>
    private DataObject? CreateLocalDragData()
    {
        var paths = LocalSelectedPaths();
        return paths.Count == 0 ? null : new DataObject(DataFormats.FileDrop, paths.ToArray());
    }

    /// <summary>
    /// Fichiers distants glissés. Seul le volet local sait les recevoir : hors
    /// double volet, on ne lance pas de glisser qui ne mènerait nulle part.
    /// </summary>
    private DataObject? CreateRemoteDragData()
    {
        if (!_dualPane || !SelectedRows.Any(r => r.Entry.Kind != FileKind.Drive)) return null;
        return new DataObject(RemoteDragFormat, "");
    }

    private void LocalPane_DragOver(object sender, DragEventArgs e)
    {
        // Ce dépôt ne vise pas la machine distante : son voile n'a rien à faire ici.
        HideDropOverlay();

        var acceptable = e.Data.GetDataPresent(RemoteDragFormat) && _localPath is not null;
        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (!acceptable)
        {
            LocalDropOverlay.Visibility = Visibility.Collapsed;
        }
        else if (LocalDropOverlay.Visibility != Visibility.Visible)
        {
            LocalDropOverlayText.Text = $"dans « {_localPath} »";
            LocalDropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void LocalPane_DragLeave(object sender, DragEventArgs e)
        => LocalDropOverlay.Visibility = Visibility.Collapsed;

    private async void LocalPane_Drop(object sender, DragEventArgs e)
    {
        LocalDropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (!e.Data.GetDataPresent(RemoteDragFormat) || _localPath is null) return;
        if (DownloadableSelection() is { } selection) await DownloadToFolderAsync(selection, _localPath);
    }

    // MARK: - Glisser-déposer

    /// <summary>
    /// Survol d'un dépôt. On n'accepte que des fichiers, et seulement dans un
    /// dossier où l'écriture est possible : le curseur le dit avant le lâcher.
    /// </summary>
    private void RootPanel_DragOver(object sender, DragEventArgs e)
    {
        var acceptable = _session.State == FilesSessionState.Ready
                         && CanUploadHereSilently()
                         && e.Data.GetDataPresent(DataFormats.FileDrop);

        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (acceptable) ShowDropOverlay();
        else HideDropOverlay();
    }

    private void RootPanel_DragLeave(object sender, DragEventArgs e) => HideDropOverlay();

    private void RootPanel_Drop(object sender, DragEventArgs e)
    {
        HideDropOverlay();
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        if (!CanUploadHere()) return;

        EnqueueUploads(paths);
    }

    private void ShowDropOverlay()
    {
        // DragOver se déclenche à chaque mouvement de souris : on ne recompose le
        // libellé qu'au premier passage.
        if (DropOverlay.Visibility == Visibility.Visible) return;

        DropOverlayText.Text = $"dans « {_session.DisplayPath} » — les dossiers sont envoyés avec leur contenu";
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void HideDropOverlay() => DropOverlay.Visibility = Visibility.Collapsed;

    // MARK: - Opérations

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextPromptDialog("Nouveau dossier",
            "Nom du nouveau dossier :", "Nouveau dossier", "Créer") { Owner = DialogOwner };
        if (dialog.ShowDialog() != true || dialog.Value is null) return;

        await _session.CreateFolderAsync(dialog.Value);
        // Les mutations ne renvoient rien : on relit le dossier pour voir le résultat.
        await Task.Delay(300);
        await _session.ListAsync();
    }

    private async void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;

        var dialog = new TextPromptDialog("Renommer",
            $"Nouveau nom de « {row.Name} » :", row.Name, "Renommer") { Owner = DialogOwner };
        if (dialog.ShowDialog() != true || dialog.Value is null) return;
        if (dialog.Value == row.Name) return;

        await _session.RenameAsync(row.Name, dialog.Value);
        await Task.Delay(300);
        await _session.ListAsync();
    }

    private async void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;

        var hasDirectory = rows.Any(r => r.IsDirectory);
        var description = rows.Count == 1
            ? $"Supprimer « {rows[0].Name} » ?"
            : $"Supprimer ces {rows.Count} éléments ?";
        var detail = hasDirectory
            ? "Les dossiers seront supprimés avec tout leur contenu. Cette action est irréversible."
            : "Cette action est irréversible.";

        // Suppression définitive côté machine distante : confirmation explicite.
        if (MessageBox.Show(detail, description,
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        await _session.DeleteAsync(rows.Select(r => r.Name), recursive: hasDirectory);
        await Task.Delay(300);
        await _session.ListAsync();
    }

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        try { Clipboard.SetText(_session.FullPathOf(row.Entry)); } catch { }
    }

    private void FileMenu_Opened(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        var count = FileList.SelectedItems.Count;
        var downloadable = SelectedRows.Count(r => r.Entry.Kind != FileKind.Drive);

        MenuOpen.IsEnabled = count == 1;
        MenuOpen.Header = row?.IsDirectory == true ? "Ouvrir" : "Télécharger";

        MenuDownload.IsEnabled = downloadable > 0;
        MenuDownload.Header = downloadable > 1 ? $"Télécharger ces {downloadable} éléments…" : "Télécharger…";

        MenuRename.IsEnabled = count == 1;
        MenuDelete.IsEnabled = count >= 1;
        MenuCopyPath.IsEnabled = count == 1;
    }
}
