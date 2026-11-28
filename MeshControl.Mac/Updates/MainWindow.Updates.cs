using Avalonia.Controls;
using Avalonia.Interactivity;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>Recherche des mises à jour (portage de UI/MainWindow.xaml.cs).</summary>
public partial class MainWindow
{
    private async Task CheckForUpdatesAsync(bool manual)
    {
        var update = await UpdateService.CheckAsync().ConfigureAwait(true);

        if (update is null)
        {
            if (manual)
            {
                await Dialogs.InformAsync(this, UpdateService.IsAvailable
                    ? $"Mesh Control {UpdateService.CurrentVersion} est à jour."
                    : "Les mises à jour ne sont proposées que sur une version installée.");
            }
            return;
        }

        // Version déjà refusée : on ne réinsiste pas à chaque démarrage.
        if (!manual && Settings.SkippedUpdateVersion == UpdateService.TargetVersion(update)) return;

        // Fenêtre masquée dans la barre des menus : l'afficher, sinon la boîte
        // de dialogue surgirait sans contexte.
        RestoreMainWindow();

        // Une installation menée à bien ne revient pas ici : le processus est
        // remplacé. Si l'on reprend la main, l'utilisateur a refusé ou
        // l'installation a échoué.
        if (!await new UpdateWindow(update).ShowDialog<bool>(this))
        {
            Settings.SkippedUpdateVersion = UpdateService.TargetVersion(update);
            Settings.Save();
        }
    }

    /// <summary>
    /// Recherche manuelle : on repropose même une version précédemment refusée,
    /// et on confirme explicitement quand tout est à jour.
    /// </summary>
    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.IsEnabled = false;
        try { await CheckForUpdatesAsync(manual: true); }
        finally { if (sender is Button b) b.IsEnabled = true; }
    }
}
