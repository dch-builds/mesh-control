using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MeshControl.Core;

namespace MeshControl.Files;

/// <summary>
/// Envoi de fichiers vers une ou plusieurs machines à la fois.
///
/// Chaque machine reçoit sa propre session de fichiers : le protocole n'accepte
/// qu'un envoi à la fois par tunnel, mais rien n'empêche d'ouvrir un tunnel par
/// machine. Les envois partent donc en parallèle, dans une limite qui évite
/// d'ouvrir des dizaines de tunnels d'un coup sur un grand parc.
/// </summary>
public partial class SendFilesWindow : MeshControl.UI.ChromeWindow
{
    /// <summary>
    /// Machines servies simultanément. Au-delà, la bande passante montante du
    /// poste se partage sans rien faire gagner, et le serveur relaie d'autant
    /// plus de tunnels.
    /// </summary>
    private const int MaxParallelDevices = 4;

    private const string DefaultWindowsFolder = @"C:\Users\Public\Downloads";
    private const string DefaultUnixFolder = "/tmp";

    private readonly MeshServerConnection _connection;
    private readonly AppSettings? _settings;

    private readonly ObservableCollection<OutgoingEntry> _entries = new();
    private readonly List<TargetDevice> _devices;
    private readonly ICollectionView _deviceView;

    // État de l'envoi
    private List<TargetDevice> _targets = new();
    private List<OutgoingFile> _files = new();
    private bool _createFolders = true;
    private CancellationTokenSource? _cancellation;
    private readonly SemaphoreSlim _gate = new(MaxParallelDevices);
    private int _running;
    private bool _preparing;

    /// <param name="settings">
    /// Réglages où retenir les dernières destinations. Facultatif : ouverte
    /// depuis une fenêtre de fichiers, la destination vient du dossier affiché.
    /// </param>
    /// <param name="preselected">Machines cochées à l'ouverture, placées en tête.</param>
    /// <param name="files">Fichiers ou dossiers déjà choisis (glissés sur une machine…).</param>
    /// <param name="remotePathHint">
    /// Dossier distant proposé comme destination pour la famille de système de
    /// la machine présélectionnée.
    /// </param>
    public SendFilesWindow(MeshServerConnection connection, AppSettings? settings,
                           IEnumerable<MeshNode>? preselected = null,
                           IEnumerable<string>? files = null,
                           string? remotePathHint = null)
    {
        _connection = connection;
        _settings = settings;

        InitializeComponent();

        var preselectedNodes = preselected?.ToList() ?? [];
        var preselectedIds = preselectedNodes.Select(n => n.Id).ToHashSet();

        // Seules les machines joignables par leur agent peuvent recevoir un
        // fichier : les autres ne feraient qu'échouer à la connexion.
        var groups = connection.Meshes.ToDictionary(m => m.Id, m => m.Name);
        var nodes = connection.Nodes.Values.ToList();
        _devices = nodes
            .Where(n => n is { IsOnline: true, HasAgent: true })
            .Select(n => new TargetDevice(n, groups.GetValueOrDefault(n.MeshId, "Sans dossier")))
            .OrderByDescending(d => preselectedIds.Contains(d.Node.Id))
            .ThenBy(d => d.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var device in _devices)
        {
            device.IsSelected = preselectedIds.Contains(device.Node.Id);
            device.SelectionChanged += UpdateSummary;
        }

        var unreachable = nodes.Count - _devices.Count;
        OfflineHint.Text = unreachable > 0 ? $"{unreachable} hors ligne non listé{(unreachable > 1 ? "s" : "")}" : "";

        _deviceView = CollectionViewSource.GetDefaultView(_devices);
        _deviceView.Filter = MatchesSearch;
        DeviceListBox.ItemsSource = _deviceView;
        FileListBox.ItemsSource = _entries;

        WindowsPathBox.Text = string.IsNullOrWhiteSpace(settings?.TransferWindowsFolder)
            ? DefaultWindowsFolder : settings!.TransferWindowsFolder;
        UnixPathBox.Text = string.IsNullOrWhiteSpace(settings?.TransferUnixFolder)
            ? DefaultUnixFolder : settings!.TransferUnixFolder;

        if (remotePathHint is { Length: > 0 } && preselectedNodes is [var origin])
        {
            if (origin.OsFamily == OSFamily.Windows) WindowsPathBox.Text = ToWindowsPath(remotePathHint);
            else UnixPathBox.Text = remotePathHint;
        }

        Closing += OnClosing;
        UpdateSummary();

        if (files is not null) _ = AddPathsAsync(files.ToList());
    }

