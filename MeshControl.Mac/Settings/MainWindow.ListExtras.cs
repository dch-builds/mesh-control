using System.Net.NetworkInformation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Compléments de la liste des machines, repris de la version Windows
/// (UI/MainWindow.xaml.cs) : saisie directe vers la recherche, menu des
/// dossiers, fichiers déposés sur une machine, resynchronisation au retour de
/// veille, d'un changement de réseau ou dans la fenêtre.
/// </summary>
public partial class MainWindow
{
    private void InitializeListExtras()
    {
        // Une lettre tapée dans la liste part dans la recherche : on cherche
        // sans viser le champ d'abord.
        DeviceList.AddHandler(TextInputEvent, DeviceList_TextInput, RoutingStrategies.Tunnel);

        FolderList.ContextRequested += FolderList_ContextRequested;

        DragDrop.SetAllowDrop(DeviceList, true);
        DragDrop.AddDragOverHandler(DeviceList, DeviceList_DragOver);
        DragDrop.AddDropHandler(DeviceList, DeviceList_Drop);

        SetupResumeWatch();
        _resyncTimer.Tick += (_, _) =>
        {
            _resyncTimer.Stop();
            _ = ResyncAsync();
        };
        Activated += (_, _) =>
        {
            // Retour dans la fenêtre après un moment : c'est le moment de
            // vérifier que la liste est juste, sans attendre la relecture périodique.
            if (DateTime.UtcNow - _lastResync > TimeSpan.FromMinutes(1)) ScheduleResync("retour dans la fenêtre");
        };
    }

    // MARK: - Saisie directe

    private void DeviceList_TextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (char.IsWhiteSpace(e.Text[0]) && string.IsNullOrEmpty(SearchBox.Text)) return;

        SearchBox.Focus();
        SearchBox.Text += e.Text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    // MARK: - Menu des dossiers

