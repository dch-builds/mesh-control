using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MeshControl.Core;
using MeshControl.Files;

namespace MeshControl.Mac;

/// <summary>
/// Fichiers d'une machine — portage de Files/FilesWindow.xaml.cs (version
/// Windows) : navigation, file de transferts (un à la fois, l'agent n'en suit
/// qu'un par tunnel), envoi et téléchargement de dossiers entiers, double
/// volet avec ce poste, glisser-déposer depuis le Finder et d'un volet à l'autre.
/// </summary>
public partial class FilesWindow : ChromeWindow, ISessionWindow
{
    private readonly FilesSession _session;
    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;

    /// <summary>Glissers internes : fichiers distants vers ce poste, fichiers locaux vers la machine.</summary>
    private static readonly DataFormat<string> RemoteDragFormat = DataFormat.CreateInProcessFormat<string>("MeshControl.RemoteFiles");
    private static readonly DataFormat<string> LocalDragFormat = DataFormat.CreateInProcessFormat<string>("MeshControl.LocalFiles");

    public string NodeId => _node.Id;

    public FilesWindow(MeshServerConnection connection, MeshNode node)
    {
        _connection = connection;
        _node = node;
        _session = new FilesSession(connection, node, AvaloniaUiDispatcher.Instance);

        InitializeComponent();

        Title = $"{node.Name} — Fichiers";
        WindowTitleBar.Title = "Fichiers — Mesh Control";
        TitleText.Text = node.Name;
        RemotePaneTitleText.Text = node.Name;
        LocalMenuReveal.Header = OperatingSystem.IsMacOS() ? "Afficher dans le Finder" : "Afficher dans l'Explorateur";
        SetStatus("Connexion…", "#EFEFF4", "#8A8A8E");
        ShowEmptyState(IconKind.Folder, "Connexion à la machine…", "", loading: true);
        TransferList.ItemsSource = _transfers;
        UpdateBreadcrumb();

        new ListDragSource(LocalList, CreateLocalDragData);
        new ListDragSource(FileList, CreateRemoteDragData);

        _localPath = App.Settings.FilesLocalFolder is { } saved && Directory.Exists(saved)
            ? saved : DefaultLocalFolder();
        if (App.Settings.FilesDualPane) SetDualPane(true, remember: false);
        UpdateBridgeButtons();

        DragDrop.AddDragOverHandler(RootPanel, RootPanel_DragOver);
        DragDrop.AddDragLeaveHandler(RootPanel, (_, _) => HideDropOverlay());
        DragDrop.AddDropHandler(RootPanel, RootPanel_Drop);
        DragDrop.AddDragOverHandler(LocalPane, LocalPane_DragOver);
        DragDrop.AddDragLeaveHandler(LocalPane, (_, _) => LocalDropOverlay.IsVisible = false);
        DragDrop.AddDropHandler(LocalPane, LocalPane_Drop);

        _session.StateChanged += OnStateChanged;
        _session.EntriesChanged += OnEntriesChanged;
        _session.TransferChanged += OnTransferChanged;

        Opened += async (_, _) => await StartSessionAsync();
        Closed += (_, _) =>
        {
            CancelQueue();
            _session.Stop();
        };
    }

    // MARK: - Session

    private bool _started;

    /// <summary>Démarrage, par l'ouverture de la fenêtre ou par la fenêtre à onglets.</summary>
    public async Task StartSessionAsync()
    {
        if (_started) return;
        _started = true;
        await _session.StartAsync();
    }

