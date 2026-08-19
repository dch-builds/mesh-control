using System.Windows;
using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>
/// Propose une mise à jour, la télécharge et lance l'installeur.
///
/// L'installeur ne peut pas remplacer un exécutable en cours d'utilisation :
/// l'application doit donc se fermer. C'est le script Inno qui la relance
/// (section [Run]).
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _update;
    private CancellationTokenSource? _cts;

    /// <summary>Vrai quand l'installeur a été lancé : l'appelant doit quitter.</summary>
    public bool InstallerLaunched { get; private set; }

    public UpdateWindow(UpdateInfo update)
    {
        _update = update;
        InitializeComponent();

        HeadlineText.Text = $"Mesh Control {update.Version} est disponible";
        VersionText.Text = $"Version installée : {UpdateService.CurrentVersion}"
            + (update.AssetSize > 0
                ? $"  ·  Téléchargement de {update.AssetSize / 1024 / 1024} Mo"
                : "");

        if (!string.IsNullOrWhiteSpace(update.Notes))
        {
            NotesText.Text = update.Notes.Trim();
            NotesBox.Visibility = Visibility.Visible;
        }

        Closing += (_, args) =>
        {
            // Fermeture pendant le téléchargement : on annule proprement.
            if (_cts is { IsCancellationRequested: false }) _cts.Cancel();
        };
    }

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
            var path = await UpdateService.DownloadAsync(_update, progress, _cts.Token);

            ProgressLabel.Text = "Lancement de l'installation…";
            UpdateService.LaunchInstaller(path);
            InstallerLaunched = true;

            DialogResult = true;
            Close();
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
