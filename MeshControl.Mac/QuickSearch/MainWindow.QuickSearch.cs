using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Recherche rapide et actions de machine (portage de UI/MainWindow.xaml.cs) :
/// le raccourci global ouvre la palette par-dessus les autres applications, et
/// une même table d'actions sert la palette et le menu contextuel.
/// </summary>
public partial class MainWindow
{
    private GlobalHotkey? _hotkey;
    private QuickSearchWindow? _quickSearch;

    private void SetupHotkey()
    {
        _hotkey ??= new GlobalHotkey();
        _hotkey.Pressed -= ShowQuickSearch;
        _hotkey.Pressed += ShowQuickSearch;

        HotkeyHintBadge.IsVisible = _hotkey.IsRegistered;
        HotkeyHintText.Text = GlobalHotkey.Label;
        Closed += (_, _) => _hotkey?.Dispose();
    }

    /// <summary>
    /// Ouvre la palette de recherche rapide par-dessus les autres applications.
    /// Déclenché par le raccourci global, même si Mesh Control est en arrière-plan.
    /// </summary>
    public void ShowQuickSearch()
    {
        // Si la palette est déjà ouverte, le raccourci la ferme (bascule).
        if (_quickSearch is not null)
        {
            _quickSearch.DismissSafely();
            _quickSearch = null;
            return;
        }

        if (_connection?.State != ConnectionState.Connected)
        {
            // Pas connecté : on montre la fenêtre principale plutôt qu'une palette vide.
            RestoreMainWindow();
            return;
        }

        _quickSearch = new QuickSearchWindow(
            _allRows,
            RunActionFromQuickSearch,
            IsActionAvailable,
            node => Settings.IsFavorite(node.Id),
            Settings.RecentNodeIds);
        _quickSearch.Closed += (_, _) => _quickSearch = null;
        _quickSearch.Show();
        _quickSearch.Activate();
    }

    /// <summary>Ramène la fenêtre principale au premier plan (masquée dans la barre de menus, réduite…).</summary>
    public void RestoreMainWindow()
    {
        if (!IsVisible) Show();
        if (WindowState == Avalonia.Controls.WindowState.Minimized) WindowState = Avalonia.Controls.WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Action lancée depuis la palette : on n'affiche pas la fenêtre principale,
    /// le but étant justement de rester dans son contexte de travail — sauf pour
    /// les actions qui demandent une confirmation ou une saisie.
    /// </summary>
    private async void RunActionFromQuickSearch(MeshNode node, DeviceAction action)
    {
        if (_connection is null) return;

        if (!IsActionAvailable(node, action))
        {
            RestoreMainWindow();
            await Dialogs.InformAsync(this, !node.IsOnline
                ? $"« {node.Name} » est hors ligne."
                : $"« {node.Name} » n'a pas d'agent installé.");
            return;
        }

        RunAction(node, action);
    }

    /// <summary>Une action est-elle possible sur cette machine, en l'état ?</summary>
    public bool IsActionAvailable(MeshNode node, DeviceAction action)
    {
        bool reachable = node is { IsOnline: true, HasAgent: true };
        return action switch
        {
            // Ces actions passent par l'agent installé sur la machine.
            DeviceAction.Desktop or DeviceAction.Terminal or DeviceAction.Files or DeviceAction.SendFiles
                or DeviceAction.Processes or DeviceAction.OpenUrl => reachable,
            DeviceAction.Reboot or DeviceAction.Sleep or DeviceAction.PowerOff => reachable,
            // SSH passe par le serveur : l'agent n'est pas requis.
            DeviceAction.Ssh => node.IsOnline || _connection?.IsLocalDevice(node) == true,
            // Le reste est traité côté serveur ou localement.
            _ => true,
        };
    }

    /// <summary>Exécute une action sur une machine, quelle qu'en soit l'origine.</summary>
    private async void RunAction(MeshNode node, DeviceAction action)
    {
        if (_connection is not { } connection) return;
        var row = _rowsById.GetValueOrDefault(node.Id);

        switch (action)
        {
            case DeviceAction.Desktop:
                if (row is not null) OpenDesktop(row);
                break;
            case DeviceAction.Terminal:
                OpenTerminal(node, TerminalWindow.Mode.Agent);
                break;
            case DeviceAction.Ssh:
                OpenTerminal(node, TerminalWindow.Mode.Ssh);
                break;
            case DeviceAction.Files:
                OpenFiles(node);
                break;
            case DeviceAction.SendFiles:
                OpenSendFiles([node]);
                break;
            case DeviceAction.Processes:
                OpenProcesses(node);
                break;
            case DeviceAction.SystemInfo:
                OpenSystemInfo(node);
                break;
            case DeviceAction.OpenUrl:
                RestoreMainWindow();
                await OpenRemoteUrlAsync(node);
                break;
            case DeviceAction.Wake:
                await RunPowerAsync(() => connection.WakeAsync(node.Id), $"Signal de réveil envoyé à « {node.Name} ».");
                break;
            case DeviceAction.Reboot or DeviceAction.Sleep or DeviceAction.PowerOff when row is not null:
                RestoreMainWindow();
                await PowerAsync([row], action switch
                {
                    DeviceAction.Reboot => PowerActionType.Reboot,
                    DeviceAction.Sleep => PowerActionType.Sleep,
                    _ => PowerActionType.PowerOff,
                });
                break;
            case DeviceAction.ToggleFavorite:
                ToggleFavorite(node);
                break;
            case DeviceAction.Share:
                RestoreMainWindow();
                await new ShareWindow(connection, node).ShowDialog(this);
                break;
            case DeviceAction.Rename:
                RestoreMainWindow();
                await RenameAsync(node);
                break;
            case DeviceAction.CopyName:
                try
                {
                    if (Clipboard is { } clipboard) await clipboard.SetTextAsync(node.Name);
                    ShowToast($"« {node.Name} » copié.");
                }
                catch { /* presse-papiers occupé */ }
                break;
            case DeviceAction.WebPage:
                if (connection.Profile.DeviceWebUrl(node.Id) is { } url)
                    await Launcher.LaunchUriAsync(new Uri(url));
                break;
        }
    }
}
