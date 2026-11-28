using Avalonia.Controls;
using Avalonia.Threading;
using MeshControl.Core;
using MeshControl.Desktop;

namespace MeshControl.Mac;

/// <summary>
/// Écrans multiples, pause du flux et reprise après redémarrage (portage de
/// Desktop/DesktopWindow.xaml.cs).
/// </summary>
public partial class DesktopWindow
{
    // MARK: - Écrans multiples

    /// <summary>Écrans proposés, puis la vue combinée en dernier ; vide avec un seul écran.</summary>
    private List<RemoteDisplay> _displayChoices = new();

    private void UpdateDisplays()
    {
        var real = _session.DisplayList.Where(d => d.Id != RemoteDisplay.AllDisplays).ToList();
        if (real.Count <= 1)
        {
            DisplayButton.IsVisible = false;
            _displayChoices = new List<RemoteDisplay>();
            return;
        }

        // Vue combinée : l'agent compose lui-même les écrans en une image unique.
        _displayChoices = new List<RemoteDisplay>(real) { new(RemoteDisplay.AllDisplays, 0, 0, 0, 0) };
        DisplayButton.IsVisible = true;
        RebuildDisplayMenu();
        UpdateDisplayButton();
    }

    private void RebuildDisplayMenu()
    {
        var menu = (MenuFlyout)DisplayButton.Flyout!;
        menu.Items.Clear();
        foreach (var display in _displayChoices)
        {
            var item = new MenuItem
            {
                Header = display.Id == RemoteDisplay.AllDisplays ? "Tous les écrans" : display.Label,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = display.Id == _session.SelectedDisplay,
            };
            var target = display;
            item.Click += (_, _) => SelectDisplay(target);
            // La vue combinée ferme la liste : un trait la détache des écrans.
            if (display.Id == RemoteDisplay.AllDisplays) menu.Items.Add(new Separator());
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Le bouton porte le numéro de l'écran courant : réduit à une icône, il
    /// doit malgré tout indiquer où l'on se trouve.
    /// </summary>
    private void UpdateDisplayButton()
    {
        var current = _displayChoices.FirstOrDefault(d => d.Id == _session.SelectedDisplay);
        DisplayButtonText.Text = current is null || current.Id == RemoteDisplay.AllDisplays ? "⁘" : current.Id.ToString();
    }

    private void SelectDisplay(RemoteDisplay display)
    {
        ScreenHost.Focus();
        if (display.Id == _session.SelectedDisplay)
        {
            RebuildDisplayMenu();   // rétablit la coche si on reclique le même
            return;
        }

        // Une zone agrandie sur un écran n'a pas de sens sur le suivant.
        ResetZoom(quiet: true);
        _session.SelectDisplay(display.Id);
        RebuildDisplayMenu();
        UpdateDisplayButton();
        ShowStatus(display.Id == RemoteDisplay.AllDisplays ? "Tous les écrans." : $"Écran {display.Id}.");
    }

    /// <summary>
    /// Écran suivant ou précédent (Ctrl+Maj+→ / ←), en bouclant. La vue « tous
    /// les écrans » fait partie du cycle : elle en est la dernière étape.
    /// </summary>
    private void CycleDisplay(int delta)
    {
        if (_displayChoices.Count == 0) return;
        var current = Math.Max(0, _displayChoices.FindIndex(d => d.Id == _session.SelectedDisplay));
        SelectDisplay(_displayChoices[(current + delta + _displayChoices.Count) % _displayChoices.Count]);
    }

    // MARK: - Pause du flux

    private bool _streamPaused;

    /// <summary>
    /// Session invisible — fenêtre réduite, onglet en arrière-plan, vignette
    /// repliée : l'agent cesse d'envoyer des images. La reprise redemande une
    /// image complète (géré par la session).
    /// </summary>
    public void UpdateStreamPause()
    {
        // Avant le démarrage, le contenu n'est pas encore à l'écran : sans ce
        // garde, la session s'ouvrirait sur une pause immédiatement défaite.
        if (!_ready || !_started) return;

        var watched = TopLevel.GetTopLevel(RootPanel) is not null
                      && RootPanel.IsEffectivelyVisible
                      && Host.WindowState != WindowState.Minimized;
        if (!watched == _streamPaused) return;
        _streamPaused = !watched;
        _session.SetPaused(_streamPaused);
    }

    private void InitializeStreamPause()
    {
        RootPanel.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(UpdateStreamPause, DispatcherPriority.Loaded);
        RootPanel.DetachedFromVisualTree += (_, _) => UpdateStreamPause();
    }

    // MARK: - Reprise après redémarrage

    /// <summary>
    /// Vrai entre un redémarrage demandé d'ici et le retour de la machine : la
    /// session est alors relancée d'elle-même, même si la reprise automatique a
    /// abandonné entre-temps (mises à jour Windows, démarrage lent).
    /// </summary>
    private bool _rebootPending;

    /// <summary>La machine a été vue hors ligne depuis l'ordre de redémarrage.</summary>
    private bool _rebootSawOffline;

    private void WatchForReboot()
    {
        if (_rebootPending) return;
        _rebootPending = true;
        _rebootSawOffline = false;
        _connection.DevicesChanged += OnDevicesChangedDuringReboot;
        Closed += (_, _) => StopWatchingReboot();
    }

    private void StopWatchingReboot()
    {
        if (!_rebootPending) return;
        _rebootPending = false;
        _connection.DevicesChanged -= OnDevicesChangedDuringReboot;
    }

    // Levé hors du fil d'interface : la connexion laisse le marshaling à l'appelant.
    private void OnDevicesChangedDuringReboot() => Dispatcher.UIThread.Post(CheckRebootReturn);

    private void CheckRebootReturn()
    {
        if (!_rebootPending || !_connection.Nodes.TryGetValue(_node.Id, out var node)) return;

        // Tant que la machine n'a pas disparu, elle n'a pas encore redémarré :
        // la voir « en ligne » juste après l'ordre ne dit rien.
        if (!node.IsOnline)
        {
            _rebootSawOffline = true;
            return;
        }
        if (!_rebootSawOffline) return;

        StopWatchingReboot();
        ShowStatus("Machine redémarrée.");
        if (_session.State == DesktopSessionState.Closed) _session.RetryNow();
    }
}