    public void ReleaseKeyboard() { }
    public TitleBar SessionTitleBar => WindowTitleBar;
    public Control FocusTarget => FileList;

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
                ShowEmptyState(IconKind.Error, "Session fermée",
                    _session.CloseReason ?? "La connexion à la machine a pris fin.");
                FileList.ItemsSource = null;
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
            ShowEmptyState(IconKind.Warning, "Ce dossier n'a pas pu être lu",
                "Droits insuffisants, ou le chemin n'existe plus.");
        }
        else
        {
            if (_session.State == FilesSessionState.Ready)
                SetStatus(Plural(_session.Entries.Count, "élément", "éléments"), "#E4F8E8", "#248A3D");

            if (_session.Entries.Count == 0)
                ShowEmptyState(IconKind.Folder, "Ce dossier est vide",
                    CanUploadHereSilently() ? "Glissez des fichiers ou des dossiers ici pour les envoyer." : "");
            else
                EmptyState.IsVisible = false;
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
        StatusBadge.Background = Palette.Brush(background);
        StatusLabel.Foreground = Palette.Brush(foreground);
    }

    private void ShowEmptyState(IconKind glyph, string title, string hint, bool loading = false)
    {
        EmptyGlyph.Kind = glyph;
        EmptyTitle.Text = title;
        EmptyHint.Text = hint;
        EmptyHint.IsVisible = !string.IsNullOrEmpty(hint);
        EmptyProgress.IsVisible = loading;
        EmptyState.IsVisible = true;
    }

    private static string Plural(int count, string singular, string plural)
        => $"{count} {(count > 1 ? plural : singular)}";

    private Task InformAsync(string message)
        => Dialogs.InformAsync(Host, message);

    // MARK: - Navigation

    private FileRow? SelectedRow => FileList.SelectedItem as FileRow;
    private List<FileRow> SelectedRows => FileList.SelectedItems?.OfType<FileRow>().ToList() ?? new();

    private async void FileList_DoubleTapped(object? sender, TappedEventArgs e) => await OpenSelectionAsync();

    private async void FileList_KeyDown(object? sender, KeyEventArgs e)
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

    private async void Up_Click(object? sender, RoutedEventArgs e) => await _session.GoUpAsync();
    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await _session.ListAsync();
    private async void MenuOpen_Click(object? sender, RoutedEventArgs e) => await OpenSelectionAsync();

    /// <summary>Fil d'Ariane : chaque dossier du chemin est un bouton qui y ramène.</summary>
    private void UpdateBreadcrumb()
    {
        var components = _session.PathComponents;
        BuildBreadcrumb(BreadcrumbPanel, BreadcrumbScroller,
            _session.IsWindows ? "Cet ordinateur" : "/", _session.IsWindows ? IconKind.Computer : IconKind.Drive,
            components, index => _session.GoToAsync(index),
            index => string.Join("/", components.Take(index + 1)));
    }

    private void BuildBreadcrumb(Panel panel, ScrollViewer scroller, string rootLabel, IconKind rootGlyph,
                                 IReadOnlyList<string> components, Func<int, Task> navigate,
                                 Func<int, string> tooltip)
    {
        panel.Children.Clear();
        panel.Children.Add(Crumb(rootLabel, rootGlyph, components.Count == 0, null, () => navigate(-1)));
        for (var index = 0; index < components.Count; index++)
        {
            panel.Children.Add(new Icon
            {
                Kind = IconKind.ChevronRight,
                Size = 8,
                Foreground = Palette.Resource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, 1, 1, 0),
            });
            var target = index;
            panel.Children.Add(Crumb(components[index], IconKind.None, index == components.Count - 1,
                                     tooltip(index), () => navigate(target)));
        }
        Dispatcher.UIThread.Post(() => scroller.Offset = new Vector(double.MaxValue, 0), DispatcherPriority.Loaded);
    }

    private static Button Crumb(string label, IconKind glyph, bool isLast, string? tooltip, Func<Task> navigate)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph != IconKind.None)
            content.Children.Add(new Icon { Kind = glyph, Size = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button { Content = content };
        button.Classes.Add("crumb");
        if (isLast) button.Classes.Add("last");
        if (tooltip is not null) ToolTip.SetTip(button, tooltip);
        button.Click += async (_, _) => await navigate();
        return button;
    }

    // MARK: - File de transferts

    /// <summary>Transferts de la session : en cours, à venir et passés.</summary>
    private readonly ObservableCollection<TransferItem> _transfers = new();
    private bool _queueRunning;
    private TransferItem? _current;
    private bool _transfersExpanded = true;
    private readonly HashSet<string> _queueWrittenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ensuredDirectories = new(StringComparer.OrdinalIgnoreCase);

    private void Enqueue(IReadOnlyList<TransferItem> items)
    {
        if (items.Count == 0) return;
        // Nouveau lot, file au repos : les réussites du lot précédent s'effacent.
        if (!_queueRunning)
            foreach (var done in _transfers.Where(t => t.State == TransferState.Done).ToList())
                _transfers.Remove(done);
        foreach (var item in items) _transfers.Add(item);
        UpdateTransferPanel();
        if (!_queueRunning) _ = RunQueueAsync();
    }

    /// <summary>Un transfert après l'autre ; un échec n'arrête pas le lot.</summary>
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
                await _session.DownloadAsync(item.Entry!, item.RemotePath, item.LocalPath, cancellation.Token);
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

    private async Task ReportQueueResultAsync()
    {
        var current = NormalizeRemote(_session.CurrentPath);
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
            SetTransfersExpanded(true);
        }

        if (refresh && _session.State == FilesSessionState.Ready) await _session.ListAsync();
        if (_localWritten && _dualPane) await LoadLocalAsync(_localPath);
        _localWritten = false;
    }

    private static string NormalizeRemote(string path) => path.Replace('\\', '/').TrimEnd('/');

    private void CancelTransfer_Click(object? sender, RoutedEventArgs e) => CancelQueue();

    /// <summary>Annuler un lot, c'est l'arrêter entier, pas seulement le fichier courant.</summary>
    private void CancelQueue()
    {
        foreach (var item in _transfers.Where(t => t.State == TransferState.Pending)) item.Cancel();
        _current?.Cancellation?.Cancel();
        UpdateTransferPanel();
    }

    /// <summary>Action de la ligne : annuler, réessayer, ou montrer le fichier reçu.</summary>
    private void TransferAction_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TransferItem item) return;
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
                Reveal(item.LocalPath);
                break;
        }
        UpdateTransferPanel();
    }

    private void ClearFinished_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _transfers.Where(t => t.IsFinished).ToList()) _transfers.Remove(item);
        UpdateTransferPanel();
    }

    private void ToggleTransfers_Click(object? sender, RoutedEventArgs e) => SetTransfersExpanded(!_transfersExpanded);

    private void SetTransfersExpanded(bool expanded)
    {
        _transfersExpanded = expanded;
        TransferListHost.IsVisible = expanded;
        ToggleTransfersGlyph.Kind = expanded ? IconKind.ChevronDown : IconKind.ChevronUp;
        ToolTip.SetTip(ToggleTransfersButton, expanded ? "Masquer le détail" : "Afficher le détail");
    }

    /// <summary>Résumé du lot : où il en est, à quel débit, pour combien de temps encore.</summary>
    private void UpdateTransferPanel()
    {
        if (_transfers.Count == 0)
        {
            TransferPanel.IsVisible = false;
            return;
        }
        TransferPanel.IsVisible = true;

        var active = _transfers.Where(t => t.State != TransferState.Cancelled).ToList();
        var done = _transfers.Count(t => t.State == TransferState.Done);
        var failed = _transfers.Count(t => t.State == TransferState.Failed);
        var waiting = _transfers.Count(t => t.State is TransferState.Pending or TransferState.Running);

        long totalBytes = active.Sum(t => t.Size);
        long doneBytes = active.Sum(t => t.State == TransferState.Done ? t.Size
                                       : t.State == TransferState.Running ? t.Transferred : 0);

        TransferBar.Value = totalBytes > 0 ? (double)doneBytes / totalBytes
                          : active.Count > 0 ? (double)done / active.Count : 0;

        CancelTransferButton.IsVisible = _queueRunning;
        ClearFinishedButton.IsVisible = !_queueRunning;

        var uploads = _transfers.All(t => t.IsUpload);
        var downloads = _transfers.All(t => !t.IsUpload);

        if (_queueRunning)
        {
            var verb = uploads ? "Envoi" : downloads ? "Téléchargement" : "Transfert";
            var total = done + failed + waiting;
            TransferTitle.Text = $"{verb} en cours · {Math.Min(done + failed + 1, total)} sur {total}";
            var speed = _current?.BytesPerSecond ?? 0;
            var parts = new List<string> { $"{FileRow.FormatBytes(doneBytes)} sur {FileRow.FormatBytes(totalBytes)}" };
            if (FileRow.FormatSpeed(speed) is { Length: > 0 } rate) parts.Add(rate);
            if (FileRow.FormatRemaining(totalBytes - doneBytes, speed) is { Length: > 0 } eta) parts.Add(eta);
            TransferSubtitle.Text = string.Join(" · ", parts);
            SetTransferBadge(uploads || !downloads ? IconKind.Upload : IconKind.Download, "#E8F2FE", "#0A84FF");
            TransferBar.Foreground = Palette.Resource("AccentBrush");
        }
        else if (failed > 0)
        {
            TransferTitle.Text = $"{Plural(done, "fichier transféré", "fichiers transférés")}, {failed} en échec";
            TransferSubtitle.Text = "Survolez une ligne pour le détail, ou relancez-la avec ↻.";
            SetTransferBadge(IconKind.Warning, "#FFF4E5", "#B25E09");
            TransferBar.Foreground = Palette.Brush("#FF9F0A");
        }
        else if (done > 0)
        {
            TransferTitle.Text = uploads ? Plural(done, "fichier envoyé", "fichiers envoyés")
                               : downloads ? Plural(done, "fichier téléchargé", "fichiers téléchargés")
                               : Plural(done, "fichier transféré", "fichiers transférés");
            TransferSubtitle.Text = $"{FileRow.FormatBytes(totalBytes)} au total";
            SetTransferBadge(IconKind.Check, "#E4F8E8", "#248A3D");
            TransferBar.Foreground = Palette.Resource("OnlineBrush");
        }
        else
        {
            TransferTitle.Text = "Transferts annulés";
            TransferSubtitle.Text = "Relancez un fichier avec ↻, ou effacez la liste.";
            SetTransferBadge(IconKind.Close, "#EFEFF4", "#8A8A8E");
            TransferBar.Foreground = Palette.Resource("OfflineBrush");
        }
    }

    private void SetTransferBadge(IconKind glyph, string background, string foreground)
    {
        TransferHeaderGlyph.Kind = glyph;
        TransferHeaderGlyph.Foreground = Palette.Brush(foreground);
        TransferHeaderBadge.Background = Palette.Brush(background);
    }

    // MARK: - Envoi

    private async void Upload_Click(object? sender, RoutedEventArgs e)
    {
        if (!await CanUploadHereAsync()) return;
        var files = await Host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choisir les fichiers à envoyer",
            AllowMultiple = true,
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) EnqueueUploads(paths);
    }

    /// <summary>L'envoi exige un dossier réel : pas la racine Windows « Cet ordinateur ».</summary>
    private bool CanUploadHereSilently() => _session.PathComponents.Count > 0 || _node.OsFamily != OSFamily.Windows;

    private async Task<bool> CanUploadHereAsync()
    {
        if (CanUploadHereSilently()) return true;
        await InformAsync("Ouvrez d'abord un lecteur : on ne peut pas déposer un fichier à la racine de « Cet ordinateur ».");
        return false;
    }

    /// <summary>Envoi de fichiers et de dossiers (avec toute leur arborescence) vers le dossier affiché.</summary>
    private async void EnqueueUploads(IReadOnlyList<string> paths)
    {
        var target = _session.CurrentPath;
        var hasDirectories = paths.Any(Directory.Exists);
        if (hasDirectories) SetStatus("Préparation de l'envoi…", "#E8F2FE", "#0A84FF");

        var (items, unreadable) = await Task.Run(() => PlanUploads(paths, target));
        Enqueue(items);

        if (unreadable > 0)
            SetStatus(Plural(unreadable, "élément illisible ignoré", "éléments illisibles ignorés"), "#FFF4E5", "#B25E09");
        else if (items.Count == 0 && hasDirectories)
            SetStatus("Aucun fichier à envoyer dans ce dossier", "#EFEFF4", "#8A8A8E");
    }

    private (List<TransferItem> Items, int Unreadable) PlanUploads(IEnumerable<string> paths, string target)
    {
        var items = new List<TransferItem>();
        var unreadable = 0;
        // Fichiers cachés compris, mais ni fichiers système ni liens qui pourraient boucler.
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
                        // Métadonnées du Finder : rien à faire sur la machine distante.
                        if (file.Name == ".DS_Store") continue;
                        var remoteDirectory = remoteRoot;
                        var relative = Path.GetRelativePath(root.FullName, file.DirectoryName!);
                        if (relative != ".")
                            foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
                                remoteDirectory = _session.CombinePath(remoteDirectory, segment);
                        items.Add(TransferItem.Upload(file.FullName, remoteDirectory, file.Length, ensureRemoteDirectory: true));
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

    /// <summary>Envoi vers cette machine et d'autres : la destination proposée est le dossier affiché.</summary>
    private void SendToDevices_Click(object? sender, RoutedEventArgs e)
    {
        new SendFilesWindow(_connection, rememberDestinations: false, preselected: [_node],
            remotePathHint: CanUploadHereSilently() ? _session.CurrentPath : null).Show();
    }

    // MARK: - Téléchargement

    private const int MaxFolderDownloadFiles = 10_000;
    private const int MaxFolderDownloadDepth = 64;

    private async void Download_Click(object? sender, RoutedEventArgs e)
    {
        var selection = await DownloadableSelectionAsync();
        if (selection is null) return;

        // Double volet : le dossier d'arrivée est celui affiché à gauche.
        if (_dualPane && _localPath is not null)
        {
            await DownloadToFolderAsync(selection, _localPath);
            return;
        }

        // Un seul fichier : on laisse choisir son nom. Sinon : le dossier d'arrivée.
        if (selection is [{ IsDirectory: false } single])
        {
            var file = await Host.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Enregistrer le fichier",
                SuggestedFileName = SafeLocalName(single.Name),
            });
            if (file?.TryGetLocalPath() is not { } destination) return;
            Enqueue([TransferItem.Download(single.Entry, _session.FullPathOf(single.Entry), destination)]);
            return;
        }

        var folders = await Host.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choisir le dossier de destination",
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
        await DownloadToFolderAsync(selection, folder);
    }

    /// <summary>Sélection distante, lecteurs exclus ; null, après explication, s'il n'y a rien à télécharger.</summary>
    private async Task<List<FileRow>?> DownloadableSelectionAsync()
    {
        var selection = SelectedRows.Where(r => r.Entry.Kind != FileKind.Drive).ToList();
        if (selection.Count > 0) return selection;
        await InformAsync(SelectedRows.Count > 0
            ? "Un lecteur entier ne se télécharge pas : ouvrez-le et sélectionnez les fichiers ou dossiers voulus."
            : "Sélectionnez au moins un fichier ou un dossier à télécharger.");
        return null;
    }

    private async Task DownloadToFolderAsync(List<FileRow> selection, string folder)
    {
        // Doublons numérotés, comme le fait un navigateur.
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
                    await CollectRemoteFilesAsync(_session.FullPathOf(row.Entry), localRoot, items, depth: 0);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Préparation interrompue", "#FFF4E5", "#B25E09");
                await InformAsync(ex.Message);
                return;
            }
            SetStatus(Plural(_session.Entries.Count, "élément", "éléments"), "#E4F8E8", "#248A3D");
        }

        if (items.Count == 0)
        {
            await InformAsync("Les dossiers sélectionnés ne contiennent aucun fichier.");
            return;
        }
        Enqueue(items);
    }

    private async Task CollectRemoteFilesAsync(string remoteDirectory, string localDirectory,
                                               List<TransferItem> items, int depth)
    {
        if (depth > MaxFolderDownloadDepth)
            throw new MeshException($"Plus de {MaxFolderDownloadDepth} niveaux de dossiers : boucle probable, téléchargement interrompu.");

        var entries = await _session.ListDirectoryAsync(remoteDirectory)
                      ?? throw new MeshException($"Le dossier « {remoteDirectory} » n'a pas pu être lu.");

        // Deux noms qui ne diffèrent que par la casse ne doivent pas s'écraser.
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Name is "." or ".." || string.IsNullOrEmpty(entry.Name)) continue;
            var remotePath = _session.CombinePath(remoteDirectory, entry.Name);
            var localName = SafeLocalName(entry.Name);
            if (!usedNames.Add(localName))
            {
                var stem = Path.GetFileNameWithoutExtension(localName);
                var extension = Path.GetExtension(localName);
                for (var index = 2; !usedNames.Add(localName = $"{stem} ({index}){extension}"); index++) { }
            }
            var localPath = Path.Combine(localDirectory, localName);

            if (entry.Kind == FileKind.File)
            {
                items.Add(TransferItem.Download(entry, remotePath, localPath));
                if (items.Count > MaxFolderDownloadFiles)
                    throw new MeshException($"Plus de {MaxFolderDownloadFiles:N0} fichiers : choisissez des dossiers plus précis.");
            }
            else if (entry.Kind == FileKind.Directory)
            {
                await CollectRemoteFilesAsync(remotePath, localPath, items, depth + 1);
            }
        }
    }

    /// <summary>
    /// Nom utilisable localement : sans caractère refusé par le système (« : »
    /// sous Windows, « / » partout), ni nom de périphérique réservé de Windows.
    /// </summary>
    private static string SafeLocalName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat([':', '/', '\\']).ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        if (safe.Length == 0) return "_";
        var stem = safe.Split('.')[0].TrimEnd(' ');
        return ReservedDeviceNames.Contains(stem) ? "_" + safe : safe;
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

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
    /// <summary>Dossier du volet local ; null pour la liste des lecteurs / volumes.</summary>
    private string? _localPath;
    private bool _localLoaded;
    private bool _localWritten;
    private const double DualPaneWidth = 1260;

    private static string DefaultLocalFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        return Directory.Exists(downloads) ? downloads : profile;
    }

    private void DualPane_Click(object? sender, RoutedEventArgs e) => SetDualPane(!_dualPane);

    private void SetDualPane(bool dual, bool remember = true)
    {
        _dualPane = dual;
        LocalPaneHost.IsVisible = dual;
        BridgePanel.IsVisible = dual;
        RemotePaneTitle.IsVisible = dual;
        PanesGrid.ColumnDefinitions[0].Width = dual ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        PanesGrid.ColumnDefinitions[1].Width = dual ? new GridLength(64) : new GridLength(0);
        DualPaneButton.Classes.Set("header", !dual);
        DualPaneButton.Classes.Set("accentTool", dual);

        // Chaque volet n'a plus que la moitié de la place : colonnes resserrées.
        FileList.ItemTemplate = (IDataTemplate)Resources[dual ? "FileRowNarrow" : "FileRowWide"]!;
        RemoteHeader.ColumnDefinitions = new ColumnDefinitions(dual ? "*,80,125,110" : "*,110,150,150");

        if (dual) EnsureWidth();
        if (dual && !_localLoaded) Dispatcher.UIThread.Post(() => _ = LoadLocalAsync(_localPath));

        if (remember)
        {
            App.Settings.FilesDualPane = dual;
            App.Settings.Save();
        }
        UpdateBridgeButtons();
    }

    /// <summary>Élargit la fenêtre pour loger deux volets, sans déborder de l'écran.</summary>
    private void EnsureWidth()
    {
        var host = Host;
        if (host.WindowState != WindowState.Normal || host.Width >= DualPaneWidth) return;
        var screen = host.Screens.ScreenFromWindow(host)?.WorkingArea;
        var available = screen is { } area ? area.Width / (host.RenderScaling > 0 ? host.RenderScaling : 1) - 40 : DualPaneWidth;
        host.Width = Math.Min(DualPaneWidth, available);
    }

    private void UpdateBridgeButtons()
    {
        if (SendLocalButton is null) return;
        var ready = _session.State == FilesSessionState.Ready;
        SendLocalButton.IsEnabled = ready && CanUploadHereSilently() && LocalSelectedPaths().Count > 0;
        DownloadHereButton.IsEnabled = ready && _localPath is not null && SelectedRows.Any(r => r.Entry.Kind != FileKind.Drive);
    }

    private void PaneSelection_Changed(object? sender, SelectionChangedEventArgs e) => UpdateBridgeButtons();

    private async void SendLocal_Click(object? sender, RoutedEventArgs e)
    {
        var paths = LocalSelectedPaths();
        if (paths.Count == 0 || !await CanUploadHereAsync()) return;
        EnqueueUploads(paths);
    }

    private async void DownloadHere_Click(object? sender, RoutedEventArgs e)
    {
        if (_localPath is null)
        {
            await InformAsync("Ouvrez d'abord, à gauche, le dossier de ce poste où déposer les fichiers.");
            return;
        }
        if (await DownloadableSelectionAsync() is { } selection) await DownloadToFolderAsync(selection, _localPath);
    }

    // MARK: - Volet local

    private List<FileRow> LocalSelectedRows => LocalList.SelectedItems?.OfType<FileRow>().ToList() ?? new();

    /// <summary>À la racine, l'entrée porte déjà son chemin complet (lecteur, volume, dossier personnel).</summary>
    private string LocalPathOf(FileRow row) => _localPath is null ? row.Name : Path.Combine(_localPath, row.Name);

    private List<string> LocalSelectedPaths()
        => LocalSelectedRows.Where(r => r.Entry.Kind != FileKind.Drive || _localPath is null)
                            .Where(r => r.Entry.Kind != FileKind.Drive).Select(LocalPathOf).ToList();

    /// <summary>Lecture hors du fil d'interface : un volume réseau lent ne fige pas la fenêtre.</summary>
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
            if (!_localLoaded && path is not null) await LoadLocalAsync(null);
            return;
        }

        _localLoaded = true;
        _localPath = path;
        LocalList.ItemsSource = entries.Select(entry => new FileRow(entry)).ToList();
        LocalUpButton.IsEnabled = path is not null;
        UpdateLocalBreadcrumb();
        LocalEmptyText.Text = "Ce dossier est vide";
        LocalEmptyState.IsVisible = entries.Count == 0;

        if (path is not null)
        {
            App.Settings.FilesLocalFolder = path;
            App.Settings.Save();
        }
        UpdateBridgeButtons();
    }

    /// <summary>
    /// Contenu d'un dossier de ce poste, au format des entrées distantes. À la
    /// racine : les lecteurs sous Windows ; sous macOS, le dossier personnel,
    /// le disque de démarrage et les volumes montés. Fichiers cachés omis.
    /// </summary>
    private static List<FileEntry> ReadLocalDirectory(string? path)
    {
        if (path is null) return LocalRoots();

        var options = new EnumerationOptions { IgnoreInaccessible = true };
        var entries = new DirectoryInfo(path).EnumerateFileSystemInfos("*", options)
            .Where(info => !info.Name.StartsWith('.'))
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

    private static List<FileEntry> LocalRoots()
    {
        if (OperatingSystem.IsWindows())
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

        var roots = new List<FileEntry>
        {
            new() { Name = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Kind = FileKind.Directory },
            new() { Name = "/", Kind = FileKind.Drive, DriveType = "démarrage", FreeBytes = FreeSpace(new DriveInfo("/")) },
        };
        try
        {
            if (Directory.Exists("/Volumes"))
                foreach (var volume in new DirectoryInfo("/Volumes").EnumerateDirectories())
                    roots.Add(new FileEntry { Name = volume.FullName, Kind = FileKind.Drive, DriveType = "volume" });
        }
        catch { /* volumes illisibles : seule la base est proposée */ }
        return roots;
    }

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
            var rootLabel = root.TrimEnd('\\', '/');
            components.Add(rootLabel.Length > 0 ? rootLabel : "/");
            components.AddRange(path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries));
        }

        string PathAt(int index) => index == 0 ? root! : Path.Combine([root!, .. components.Skip(1).Take(index)]);

        var label = OperatingSystem.IsMacOS() ? "Ce Mac" : "Ce PC";
        BuildBreadcrumb(LocalBreadcrumbPanel, LocalBreadcrumbScroller, label, IconKind.Computer,
            components, index => LoadLocalAsync(index < 0 ? null : PathAt(index)), PathAt);
    }

    private async void LocalUp_Click(object? sender, RoutedEventArgs e) => await LocalGoUpAsync();
    private async void LocalRefresh_Click(object? sender, RoutedEventArgs e) => await LoadLocalAsync(_localPath);

    private Task LocalGoUpAsync()
        => _localPath is null ? Task.CompletedTask : LoadLocalAsync(Directory.GetParent(_localPath)?.FullName);

    private async void LocalList_DoubleTapped(object? sender, TappedEventArgs e) => await OpenLocalSelectionAsync();

    private async void LocalList_KeyDown(object? sender, KeyEventArgs e)
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
        try { await Launcher.LaunchFileInfoAsync(new FileInfo(LocalPathOf(row))); }
        catch (Exception ex) { await InformAsync(ex.Message); }
    }

    private async void LocalMenuOpen_Click(object? sender, RoutedEventArgs e) => await OpenLocalSelectionAsync();

    private void LocalMenuReveal_Click(object? sender, RoutedEventArgs e)
    {
        if (LocalList.SelectedItem is FileRow row) Reveal(LocalPathOf(row));
    }

    /// <summary>Montre un fichier dans le Finder (ou l'Explorateur), sélectionné.</summary>
    private static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", ["-R", path]);
            else if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else Process.Start("xdg-open", [Path.GetDirectoryName(path) ?? path]);
        }
        catch { /* gestionnaire de fichiers indisponible : sans conséquence */ }
    }

    private void LocalMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var sendable = LocalSelectedPaths().Count;
        LocalMenuSend.IsEnabled = sendable > 0 && _session.State == FilesSessionState.Ready && CanUploadHereSilently();
        LocalMenuSend.Header = sendable > 1 ? $"Envoyer ces {sendable} éléments vers la machine" : "Envoyer vers la machine";
        var count = LocalSelectedRows.Count;
        LocalMenuOpen.IsEnabled = count == 1;
        LocalMenuReveal.IsEnabled = count == 1 && _localPath is not null;
    }

    // MARK: - Glisser d'un volet à l'autre, et depuis le Finder

    private IDataTransfer? CreateLocalDragData()
    {
        var paths = LocalSelectedPaths();
        if (paths.Count == 0) return null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(LocalDragFormat, string.Join('\n', paths)));
        return data;
    }

    /// <summary>Fichiers distants glissés : seul le volet local sait les recevoir.</summary>
    private IDataTransfer? CreateRemoteDragData()
    {
        if (!_dualPane || !SelectedRows.Any(r => r.Entry.Kind != FileKind.Drive)) return null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(RemoteDragFormat, ""));
        return data;
    }

    private void LocalPane_DragOver(object? sender, DragEventArgs e)
    {
        HideDropOverlay();
        var acceptable = e.DataTransfer.Contains(RemoteDragFormat) && _localPath is not null;
        e.DragEffects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!acceptable) LocalDropOverlay.IsVisible = false;
        else if (!LocalDropOverlay.IsVisible)
        {
            LocalDropOverlayText.Text = $"dans « {_localPath} »";
            LocalDropOverlay.IsVisible = true;
        }
    }

    private async void LocalPane_Drop(object? sender, DragEventArgs e)
    {
        LocalDropOverlay.IsVisible = false;
        e.Handled = true;
        if (!e.DataTransfer.Contains(RemoteDragFormat) || _localPath is null) return;
        if (await DownloadableSelectionAsync() is { } selection) await DownloadToFolderAsync(selection, _localPath);
    }

    /// <summary>Fichiers du Finder ou du volet local : acceptés seulement là où l'écriture est possible.</summary>
    private void RootPanel_DragOver(object? sender, DragEventArgs e)
    {
        var acceptable = _session.State == FilesSessionState.Ready && CanUploadHereSilently()
                         && (e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(LocalDragFormat));
        e.DragEffects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (acceptable) ShowDropOverlay();
        else HideDropOverlay();
    }

    private async void RootPanel_Drop(object? sender, DragEventArgs e)
    {
        HideDropOverlay();
        e.Handled = true;

        var paths = new List<string>();
        if (e.DataTransfer.TryGetValue(LocalDragFormat) is { Length: > 0 } local)
            paths.AddRange(local.Split('\n'));
        else if (e.DataTransfer.TryGetFiles() is { } files)
            paths.AddRange(files.Select(f => f.TryGetLocalPath()).OfType<string>());

        if (paths.Count == 0 || !await CanUploadHereAsync()) return;
        EnqueueUploads(paths);
    }

    private void ShowDropOverlay()
    {
        if (DropOverlay.IsVisible) return;
        DropOverlayText.Text = $"dans « {_session.DisplayPath} » — les dossiers sont envoyés avec leur contenu";
        DropOverlay.IsVisible = true;
    }

    private void HideDropOverlay() => DropOverlay.IsVisible = false;

    // MARK: - Opérations

    private async void NewFolder_Click(object? sender, RoutedEventArgs e)
    {
        var name = await Dialogs.PromptAsync(Host, "Nouveau dossier", "Nom du nouveau dossier :", "Nouveau dossier", "Créer");
        if (string.IsNullOrWhiteSpace(name)) return;
        await _session.CreateFolderAsync(name);
        // Les mutations ne renvoient rien : on relit le dossier.
        await Task.Delay(300);
        await _session.ListAsync();
    }

    private async void MenuRename_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        var name = await Dialogs.PromptAsync(Host, "Renommer", $"Nouveau nom de « {row.Name} » :", row.Name, "Renommer");
        if (string.IsNullOrWhiteSpace(name) || name == row.Name) return;
        await _session.RenameAsync(row.Name, name);
        await Task.Delay(300);
        await _session.ListAsync();
    }

    private async void MenuDelete_Click(object? sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;
        var hasDirectory = rows.Any(r => r.IsDirectory);
        var description = rows.Count == 1 ? $"Supprimer « {rows[0].Name} » ?" : $"Supprimer ces {rows.Count} éléments ?";
        var detail = hasDirectory
            ? "Les dossiers seront supprimés avec tout leur contenu. Cette action est irréversible."
            : "Cette action est irréversible.";
        if (!await Dialogs.ConfirmAsync(Host, description, detail, "Supprimer")) return;

        await _session.DeleteAsync(rows.Select(r => r.Name), recursive: hasDirectory);
        await Task.Delay(300);
        await _session.ListAsync();
    }

    private async void MenuCopyPath_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row || Host.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(_session.FullPathOf(row.Entry)); } catch { }
    }

    private void FileMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var row = SelectedRow;
        var count = SelectedRows.Count;
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

