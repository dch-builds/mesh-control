using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Crée et gère les liens de partage public d'un appareil, et fournit le code
/// iframe prêt à intégrer dans un outil interne (portage de UI/ShareWindow.xaml.cs).
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
        Title = $"Partager « {node.Name} » — Mesh Control";
        HeaderText.Text = $"Partager « {node.Name} »";
        UpdateSingleAccessHint();

        Opened += async (_, _) => await RefreshSharesAsync();
    }

    /// <summary>
    /// Explique le seul levier réellement efficace sur l'interface de la page de
    /// partage : la barre latérale n'apparaît que si plusieurs accès sont partagés.
    /// </summary>
    private void UpdateSingleAccessHint()
    {
        // Les handlers du XAML peuvent se déclencher pendant InitializeComponent,
        // avant que les contrôles suivants n'existent.
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

    private void AccessOption_Changed(object? sender, RoutedEventArgs e) => UpdateSingleAccessHint();

    // MARK: - Création

    private async void Create_Click(object? sender, RoutedEventArgs e)
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

        var guestName = GuestBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(guestName))
        {
            ShowError("Indiquez un nom d'invité (il apparaît dans les journaux du serveur).");
            return;
        }

        var duration = int.TryParse((DurationCombo.SelectedItem as ComboBoxItem)?.Tag as string, out var minutes)
            ? minutes : 480;

        CreateButton.IsEnabled = false;
        try
        {
            var share = await _connection.CreateShareLinkAsync(
                _node.Id, guestName, protocols, duration, ConsentValue(),
                viewOnly: ViewOnlyCheck.IsChecked == true);

            _lastShareUrl = share.Url;
            RenderResult();
            ResultPanel.IsVisible = true;

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
        if (_lastShareUrl is null || LinkBox is null) return;
        var url = BuildDisplayUrl(_lastShareUrl);
        LinkBox.Text = url;
        IframeBox.Text = BuildIframe(url);
    }

    /// <summary>
    /// Ajoute les arguments d'affichage au lien de partage. La page de partage
    /// de MeshCentral ne lit que debug, xterm, hide et locale ; « hide » n'y
    /// ajuste que des hauteurs de zone. Le vrai levier est de ne partager qu'un
    /// seul accès : la barre latérale disparaît alors.
    /// </summary>
    private string BuildDisplayUrl(string baseUrl)
    {
        if (HideUiCheck.IsChecked != true) return baseUrl;
        var separator = baseUrl.Contains('?') ? "&" : "?";
        return baseUrl + separator + "hide=15";
    }

    private void DisplayOption_Changed(object? sender, RoutedEventArgs e) => RenderResult();

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
            NoSharesText.Text = "Aucun partage actif.";
            NoSharesText.IsVisible = shares.Count == 0;
        }
        catch (Exception ex)
        {
            // Ne pas bloquer la création si la liste échoue.
            NoSharesText.Text = $"Impossible de lister les partages : {ex.Message}";
            NoSharesText.IsVisible = true;
        }
    }

    private async void Revoke_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not DeviceShare { PublicId.Length: > 0 } share) return;

        if (!await Dialogs.ConfirmAsync(this, "Révoquer ce partage ?",
                "Le lien cessera immédiatement de fonctionner pour toute personne qui l'utilise.", "Révoquer"))
            return;

        try
        {
            await _connection.RemoveShareAsync(_node.Id, share.PublicId);
            await Task.Delay(300);   // laisser le serveur enregistrer
            await RefreshSharesAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Échec de la révocation : {ex.Message}");
        }
    }

    private void CopyExisting_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DeviceShare { Url.Length: > 0 } share) CopyToClipboard(share.Url);
    }

    // MARK: - Presse-papiers

    private void CopyLink_Click(object? sender, RoutedEventArgs e) => CopyToClipboard(LinkBox.Text);
    private void CopyIframe_Click(object? sender, RoutedEventArgs e) => CopyToClipboard(IframeBox.Text);

    private async void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text) || Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(text); }
        catch { /* presse-papiers verrouillé par une autre application */ }
    }

    private async void OpenLink_Click(object? sender, RoutedEventArgs e)
    {
        var url = LinkBox.Text;
        // Seul un lien web part vers le navigateur.
        if (url is null || !MeshServerConnection.IsWebUrl(url)) return;
        try
        {
            await Launcher.LaunchUriAsync(new Uri(url));
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
        ErrorText.IsVisible = true;
    }

    private void HideError() => ErrorText.IsVisible = false;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