    /// <summary>
    /// Chemin distant tel que la fenêtre de fichiers le compose (« C:/Users/x »),
    /// réécrit dans la forme familière sous Windows.
    /// </summary>
    private static string ToWindowsPath(string path)
    {
        var windows = path.Replace('/', '\\');
        return windows.Length == 2 && windows[1] == ':' ? windows + "\\" : windows;
    }

    private static string Plural(int count, string singular, string plural)
        => $"{count} {(count > 1 ? plural : singular)}";

    private static SolidColorBrush MakeBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(MeshControl.UI.Palette.Adapt(hex)));

    // MARK: - Étape 1 : les fichiers

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choisir les fichiers à envoyer", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        _ = AddPathsAsync(dialog.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choisir les dossiers à envoyer", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        _ = AddPathsAsync(dialog.FolderNames);
    }

    /// <summary>
    /// Ajoute des fichiers ou dossiers à l'envoi. Un dossier est parcouru hors
    /// du fil d'interface : un gros arbre prendrait sinon la fenêtre en otage.
    /// </summary>
    private async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        var known = _entries.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = paths.Where(p => !known.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (fresh.Count == 0) return;

        _preparing = true;
        UpdateSummary();

        var prepared = await Task.Run(() => fresh.Select(OutgoingEntry.From).ToList());

        _preparing = false;
        foreach (var entry in prepared.OfType<OutgoingEntry>()) _entries.Add(entry);
        UpdateSummary();

        var unreadable = prepared.Count(e => e is null);
        if (unreadable > 0)
            MessageBox.Show(this, unreadable == 1
                    ? "Un élément n'a pas pu être lu et a été écarté."
                    : $"{unreadable} éléments n'ont pas pu être lus et ont été écartés.",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is OutgoingEntry entry) _entries.Remove(entry);
        UpdateSummary();
    }

    private void ClearFiles_Click(object sender, RoutedEventArgs e)
    {
        _entries.Clear();
        UpdateSummary();
    }

    // MARK: - Étape 2 : les appareils

    private bool MatchesSearch(object item)
    {
        var query = DeviceSearchBox.Text.Trim();
        if (query.Length == 0 || item is not TargetDevice device) return true;
        return device.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || device.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase)
               || (device.Node.OsDescription?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void DeviceSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        DeviceSearchPlaceholder.Visibility = DeviceSearchBox.Text.Length == 0
            ? Visibility.Visible : Visibility.Collapsed;
        _deviceView.Refresh();
        UpdateSummary();
    }

    /// <summary>Un clic n'importe où sur la ligne coche ou décoche la machine.</summary>
    private void DeviceList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(DeviceListBox, (DependencyObject)e.OriginalSource)
                is ListBoxItem { DataContext: TargetDevice device })
        {
            device.IsSelected = !device.IsSelected;
            e.Handled = true;
        }
    }

    /// <summary>« Tout » vise ce qui est affiché : après une recherche, le résultat de la recherche.</summary>
    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in _deviceView.Cast<TargetDevice>()) device.IsSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in _devices) device.IsSelected = false;
    }

    // MARK: - Étape 3 : la destination

    private void Destination_TextChanged(object sender, TextChangedEventArgs e) => UpdateSummary();

    /// <summary>
    /// Résumé des trois étapes, et disponibilité du bouton d'envoi. N'affiche
    /// que les champs de destination des systèmes effectivement visés.
    /// </summary>
    private void UpdateSummary()
    {
        if (SendButton is null) return;   // pendant InitializeComponent

        var fileCount = _entries.Sum(e => e.Files.Count);
        var size = _entries.Sum(e => e.TotalSize);
        FilesSummary.Text = _preparing ? "Préparation…"
                          : fileCount == 0 ? ""
                          : $"{Plural(fileCount, "fichier", "fichiers")} · {FileRow.FormatBytes(size)}";
        FilesDropZone.Visibility = _entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FileListBox.Visibility = _entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ClearFilesButton.Visibility = _entries.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        var selected = _devices.Where(d => d.IsSelected).ToList();
        DevicesSummary.Text = selected.Count == 0 ? "Aucun sélectionné"
                            : $"{Plural(selected.Count, "sélectionné", "sélectionnés")}";

        if (_devices.Count == 0)
        {
            NoDeviceText.Text = "Aucune machine en ligne avec un agent : il n'y a pour l'instant "
                              + "aucun destinataire possible.";
            NoDeviceText.Visibility = Visibility.Visible;
        }
        else if (_deviceView.IsEmpty)
        {
            NoDeviceText.Text = "Aucun appareil ne correspond à la recherche.";
            NoDeviceText.Visibility = Visibility.Visible;
        }
        else
        {
            NoDeviceText.Visibility = Visibility.Collapsed;
        }

        // Sans sélection, les deux champs restent visibles : on ne sait pas encore
        // lequel servira.
        var anyWindows = selected.Any(d => d.IsWindows);
        var anyUnix = selected.Any(d => !d.IsWindows);
        WindowsDestinationRow.Visibility = anyWindows || selected.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        UnixDestinationRow.Visibility = anyUnix || selected.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        var destinationsFilled = (!anyWindows || WindowsPathBox.Text.Trim().Length > 0)
                                 && (!anyUnix || UnixPathBox.Text.Trim().Length > 0);

        SendButton.IsEnabled = fileCount > 0 && selected.Count > 0 && destinationsFilled && !_preparing;
        SendButtonText.Text = selected.Count > 1 ? $"Envoyer vers {selected.Count} appareils" : "Envoyer";
    }

    // MARK: - Envoi

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var selected = _devices.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0 || _entries.Count == 0) return;

        var windowsRoot = WindowsPathBox.Text.Trim();
        var unixRoot = UnixPathBox.Text.Trim();

        // Un chemin relatif dépendrait du dossier courant de l'agent, que
        // personne ne connaît : on exige un chemin absolu.
        if (selected.Any(d => d.IsWindows)
            && !(windowsRoot.Length >= 2 && windowsRoot[1] == ':') && !windowsRoot.StartsWith(@"\\"))
        {
            MessageBox.Show(this, "La destination Windows doit être un chemin complet, par exemple "
                                + $"« {DefaultWindowsFolder} ».",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
            WindowsPathBox.Focus();
            return;
        }
        if (selected.Any(d => !d.IsWindows) && !unixRoot.StartsWith('/'))
        {
            MessageBox.Show(this, "La destination Linux / macOS doit commencer par « / », par exemple "
                                + $"« {DefaultUnixFolder} ».",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
            UnixPathBox.Focus();
            return;
        }

        if (_settings is not null)
        {
            _settings.TransferWindowsFolder = windowsRoot;
            _settings.TransferUnixFolder = unixRoot;
            _settings.Save();
        }

        _files = _entries.SelectMany(entry => entry.Files).ToList();
        _targets = selected;
        _createFolders = CreateFolderCheck.IsChecked == true;
        foreach (var target in _targets)
        {
            target.Delivered.Clear();
            target.Destination = target.IsWindows ? windowsRoot : unixRoot;
            target.Prepare(_files);
        }

        ShowProgressView(true);
        DeviceCards.ItemsSource = _targets;
        await RunAsync(_targets);
    }

    /// <summary>Lance (ou relance) l'envoi vers ces machines, en parallèle.</summary>
    private async Task RunAsync(IReadOnlyList<TargetDevice> targets)
    {
        // Une annulation précédente ne doit pas couper la relance.
        if (_cancellation is null || _cancellation.IsCancellationRequested)
        {
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
        }
        var token = _cancellation.Token;

        foreach (var target in targets) target.Prepare(_files);

        // Les tâches sont lancées avant le premier bilan : chacune se compte
        // parmi les envois en cours dès son départ, sans quoi l'en-tête
        // annoncerait « terminé » le temps des connexions.
        var sends = targets.Select(target => SendToDeviceAsync(target, token)).ToList();
        UpdateProgressView();

        await Task.WhenAll(sends);
        UpdateProgressView();
    }

    private async Task SendToDeviceAsync(TargetDevice target, CancellationToken ct)
    {
        _running++;
        try
        {
            try
            {
                await _gate.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                target.Cancel();
                return;
            }

            try
            {
                await SendWithSessionAsync(target, ct);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _running--;
            UpdateProgressView();
        }
    }

    private async Task SendWithSessionAsync(TargetDevice target, CancellationToken ct)
    {
        var session = new FilesSession(_connection, target.Node, Dispatcher);

        void OnProgress()
        {
            if (session.Transfer is { } progress) target.Report(progress);
            UpdateProgressView();
        }
        session.TransferChanged += OnProgress;

        try
        {
            target.Connecting();
            await session.StartAsync(listOnStart: false);
            if (session.State != FilesSessionState.Ready)
                throw new MeshException(session.CloseReason ?? "Connexion à la machine impossible.");
            ct.ThrowIfCancellationRequested();

            var root = target.Destination;
            if (_createFolders)
                await session.EnsureDirectoryAsync(root, ct);
            else if (await session.ListDirectoryAsync(root, ct) is null)
                throw new MeshException($"Le dossier « {root} » n'existe pas sur cette machine.");

            // Chaque sous-dossier n'est vérifié qu'une fois par machine.
            var ensured = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };

            foreach (var file in _files)
            {
                if (target.Delivered.Contains(file)) continue;
                ct.ThrowIfCancellationRequested();

                var directory = root;
                foreach (var segment in file.RelativeDirectory)
                    directory = session.CombinePath(directory, segment);
                if (ensured.Add(directory)) await session.EnsureDirectoryAsync(directory, ct);

                target.BeginFile(file);
                await session.UploadAsync(file.LocalPath, directory, ct);
                target.EndFile(file);
            }

            target.Complete();
            Log.Info($"{Plural(_files.Count, "fichier envoyé", "fichiers envoyés")} à {target.Name} "
                   + $"({target.Destination})", "Fichiers");
        }
        catch (OperationCanceledException)
        {
            target.Cancel();
        }
        catch (Exception ex)
        {
            target.Fail(ex.Message);
            Log.Warning($"Envoi vers {target.Name} : {ex.Message}", "Fichiers");
        }
        finally
        {
            session.TransferChanged -= OnProgress;
            session.Stop();
        }
    }

    // MARK: - Suivi

    private void ShowProgressView(bool progress)
    {
        ComposePanel.Visibility = progress ? Visibility.Collapsed : Visibility.Visible;
        ComposeFooter.Visibility = progress ? Visibility.Collapsed : Visibility.Visible;
        ProgressPanel.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
        ProgressFooter.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Bilan d'ensemble : progression cumulée de toutes les machines, débit
    /// total et temps restant.
    /// </summary>
    private void UpdateProgressView()
    {
        if (_targets.Count == 0) return;

        var done = _targets.Count(t => t.State == DeviceSendState.Done);
        var toRetry = _targets.Count(t => t.State is DeviceSendState.Failed or DeviceSendState.Cancelled);

        long total = _targets.Sum(t => t.BytesTotal);
        long sent = _targets.Sum(t => t.State == DeviceSendState.Done ? t.BytesTotal : t.BytesDone);
        var fraction = total > 0 ? Math.Min(1, (double)sent / total) : (double)done / _targets.Count;
        var speed = _targets.Sum(t => t.BytesPerSecond);

        OverallBar.Value = fraction;
        OverallPercent.Text = $"{Math.Floor(fraction * 100)} %";

        var devices = Plural(_targets.Count, "appareil", "appareils");
        if (_running > 0)
        {
            OverallTitle.Text = $"Envoi vers {devices}";
            var parts = new List<string>
            {
                $"{done} terminé{(done > 1 ? "s" : "")} sur {_targets.Count}",
                $"{FileRow.FormatBytes(sent)} sur {FileRow.FormatBytes(total)}",
            };
            if (FileRow.FormatSpeed(speed) is { Length: > 0 } rate) parts.Add(rate);
            if (FileRow.FormatRemaining(total - sent, speed) is { Length: > 0 } eta) parts.Add(eta);
            OverallSubtitle.Text = string.Join(" · ", parts);
            SetOverallBadge("\uE898", "#E8F2FE", "#0A84FF");
        }
        else if (toRetry == 0)
        {
            OverallTitle.Text = "Envoi terminé";
            OverallSubtitle.Text = $"{Plural(_files.Count, "fichier livré", "fichiers livrés")} "
                                 + $"sur {devices} · {FileRow.FormatBytes(total)}";
            SetOverallBadge("\uE73E", "#E4F8E8", "#248A3D");
        }
        else
        {
            OverallTitle.Text = done == 0
                ? "L'envoi n'a pas abouti"
                : $"{done} réussi{(done > 1 ? "s" : "")}, {toRetry} à reprendre";
            OverallSubtitle.Text = "Les fichiers déjà reçus ne sont pas renvoyés lors d'une nouvelle tentative.";
            SetOverallBadge("\uE7BA", "#FFF4E5", "#B25E09");
        }

        RetryAllButton.Visibility = _running == 0 && toRetry > 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelAllButton.Visibility = _running > 0 ? Visibility.Visible : Visibility.Collapsed;
        NewBatchButton.IsEnabled = _running == 0;
    }

    private void SetOverallBadge(string glyph, string background, string foreground)
    {
        OverallGlyph.Text = glyph;
        OverallGlyph.Foreground = MakeBrush(foreground);
        OverallBadge.Background = MakeBrush(background);
        OverallPercent.Foreground = MakeBrush(foreground);
        OverallBar.Foreground = MakeBrush(foreground == "#0A84FF" ? "#0A84FF"
                                        : foreground == "#248A3D" ? "#34C759" : "#FF9F0A");
    }

    private async void RetryDevice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TargetDevice target && !IsRunning(target))
            await RunAsync([target]);
    }

    private async void RetryAll_Click(object sender, RoutedEventArgs e)
    {
        var targets = _targets.Where(t => t.State is DeviceSendState.Failed or DeviceSendState.Cancelled)
                              .ToList();
        if (targets.Count > 0) await RunAsync(targets);
    }

    private static bool IsRunning(TargetDevice target)
        => target.State is DeviceSendState.Connecting or DeviceSendState.Sending
                        or DeviceSendState.Waiting;

    private void CancelAll_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    /// <summary>Retour à la composition, fichiers et sélection conservés.</summary>
    private void NewBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_running > 0) return;
        _targets = new List<TargetDevice>();
        DeviceCards.ItemsSource = null;
        ShowProgressView(false);
        UpdateSummary();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_running == 0) return;

        var answer = MessageBox.Show(this,
            "Des envois sont encore en cours. Les interrompre et fermer la fenêtre ?",
            "Mesh Control", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }
        _cancellation?.Cancel();
    }

    // MARK: - Glisser-déposer

    private bool IsComposing => ComposePanel.Visibility == Visibility.Visible;

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        var acceptable = IsComposing && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropOverlay.Visibility = acceptable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Root_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private void Root_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        if (!IsComposing) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            _ = AddPathsAsync(paths);
    }
}
