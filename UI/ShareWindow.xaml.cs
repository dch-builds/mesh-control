using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>
/// Crée et gère les liens de partage public d'un appareil, et fournit le code
/// iframe prêt à intégrer dans un outil interne.
/// </summary>
public partial class ShareWindow : ChromeWindow
{
    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;

    public ShareWindow(MeshServerConnection connection, MeshNode node)
    {
        _connection = connection;
        _node = node;

        InitializeComponent();
        HeaderText.Text = $"Partager « {node.Name} »";
        UpdateSingleAccessHint();

        Loaded += async (_, _) => await RefreshSharesAsync();
    }

    /// <summary>
    /// Explique le seul levier réellement efficace sur l'interface de la page de
    /// partage : la barre latérale n'apparaît que si plusieurs accès sont partagés.
    /// </summary>
    private void UpdateSingleAccessHint()
    {
        // Les handlers du XAML se déclenchent pendant InitializeComponent, avant
        // que les contrôles suivants n'existent.
        if (SingleAccessText is null) return;

        int count = 0;
        if (DesktopCheck.IsChecked == true) count++;
        if (TerminalCheck.IsChecked == true) count++;
        if (FilesCheck.IsChecked == true) count++;

        SingleAccessText.Text = count > 1
            ? "Avec plusieurs accès partagés, MeshCentral affiche une barre latérale de "
              + "navigation. Ne cochez que « Bureau à distance » pour l'éliminer."
            : "Un seul accès partagé : MeshCentral n'affichera pas la barre latérale de "
              + "navigation. Une barre supérieure (bouton Connecter, état) reste "
              + "toutefois présente — elle n'est pas masquable par lien.";
    }

    private void AccessOption_Changed(object sender, RoutedEventArgs e)
        => UpdateSingleAccessHint();

    // MARK: - Création

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        HideError();

        // Masque de bits des accès : 1 = terminal, 2 = bureau, 4 = fichiers.
        int protocols = 0;
        if (DesktopCheck.IsChecked == true) protocols |= (int)SharedFeatures.Desktop;
        if (TerminalCheck.IsChecked == true) protocols |= (int)SharedFeatures.Terminal;
        if (FilesCheck.IsChecked == true) protocols |= (int)SharedFeatures.Files;

        if (protocols == 0)
        {
            ShowError("Sélectionnez au moins un type d'accès.");
            return;
        }

        var guestName = GuestBox.Text.Trim();
        if (string.IsNullOrEmpty(guestName))
        {
            ShowError("Indiquez un nom d'invité (il apparaît dans les journaux du serveur).");
            return;
        }

        var duration = int.TryParse(
            (DurationCombo.SelectedItem as ComboBoxItem)?.Tag as string, out var minutes)
            ? minutes : 480;

        var consent = ConsentValue();

        CreateButton.IsEnabled = false;
        try
        {
            var share = await _connection.CreateShareLinkAsync(
                _node.Id, guestName, protocols, duration, consent,
                viewOnly: ViewOnlyCheck.IsChecked == true);

            _lastShareUrl = share.Url;
            RenderResult();
            ResultPanel.Visibility = Visibility.Visible;

            await RefreshSharesAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }

    /// <summary>URL brute du dernier partage créé, avant ajout des options d'affichage.</summary>
    private string? _lastShareUrl;

    /// <summary>
    /// Recalcule le lien et le code iframe selon les options d'intégration. Les
    /// options d'affichage sont de simples arguments d'URL : on peut donc les
    /// changer sans recréer le partage.
    /// </summary>
    private void RenderResult()
    {
        // _lastShareUrl est null tant qu'aucun partage n'a été créé, ce qui couvre
        // aussi les handlers déclenchés pendant le chargement du XAML.
        if (_lastShareUrl is null || LinkBox is null) return;

        var url = BuildDisplayUrl(_lastShareUrl);
        LinkBox.Text = url;
        IframeBox.Text = BuildIframe(url);
    }

