using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeshControl.Core;
using MeshControl.Files;

namespace MeshControl.Mac;

/// <summary>
/// Fichiers glissés depuis le Finder sur l'écran distant : ils partent vers la
/// machine, dans le dossier choisi (portage de Desktop/DesktopWindow.xaml.cs).
/// </summary>
public partial class DesktopWindow
{
    /// <summary>
    /// Fichiers en attente d'envoi, chacun avec son dossier d'arrivée. Un dépôt
    /// fait pendant un envoi s'ajoute à la file plutôt que d'ouvrir un second tunnel.
    /// </summary>
    private readonly Queue<(OutgoingFile File, string Root)> _dropQueue = new();
    private CancellationTokenSource? _dropCts;
    private bool _dropRunning;
    private int _dropIndex;
    private int _dropTotal;
    private string? _dropCurrentName;

    private void InitializeDrop()
    {
        DragDrop.SetAllowDrop(ScreenHost, true);
        DragDrop.AddDragOverHandler(ScreenHost, ScreenHost_DragOver);
        DragDrop.AddDragLeaveHandler(ScreenHost, (_, _) => DropOverlay.IsVisible = false);
        DragDrop.AddDropHandler(ScreenHost, ScreenHost_Drop);
        Closed += (_, _) => _dropCts?.Cancel();
    }

    private void ScreenHost_DragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        if (!DropOverlay.IsVisible)
        {
            DropDestinationText.Text = _dropRunning
                ? "Ils rejoindront l'envoi en cours, dans le dossier de votre choix"
                : "Vous choisirez ensuite le dossier de destination";
            DropOverlay.IsVisible = true;
        }
    }

    private async void ScreenHost_Drop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        e.Handled = true;
        if (e.DataTransfer.TryGetFiles() is not { } items) return;
        var paths = items.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0) return;

        // Hors du fil d'interface : un dossier peut compter des milliers de fichiers.
        var files = await Task.Run(() => paths
            .Select(OutgoingEntry.From)
            .OfType<OutgoingEntry>()
            .SelectMany(entry => entry.Files)
            .ToList());

        if (files.Count == 0)
        {
            ShowStatus("Rien à envoyer : fichiers introuvables ou dossiers vides.");
            return;
        }

        var root = await new DropDestinationDialog(_connection, _node, DescribeFiles(files)).ShowDialog<string?>(Host);
        ScreenHost.Focus();
        if (root is null)
        {
            ShowStatus("Envoi annulé.");
            return;
        }

        foreach (var file in files) _dropQueue.Enqueue((file, root));
        _dropTotal += files.Count;

        if (_dropRunning)
        {
            UpdateDropStatus(null);
            return;
        }

        _dropCts = new CancellationTokenSource();
        TransferPanel.IsVisible = true;
        try
        {
            await RunDropQueueAsync(_dropCts.Token);
        }
        finally
        {
            _dropQueue.Clear();
            _dropTotal = 0;
            _dropIndex = 0;
            _dropCurrentName = null;
            _dropCts.Dispose();
            _dropCts = null;
            TransferPanel.IsVisible = false;
        }
    }

    /// <summary>« « rapport.pdf » · 1,2 Mo », ou « 3 fichiers · 12,4 Mo ».</summary>
    private static string DescribeFiles(IReadOnlyList<OutgoingFile> files)
    {
        var size = FileRow.FormatBytes(files.Sum(f => f.Size));
        return files.Count == 1
            ? $"« {Path.GetFileName(files[0].LocalPath)} » · {size}"
            : $"{files.Count} fichiers · {size}";
    }

    private void CancelDrop_Click(object? sender, RoutedEventArgs e)
    {
        _dropCts?.Cancel();
        ScreenHost.Focus();
    }

    /// <summary>
    /// Envoie la file sur un tunnel de fichiers ouvert pour l'occasion, refermé
    /// une fois la file vide. Les dossiers glissés gardent leur arborescence.
    /// </summary>
    private async Task RunDropQueueAsync(CancellationToken ct)
    {
        _dropRunning = true;
        var sent = 0;
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var session = new FilesSession(_connection, _node, AvaloniaUiDispatcher.Instance);
        void OnProgress() => UpdateDropStatus(session.Transfer);
        session.TransferChanged += OnProgress;

        StatusTransfer.Text = "Connexion pour l'envoi…";

        try
        {
            await session.StartAsync(listOnStart: false);
            if (session.State != FilesSessionState.Ready)
                throw new MeshException(session.CloseReason ?? "Connexion à la machine impossible.");

            var ensured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (_dropQueue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (file, root) = _dropQueue.Dequeue();

                if (ensured.Add(root)) await session.EnsureDirectoryAsync(root, ct);
                var directory = root;
                foreach (var segment in file.RelativeDirectory)
                    directory = session.CombinePath(directory, segment);
                if (ensured.Add(directory)) await session.EnsureDirectoryAsync(directory, ct);

                _dropIndex++;
                _dropCurrentName = Path.GetFileName(file.LocalPath);
                UpdateDropStatus(null);

                await session.UploadAsync(file.LocalPath, directory, ct);
                sent++;
                roots.Add(root);
            }

            var where = roots.Count == 1 ? $" dans {roots.First()}" : "";
            ShowStatus(sent == 1 ? $"Fichier reçu{where}" : $"{sent} fichiers reçus{where}");
            Log.Info($"{sent} fichier(s) déposé(s) sur {_node.Name} ({string.Join(", ", roots)})", "Bureau");
        }
        catch (OperationCanceledException)
        {
            ShowStatus(sent > 0 ? $"Envoi annulé ({sent} fichier(s) reçu(s))." : "Envoi annulé.");
        }
        catch (Exception ex)
        {
            Log.Warning($"Dépôt de fichiers sur {_node.Name} : {ex.Message}", "Bureau");
            ShowStatus("Envoi interrompu.");
            await Dialogs.InformAsync(Host, $"L'envoi vers « {_node.Name} » s'est interrompu"
                + (sent > 0 ? $" après {sent} fichier(s)" : "") + $" : {ex.Message}");
        }
        finally
        {
            session.TransferChanged -= OnProgress;
            session.Stop();
            _dropRunning = false;
        }
    }

    private void UpdateDropStatus(TransferProgress? progress)
    {
        if (!_dropRunning || _dropCurrentName is null) return;

        var parts = new List<string> { $"Envoi {_dropIndex}/{_dropTotal}", _dropCurrentName };
        if (progress is not null)
        {
            parts.Add($"{Math.Floor(progress.Fraction * 100)} %");
            if (FileRow.FormatSpeed(progress.BytesPerSecond) is { Length: > 0 } speed) parts.Add(speed);
        }
        StatusTransfer.Text = string.Join(" · ", parts);
    }
}
