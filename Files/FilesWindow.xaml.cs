using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MeshControl.Core;
using MeshControl.UI;

namespace MeshControl.Files;

public partial class FilesWindow : Window, MeshControl.UI.ISessionWindow
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
    private readonly MeshNode _node;

    public FilesWindow(MeshServerConnection connection, MeshNode node)
    {
        _node = node;
        _session = new FilesSession(connection, node, Dispatcher);

        InitializeComponent();

        TitleText.Text = $"{node.Name} — Fichiers";
        SetStatus("Connexion…", "#EFEFF4", "#8A8A8E");

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
                if (!string.IsNullOrEmpty(_session.CloseReason))
                    MessageBox.Show(_session.CloseReason, "Mesh Control",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    private void OnEntriesChanged()
    {
        FileList.ItemsSource = _session.Entries.Select(e => new FileRow(e)).ToList();
        PathText.Text = _session.DisplayPath;
        UpButton.IsEnabled = _session.PathComponents.Count > 0;

        if (_session.LastError is { } error)
            SetStatus(error, "#FFF4E5", "#B25E09");
        else if (_session.State == FilesSessionState.Ready)
            SetStatus($"{_session.Entries.Count} élément(s)", "#E4F8E8", "#248A3D");
    }

    private void OnTransferChanged() => UpdateTransferPanel();

    private void SetStatus(string text, string background, string foreground)
    {
        StatusLabel.Text = text;
        StatusBadge.Background = MakeBrush(background);
        StatusLabel.Foreground = MakeBrush(foreground);
    }

    private static SolidColorBrush MakeBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

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

    // MARK: - File de transferts

    /// <summary>
    /// Un transfert demandé, en attente de son tour.
    ///
    /// Le chemin distant est figé à la mise en file : l'utilisateur peut changer
    /// de dossier pendant qu'un lot s'envoie, et les fichiers doivent malgré tout
    /// arriver là où il les a déposés.
    /// </summary>
    private sealed record PendingTransfer(
        bool IsUpload, string LocalPath, string RemotePath, FileEntry? Entry)
    {
        /// <summary>Nom à citer dans un compte rendu d'erreur.</summary>
        public string DisplayName => IsUpload ? Path.GetFileName(LocalPath) : Entry!.Name;
    }

    /// <summary>
    /// Taille maximale d'un envoi. L'envoi charge le fichier entier en mémoire
    /// avant de le découper en fragments : au-delà, l'échec serait certain et sa
    /// cause illisible. Un fichier plus gros passe par un partage réseau.
    /// </summary>
    private const long MaxUploadBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// Transferts en attente. Le protocole n'en accepte qu'un à la fois (l'agent
    /// suit un seul envoi par tunnel) : la file les enchaîne sans que
    /// l'utilisateur ait à attendre devant chaque fichier.
    /// </summary>
    private readonly Queue<PendingTransfer> _queue = new();

    private CancellationTokenSource? _queueCts;
    private bool _queueRunning;

    /// <summary>
    /// Dossiers distants où le lot a écrit. Sert à ne relire la liste que si
    /// c'est le dossier affiché — l'agent ne signale pas ses propres écritures.
    /// </summary>
    private readonly HashSet<string> _queueWrittenPaths = new(StringComparer.OrdinalIgnoreCase);

    private void Enqueue(IReadOnlyList<PendingTransfer> transfers)
    {
        if (transfers.Count == 0) return;

        foreach (var transfer in transfers) _queue.Enqueue(transfer);
        UpdateTransferPanel();

        if (!_queueRunning) _ = RunQueueAsync();
    }

    /// <summary>
    /// Traite la file, un transfert après l'autre. Un échec n'arrête pas le lot :
    /// il est relevé et le fichier suivant part — sinon un seul fichier verrouillé
    /// annulerait tout le reste.
    /// </summary>
    private async Task RunQueueAsync()
    {
        _queueRunning = true;
        _queueCts = new CancellationTokenSource();

        var done = 0;
        var failures = new List<string>();
        var cancelled = false;

        try
        {
            while (_queue.Count > 0)
            {
                if (_queueCts.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var transfer = _queue.Dequeue();
                try
                {
                    if (transfer.IsUpload)
                    {
                        await _session.UploadAsync(transfer.LocalPath, transfer.RemotePath,
                                                   _queueCts.Token);
                        _queueWrittenPaths.Add(transfer.RemotePath);
                    }
                    else
                    {
                        await _session.DownloadAsync(transfer.Entry!, transfer.RemotePath,
                                                     transfer.LocalPath, _queueCts.Token);
                    }
                    done++;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    failures.Add($"{transfer.DisplayName} : {ex.Message}");
                }

                UpdateTransferPanel();
            }
        }
        finally
        {
            _queue.Clear();
            _queueCts?.Dispose();
            _queueCts = null;
            _queueRunning = false;
            UpdateTransferPanel();
        }

        await ReportQueueResultAsync(done, failures, cancelled);
    }

    /// <summary>
    /// Compte rendu du lot, puis relecture du dossier si le lot y a écrit.
    /// </summary>
    private async Task ReportQueueResultAsync(int done, List<string> failures, bool cancelled)
    {
        var refresh = _queueWrittenPaths.Contains(_session.CurrentPath);
        _queueWrittenPaths.Clear();

        if (cancelled)
            SetStatus(done == 0
                          ? "Transfert annulé."
                          : $"Transfert interrompu après {done} fichier(s).",
                      "#EFEFF4", "#8A8A8E");
        else if (failures.Count == 0)
            SetStatus(done == 1 ? "1 fichier transféré." : $"{done} fichiers transférés.",
                      "#E4F8E8", "#248A3D");
        else
            SetStatus($"{done} transféré(s), {failures.Count} en échec.", "#FFF4E5", "#B25E09");

        // Relecture avant la boîte de dialogue : l'utilisateur doit retrouver la
        // liste à jour en la refermant.
        if (refresh && _session.State == FilesSessionState.Ready) await _session.ListAsync();

        if (failures.Count > 0)
        {
            // Au-delà de quelques lignes, la liste devient illisible : le reste
            // est résumé, le journal garde la trace complète.
            const int shown = 8;
            var detail = string.Join("\n", failures.Take(shown));
            if (failures.Count > shown) detail += $"\n… et {failures.Count - shown} autre(s).";

            foreach (var failure in failures) Log.Warning($"Transfert : {failure}", "Fichiers");

            MessageBox.Show($"Certains transferts ont échoué :\n\n{detail}", "Mesh Control",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e) => CancelQueue();

    /// <summary>
    /// Interrompt le transfert en cours et vide la file : quand on annule un lot,
    /// c'est le lot entier que l'on veut arrêter, pas seulement le fichier courant.
    /// </summary>
    private void CancelQueue()
    {
        _queue.Clear();
        _queueCts?.Cancel();
        _session.CancelTransfer();
        UpdateTransferPanel();
    }

    /// <summary>
    /// Panneau de progression : le transfert en cours, et ce qui attend derrière.
    /// </summary>
    private void UpdateTransferPanel()
    {
        var transfer = _session.Transfer;
        var waiting = _queue.Count;

        if (transfer is null && waiting == 0)
        {
            TransferPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TransferPanel.Visibility = Visibility.Visible;
        CancelTransferButton.Content = waiting > 0 ? "Tout annuler" : "Annuler";

        if (transfer is null)
        {
            // Entre deux fichiers : la barre ne représente rien de mesurable.
            TransferBar.IsIndeterminate = true;
            TransferLabel.Text = waiting == 1
                ? "1 fichier en attente…"
                : $"{waiting} fichiers en attente…";
            return;
        }

        TransferBar.IsIndeterminate = false;
        TransferBar.Value = transfer.Fraction;

        var verb = transfer.IsUpload ? "Envoi" : "Téléchargement";
        var label = $"{verb} de « {transfer.FileName} » — "
            + $"{FileRow.FormatBytes(transfer.Transferred)} / {FileRow.FormatBytes(transfer.Total)}";
        TransferLabel.Text = waiting > 0 ? $"{label} · {waiting} en attente" : label;
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
    /// Met en file l'envoi de ces fichiers vers le dossier affiché. Ce qui n'est
    /// pas envoyable est écarté puis expliqué, plutôt qu'ignoré en silence.
    /// </summary>
    private void EnqueueUploads(IEnumerable<string> paths)
    {
        var target = _session.CurrentPath;
        var transfers = new List<PendingTransfer>();
        var directories = 0;
        var tooLarge = new List<string>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                // Le protocole envoie des fichiers, un par un : il n'a pas de
                // notion d'arborescence, et créer les dossiers distants un à un
                // sans acquittement serait une course perdue d'avance.
                directories++;
                continue;
            }
            if (!File.Exists(path)) continue;

            long size;
            try { size = new FileInfo(path).Length; }
            catch { continue; }

            if (size > MaxUploadBytes)
            {
                tooLarge.Add(Path.GetFileName(path));
                continue;
            }

            transfers.Add(new PendingTransfer(true, path, target, null));
        }

        Enqueue(transfers);
        ExplainSkipped(directories, tooLarge);
    }

    /// <summary>Explique ce qui n'a pas été mis en file.</summary>
    private void ExplainSkipped(int directories, List<string> tooLarge)
    {
        var reasons = new List<string>();

        if (directories > 0)
            reasons.Add(directories == 1
                ? "Un dossier a été ignoré : seuls des fichiers peuvent être envoyés."
                : $"{directories} dossiers ont été ignorés : seuls des fichiers peuvent être envoyés.");

        if (tooLarge.Count > 0)
            reasons.Add($"Au-delà de {FileRow.FormatBytes(MaxUploadBytes)}, l'envoi n'est pas "
                      + "possible depuis cette application : "
                      + string.Join(", ", tooLarge) + ".");

        if (reasons.Count == 0) return;

        MessageBox.Show(string.Join("\n\n", reasons), "Mesh Control",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // MARK: - Téléchargement

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        var selection = SelectedRows;
        var files = selection.Where(r => !r.IsDirectory).ToList();

        if (files.Count == 0)
        {
            MessageBox.Show(
                selection.Count > 0
                    ? "Un dossier ne se télécharge pas d'un bloc : ouvrez-le et sélectionnez "
                      + "les fichiers voulus."
                    : "Sélectionnez au moins un fichier à télécharger.",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Un seul fichier : on laisse choisir son nom, comme tout « Enregistrer
        // sous ». Plusieurs : on demande le dossier d'arrivée.
        if (files.Count == 1)
        {
            var dialog = new SaveFileDialog
            {
                FileName = files[0].Name,
                Title = "Enregistrer le fichier",
            };
            if (dialog.ShowDialog(DialogOwner) != true) return;

            Enqueue([Download(files[0], dialog.FileName)]);
            return;
        }

        var folder = new OpenFolderDialog { Title = "Choisir le dossier de destination" };
        if (folder.ShowDialog(DialogOwner) != true) return;

        // Sur un lot, on ne peut pas poser une question par fichier déjà
        // présent : les doublons sont numérotés, comme le fait un navigateur.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var transfers = files
            .Select(row => Download(row, UniqueLocalPath(folder.FolderName, row.Name, reserved)))
            .ToList();

        Enqueue(transfers);
    }

    /// <summary>
    /// Prépare un téléchargement. Le chemin distant est résolu maintenant : la
    /// file peut tourner alors que l'utilisateur a déjà changé de dossier.
    /// </summary>
    private PendingTransfer Download(FileRow row, string destination)
        => new(false, destination, _session.FullPathOf(row.Entry), row.Entry);

    /// <summary>Chemin local encore libre pour ce nom de fichier.</summary>
    private static string UniqueLocalPath(string directory, string name, HashSet<string> reserved)
    {
        var candidate = Path.Combine(directory, name);
        if (!File.Exists(candidate) && reserved.Add(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 2; ; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && reserved.Add(candidate)) return candidate;
        }
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

        DropOverlayText.Text = $"Déposer pour envoyer dans « {_session.DisplayPath} »";
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
        var files = SelectedRows.Count(r => !r.IsDirectory);

        MenuOpen.IsEnabled = count == 1;
        MenuOpen.Header = row?.IsDirectory == true ? "Ouvrir" : "Télécharger";

        MenuDownload.IsEnabled = files > 0;
        MenuDownload.Header = files > 1 ? $"Télécharger ces {files} fichiers…" : "Télécharger…";

        MenuRename.IsEnabled = count == 1;
        MenuDelete.IsEnabled = count >= 1;
        MenuCopyPath.IsEnabled = count == 1;
    }
}