    /// <summary>
    /// Ajoute les arguments d'affichage au lien de partage.
    ///
    /// La page de partage de MeshCentral ne lit que quatre arguments d'URL :
    /// debug, xterm, hide et locale. « hide » n'y ajuste que des hauteurs de
    /// zone, sans masquer les barres — son effet reste donc marginal. Il n'existe
    /// aucun paramètre de connexion automatique (la fonction interne
    /// autoConnectDesktop est marquée « Debug » et n'est pas exposée).
    ///
    /// Le vrai levier est ailleurs : la barre latérale de navigation n'apparaît
    /// que si PLUSIEURS accès sont partagés (QV('LeftSideToolBar', featureCount > 1)).
    /// Partager le seul bureau suffit donc à l'éliminer.
    /// </summary>
    private string BuildDisplayUrl(string baseUrl)
    {
        if (HideUiCheck.IsChecked != true) return baseUrl;
        var separator = baseUrl.Contains('?') ? "&" : "?";
        return baseUrl + separator + "hide=15";
    }

    private void DisplayOption_Changed(object sender, RoutedEventArgs e) => RenderResult();

    /// <summary>
    /// Traduit le choix de consentement en drapeaux MeshCentral. Les bits
    /// utilisés côté serveur : 0x01 notification bureau, 0x08 invite bureau
    /// (mêmes bits décalés pour terminal et fichiers).
    /// </summary>
    private int ConsentValue()
    {
        var tag = (ConsentCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            // Invite + notification, pour les trois types d'accès.
            "prompt" => 0x40 | 0x08 | 0x01 | 0x02 | 0x04 | 0x10 | 0x20,
            "notify" => 0x01 | 0x02 | 0x04,
            _ => 0,
        };
    }

    private string BuildIframe(string url)
    {
        // Plein écran : l'iframe épouse son conteneur. Sinon, dimensions fixes.
        var sizing = FullScreenIframeCheck.IsChecked == true
            ? "        style=\"border:0;width:100%;height:100%;min-height:600px;display:block\""
            : "        width=\"1280\" height=\"800\"\n        style=\"border:0;border-radius:8px\"";

        return $"<iframe src=\"{url}\"\n"
             + sizing + "\n"
             + "        allow=\"fullscreen; clipboard-read; clipboard-write\">\n"
             + "</iframe>";
    }

    // MARK: - Partages existants

    private async Task RefreshSharesAsync()
    {
        try
        {
            var shares = await _connection.ListSharesAsync(_node.Id);
            SharesList.ItemsSource = shares;
            NoSharesText.Visibility = shares.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // Ne pas bloquer la création si la liste échoue.
            NoSharesText.Text = $"Impossible de lister les partages : {ex.Message}";
            NoSharesText.Visibility = Visibility.Visible;
        }
    }

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string publicId || publicId.Length == 0) return;

        if (MessageBox.Show(
                "Le lien cessera immédiatement de fonctionner pour toute personne qui l'utilise.",
                "Révoquer ce partage ?",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        try
        {
            await _connection.RemoveShareAsync(_node.Id, publicId);
            await Task.Delay(300);   // laisser le serveur enregistrer
            await RefreshSharesAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Échec de la révocation : {ex.Message}");
        }
    }

    private void CopyExisting_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string url && url.Length > 0) CopyToClipboard(url);
    }

    // MARK: - Presse-papiers

    private void CopyLink_Click(object sender, RoutedEventArgs e) => CopyToClipboard(LinkBox.Text);
    private void CopyIframe_Click(object sender, RoutedEventArgs e) => CopyToClipboard(IframeBox.Text);

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); }
        catch { /* presse-papiers verrouillé par une autre application */ }
    }

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        var url = LinkBox.Text;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError($"Impossible d'ouvrir le navigateur : {ex.Message}");
        }
    }

    // MARK: - Divers

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
