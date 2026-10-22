using System.Windows;
using MeshControl.Core;
using Velopack;

namespace MeshControl.UI;

/// <summary>
/// Propose une mise à jour, la télécharge et l'applique.
///
/// Velopack remplace le dossier de l'application puis relance le processus. À la
/// différence de l'ancien installeur, il n'y a donc rien à fermer soi-même : une
/// installation réussie ne revient jamais dans ce code.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _update;
    private CancellationTokenSource? _cts;

    public UpdateWindow(UpdateInfo update)
    {
        _update = update;
        InitializeComponent();

        HeadlineText.Text = $"Mesh Control {UpdateService.TargetVersion(update)} est disponible";
        VersionText.Text = $"Version installée : {UpdateService.CurrentVersion}"
            + $"  ·  Téléchargement de {FormatSize(UpdateService.DownloadSize(update))}";

        var notes = UpdateService.Notes(update);
        if (!string.IsNullOrWhiteSpace(notes))
        {
            NotesText.Text = notes.Trim();
            NotesBox.Visibility = Visibility.Visible;
        }

        Closing += (_, args) =>
        {
            // Fermeture pendant le téléchargement : on annule proprement.
            if (_cts is { IsCancellationRequested: false }) _cts.Cancel();
        };
    }

    /// <summary>
    /// Un paquet delta pèse souvent moins d'un mégaoctet : arrondir au mégaoctet
    /// afficherait « 0 Mo » pour une mise à jour bien réelle.
    /// </summary>
    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:0.#} Mo"
            : $"{Math.Max(1, bytes / 1024)} Ko";

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        LaterButton.Content = "Annuler";
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressLabel.Text = "Téléchargement…";

        _cts = new CancellationTokenSource();
        var progress = new Progress<double>(fraction =>
        {
            ProgressBarControl.Value = fraction;
            ProgressLabel.Text = $"Téléchargement… {fraction * 100:0} %";
        });

        try
        {
            await UpdateService.DownloadAsync(_update, progress, _cts.Token);

            ProgressLabel.Text = "Installation…";

            // Ne rend pas la main : le processus est remplacé par la nouvelle
            // version. Ce qui suit ne s'exécute qu'en cas d'échec.
            UpdateService.ApplyAndRestart(_update);
        }
        catch (OperationCanceledException)
        {
            // Annulation volontaire : on rétablit l'état initial.
            ProgressPanel.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = true;
            LaterButton.Content = "Plus tard";
        }
        catch (Exception ex)
        {
            Log.Error($"Mise à jour impossible : {ex.Message}", ex, "Mise à jour");
            ProgressPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = $"Échec : {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
            InstallButton.IsEnabled = true;
            LaterButton.Content = "Plus tard";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }
}