/// <summary>
/// Glisser depuis une liste (portage de Files/ListDragSource.cs) : le glisser
/// part quand la souris s'éloigne de quelques pixels du point d'appui, bouton
/// enfoncé, sur une ligne.
/// </summary>
public sealed class ListDragSource
{
    private const double Threshold = 5;
    private readonly Func<IDataTransfer?> _createData;
    private PointerPressedEventArgs? _press;
    private Point _start;
    private bool _dragging;

    public ListDragSource(ListBox list, Func<IDataTransfer?> createData)
    {
        _createData = createData;
        list.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            var point = e.GetCurrentPoint(list);
            if (!point.Properties.IsLeftButtonPressed || !IsOnItem(e.Source)) return;
            _press = e;
            _start = point.Position;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        list.AddHandler(InputElement.PointerMovedEvent, async (_, e) =>
        {
            if (_press is null || _dragging) return;
            var point = e.GetCurrentPoint(list);
            if (!point.Properties.IsLeftButtonPressed)
            {
                _press = null;
                return;
            }
            if (Math.Abs(point.Position.X - _start.X) < Threshold && Math.Abs(point.Position.Y - _start.Y) < Threshold)
                return;

            var press = _press;
            _press = null;
            if (_createData() is not { } data) return;
            _dragging = true;
            try { await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy); }
            catch { /* glisser refusé par la plateforme : sans conséquence */ }
            finally { _dragging = false; }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        list.AddHandler(InputElement.PointerReleasedEvent, (_, _) => _press = null,
                        RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static bool IsOnItem(object? source)
    {
        for (var element = source as StyledElement; element is not null; element = element.Parent)
            if (element is ListBoxItem) return true;
        return false;
    }
}
