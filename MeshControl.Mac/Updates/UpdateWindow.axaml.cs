using Avalonia.Interactivity;
using MeshControl.Core;
using Velopack;

namespace MeshControl.Mac;

/// <summary>
/// Propose une mise à jour, la télécharge et l'applique (portage de
/// UI/UpdateWindow.xaml.cs). Velopack remplace l'application puis relance le
/// processus : une installation réussie ne revient jamais dans ce code.
/// </summary>
public partial class UpdateWindow : ChromeWindow
{
    private readonly UpdateInfo _update;
    private CancellationTokenSource? _cts;

    public UpdateWindow(UpdateInfo update)
    {
        _update = update;
        InitializeComponent();
        Title = "Mise à jour — Mesh Control";

        HeadlineText.Text = $"Mesh Control {UpdateService.TargetVersion(update)} est disponible";
        VersionText.Text = $"Version installée : {UpdateService.CurrentVersion}"
            + $"  ·  Téléchargement de {FormatSize(UpdateService.DownloadSize(update))}";

        var notes = UpdateService.Notes(update);
        if (!string.IsNullOrWhiteSpace(notes))
        {
            NotesText.Text = notes.Trim();
            NotesBox.IsVisible = true;
        }

        // Fermeture pendant le téléchargement : on annule proprement.
        Closing += (_, _) =>
        {
            if (_cts is { IsCancellationRequested: false }) _cts.Cancel();
        };
    }

    /// <summary>Un paquet delta pèse souvent moins d'un mégaoctet : pas de « 0 Mo ».</summary>
    private static string FormatSize(long bytes)
        => bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.#} Mo" : $"{Math.Max(1, bytes / 1024)} Ko";

    private void Later_Click(object? sender, RoutedEventArgs e) => Close(false);

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        LaterButton.Content = "Annuler";
        ErrorText.IsVisible = false;
        ProgressPanel.IsVisible = true;
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
            // Ne rend pas la main : le processus est remplacé par la nouvelle version.
            UpdateService.ApplyAndRestart(_update);
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.IsVisible = false;
            InstallButton.IsEnabled = true;
            LaterButton.Content = "Plus tard";
        }
        catch (Exception ex)
        {
            Log.Error($"Mise à jour impossible : {ex.Message}", ex, "Mise à jour");
            ProgressPanel.IsVisible = false;
            ErrorText.Text = $"Échec : {ex.Message}";
            ErrorText.IsVisible = true;
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