    private void FolderList_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not FolderRow { IsEntry: true } folder)
            return;
        e.Handled = true;

        var menu = new ContextMenu();
        void Add(string header, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        // Actions sur les machines : pas sur « Toutes les machines », où un
        // clic malheureux toucherait tout le parc.
        if (folder.Key != FolderRow.AllKey)
        {
            var nodes = RowsOfFolder(folder.Key).Select(r => r.Node).GroupBy(n => n.Id).Select(g => g.First()).ToList();
            var offline = nodes.Where(n => !n.IsOnline).ToList();
            var reachable = nodes.Where(n => n is { IsOnline: true, HasAgent: true }).ToList();

            Add($"Réveiller les machines hors ligne ({offline.Count})", offline.Count > 0, () =>
            {
                if (_connection is { } connection)
                    _ = RunPowerAsync(() => connection.WakeAsync(offline.Select(n => n.Id)),
                                      $"Signal de réveil envoyé à {Machines(offline.Count)}.");
            });
            Add($"Envoyer des fichiers aux machines en ligne ({reachable.Count})", reachable.Count > 0,
                () => OpenSendFiles(reachable));
            var anyUnwatched = offline.Any(n => !_watchOnline.Contains(n.Id));
            Add(anyUnwatched || offline.Count == 0
                    ? $"Me prévenir du retour des machines hors ligne ({offline.Count})"
                    : $"Ne plus me prévenir de leur retour ({offline.Count})",
                offline.Count > 0, () => SetWatched(offline, anyUnwatched));
        }

        if (folder.IsRealFolder)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            Add(folder.IsPinned ? "Désépingler" : "Épingler en haut du volet", true, () => TogglePinnedFolder(folder.Key));
            Add("Masquer ce dossier", true, () => SetFolderHidden(folder.Key, true));
        }
        else if (folder.Key == FolderRow.HiddenKey)
        {
            menu.Items.Add(new Separator());
            foreach (var name in HiddenFolderNames())
                Add($"Réafficher « {name} »", true, () => SetFolderHidden(name, false));
            Add("Tout réafficher", true, () =>
            {
                Settings.HiddenFolders.Clear();
                AfterFolderLayoutChanged();
            });
        }

        if (menu.Items.Count > 0) menu.Open(FolderList);
    }

    private void TogglePinnedFolder(string name)
    {
        if (Settings.PinnedFolders.RemoveAll(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)) == 0)
            Settings.PinnedFolders.Add(name);
        AfterFolderLayoutChanged();
    }

    private void SetFolderHidden(string name, bool hidden)
    {
        Settings.HiddenFolders.RemoveAll(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        if (hidden)
        {
            Settings.HiddenFolders.Add(name);
            // Le dossier affiché disparaît du volet : on revient à tout le parc.
            if (string.Equals(_selectedFolderKey, name, StringComparison.OrdinalIgnoreCase))
                _selectedFolderKey = FolderRow.AllKey;
            ShowToast($"« {name} » est masqué. Il reste accessible sous « dossiers masqués », en bas du volet.");
        }
        AfterFolderLayoutChanged();
    }

    /// <summary>Enregistre l'ordre du volet et recompose volet et liste.</summary>
    private void AfterFolderLayoutChanged()
    {
        Settings.Save();
        RebuildFolderPanel();
        ApplyFilter(force: true);
    }

    // MARK: - Fichiers déposés sur une machine

    private static DeviceRow? DeviceRowAt(object? source)
        => (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as DeviceRow;

    private void DeviceList_DragOver(object? sender, DragEventArgs e)
    {
        var ok = e.DataTransfer.Contains(DataFormat.File)
                 && DeviceRowAt(e.Source)?.Node is { IsOnline: true, HasAgent: true };
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Fichiers du Finder lâchés sur une machine : la fenêtre d'envoi s'ouvre, fichiers et machine choisis.</summary>
    private void DeviceList_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.DataTransfer.TryGetFiles() is not { } items) return;
        var paths = items.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0 || DeviceRowAt(e.Source)?.Node is not { IsOnline: true, HasAgent: true } node) return;
        OpenSendFiles([node], paths);
    }

    // MARK: - Resynchronisation

    /// <summary>Regroupe les demandes rapprochées (plusieurs avis réseau à la suite).</summary>
    private readonly DispatcherTimer _resyncTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _resyncReason = "";
    private DateTime _lastResync = DateTime.UtcNow;

    /// <summary>Attente entre deux tentatives de reconnexion, que la resynchronisation peut écourter.</summary>
    private TaskCompletionSource? _reconnectWakeup;

    /// <summary>
    /// Écoute les changements d'adresse réseau — dont la sortie de veille, qui
    /// en provoque toujours. Le canal de contrôle est alors souvent mort sans
    /// que rien ne l'ait signalé ; sans cette écoute, il fallait attendre le
    /// contrôle de silence.
    /// </summary>
    private void SetupResumeWatch()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        Closed += (_, _) => NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => ScheduleResync("changement d'adresse réseau");

    private void ScheduleResync(string reason)
    {
        // Ces événements arrivent depuis des fils système.
        Dispatcher.UIThread.Post(() =>
        {
            _resyncReason = reason;
            _resyncTimer.Stop();
            _resyncTimer.Start();
        });
    }

    /// <summary>
    /// Remet l'affichage en phase avec le serveur : relecture de l'inventaire si
    /// la connexion tient, reprise immédiate de la reconnexion sinon.
    /// </summary>
    private async Task ResyncAsync()
    {
        _lastResync = DateTime.UtcNow;
        if (_connection is not { } connection) return;

        if (connection.State == ConnectionState.Connected)
        {
            Log.Info($"Relecture de l'inventaire ({_resyncReason})", "Serveur");
            try { await connection.RefreshDeviceListAsync(); }
            catch { /* la perte de connexion est détectée par le contrôle de silence */ }
            return;
        }

        // Une attente entre deux tentatives est en cours : la raison probable de
        // l'échec vient de disparaître, inutile de patienter jusqu'au bout.
        _reconnectAttempt = 0;
        if (_reconnectWakeup?.TrySetResult() == true)
            Log.Info($"Reconnexion avancée ({_resyncReason})", "Serveur");
    }

    /// <summary>Attente avant une nouvelle tentative, écourtée par une resynchronisation.</summary>
    private async Task WaitBeforeRetryAsync(TimeSpan delay)
    {
        var wakeup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reconnectWakeup = wakeup;
        await Task.WhenAny(Task.Delay(delay), wakeup.Task);
        if (ReferenceEquals(_reconnectWakeup, wakeup)) _reconnectWakeup = null;
    }
}
