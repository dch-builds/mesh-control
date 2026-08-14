using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MeshControl.Core;
using MeshControl.UI;

namespace MeshControl.Files;

public partial class FilesWindow : Window, MeshControl.UI.ISessionWindow
{
    /// <summary>Empêche un double démarrage (Loaded et hôte d'onglet).</summary>
    private bool _sessionStarted;

    /// <summary>
    /// Fenêtre à utiliser comme propriétaire des boîtes de dialogue. En mode
    /// onglets, cette fenêtre n'est jamais affichée : WPF refuserait alors de
    /// l'accepter comme propriétaire. On remonte donc à la fenêtre hôte.
    /// </summary>
    private Window DialogOwner
    {
        get
        {
            if (IsLoaded && IsVisible) return this;
            var host = RootPanel is null ? null : Window.GetWindow(RootPanel);
            return host ?? Application.Current.MainWindow ?? this;
        }
    }

    /// <summary>Démarre la session ; sans effet si elle l'est déjà.</summary>
    public async Task StartSessionAsync()
    {
        if (_sessionStarted) return;
        _sessionStarted = true;
        await _session.StartAsync();
    }

    private readonly FilesSession _session;
    private readonly MeshNode _node;

    public FilesWindow(MeshServerConnection connection, MeshNode node)
    {
        _node = node;
        _session = new FilesSession(connection, node, Dispatcher);

        InitializeComponent();

        TitleText.Text = $"{node.Name} — Fichiers";
        SetStatus("Connexion…", "#EFEFF4", "#8A8A8E");

        _session.StateChanged += OnStateChanged;
        _session.EntriesChanged += OnEntriesChanged;
        _session.TransferChanged += OnTransferChanged;

        // En mode onglets, la fenêtre n'est jamais affichée : Loaded ne se
        // déclenche pas, l'hôte appelle StartSessionAsync() explicitement.
        Loaded += async (_, _) => await StartSessionAsync();
        Closed += (_, _) => _session.Stop();
    }

    // MARK: - Session

    private void OnStateChanged()
    {
        switch (_session.State)
        {
            case FilesSessionState.Connecting:
                SetStatus("Connexion…", "#EFEFF4", "#8A8A8E");
                break;
            case FilesSessionState.Ready:
                SetStatus("Connecté", "#E4F8E8", "#248A3D");
                break;
            case FilesSessionState.Closed:
                SetStatus("Session fermée", "#FDE8E7", "#C0392B");
                if (!string.IsNullOrEmpty(_session.CloseReason))
                    MessageBox.Show(_session.CloseReason, "Mesh Control",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    private void OnEntriesChanged()
    {
        FileList.ItemsSource = _session.Entries.Select(e => new FileRow(e)).ToList();
        PathText.Text = _session.DisplayPath;
        UpButton.IsEnabled = _session.PathComponents.Count > 0;

        if (_session.LastError is { } error)
            SetStatus(error, "#FFF4E5", "#B25E09");
        else if (_session.State == FilesSessionState.Ready)
            SetStatus($"{_session.Entries.Count} élément(s)", "#E4F8E8", "#248A3D");
    }

    private void OnTransferChanged()
    {
        var transfer = _session.Transfer;
        if (transfer is null)
        {
            TransferPanel.Visibility = Visibility.Collapsed;
            return;
        }

        TransferPanel.Visibility = Visibility.Visible;
        TransferBar.Value = transfer.Fraction;
        var verb = transfer.IsUpload ? "Envoi" : "Téléchargement";
        TransferLabel.Text = $"{verb} de « {transfer.FileName} » — "
            + $"{FileRow.FormatBytes(transfer.Transferred)} / {FileRow.FormatBytes(transfer.Total)}";
    }

    private void SetStatus(string text, string background, string foreground)
    {
        StatusLabel.Text = text;
        StatusBadge.Background = MakeBrush(background);
        StatusLabel.Foreground = MakeBrush(foreground);
    }

    private static SolidColorBrush MakeBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    // MARK: - Navigation

    private FileRow? SelectedRow => FileList.SelectedItem as FileRow;
    private List<FileRow> SelectedRows => FileList.SelectedItems.Cast<FileRow>().ToList();

    private async void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => await OpenSelectionAsync();

    private async void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                await OpenSelectionAsync();
                break;
            case Key.Back:
                e.Handled = true;
                await _session.GoUpAsync();
                break;
            case Key.Delete:
                e.Handled = true;
                MenuDelete_Click(sender, e);
                break;
            case Key.F5:
                e.Handled = true;
                await _session.ListAsync();
                break;
        }
    }

    private async Task OpenSelectionAsync()
    {
        if (SelectedRow is not { } row) return;
        if (row.IsDirectory) await _session.EnterAsync(row.Entry);
        else Download_Click(this, new RoutedEventArgs());   // un fichier : on le télécharge
    }

    private async void Up_Click(object sender, RoutedEventArgs e) => await _session.GoUpAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await _session.ListAsync();

    private async void MenuOpen_Click(object sender, RoutedEventArgs e)
        => await OpenSelectionAsync();

    // MARK: - Transferts

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row || row.IsDirectory)
        {
            MessageBox.Show("Sélectionnez un fichier à télécharger.", "Mesh Control",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            FileName = row.Name,
            Title = "Enregistrer le fichier",
        };
        if (dialog.ShowDialog(DialogOwner) != true) return;

        try
        {
            await _session.DownloadAsync(row.Entry, dialog.FileName);
            SetStatus($"« {row.Name} » téléchargé.", "#E4F8E8", "#248A3D");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Téléchargement annulé.", "#EFEFF4", "#8A8A8E");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Échec du téléchargement : {ex.Message}", "Mesh Control",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_session.PathComponents.Count == 0 && _node.OsFamily == OSFamily.Windows)
        {
            MessageBox.Show("Ouvrez d'abord un lecteur : on ne peut pas déposer un fichier "
                          + "à la racine de « Cet ordinateur ».",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog { Title = "Choisir un fichier à envoyer" };
        if (dialog.ShowDialog(DialogOwner) != true) return;

        try
        {
            await _session.UploadAsync(dialog.FileName);
            SetStatus($"« {Path.GetFileName(dialog.FileName)} » envoyé.", "#E4F8E8", "#248A3D");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Envoi annulé.", "#EFEFF4", "#8A8A8E");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Échec de l'envoi : {ex.Message}", "Mesh Control",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
        => _session.CancelTransfer();

    // MARK: - Opérations

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextPromptDialog("Nouveau dossier",
            "Nom du nouveau dossier :", "Nouveau dossier", "Créer") { Owner = DialogOwner };
        if (dialog.ShowDialog() != true || dialog.Value is null) return;

        await _session.CreateFolderAsync(dialog.Value);
        // Les mutations ne renvoient rien : on relit le dossier pour voir le résultat.
        await Task.Delay(300);
        await _session.ListAsync();
    }

    private async void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;

        var dialog = new TextPromptDialog("Renommer",
            $"Nouveau nom de « {row.Name} » :", row.Name, "Renommer") { Owner = DialogOwner };
        if (dialog.ShowDialog() != true || dialog.Value is null) return;
        if (dialog.Value == row.Name) return;

        await _session.RenameAsync(row.Name, dialog.Value);
        await Task.Delay(300);
        await _session.ListAsync();
    }

    private async void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;

        var hasDirectory = rows.Any(r => r.IsDirectory);
        var description = rows.Count == 1
            ? $"Supprimer « {rows[0].Name} » ?"
            : $"Supprimer ces {rows.Count} éléments ?";
        var detail = hasDirectory
            ? "Les dossiers seront supprimés avec tout leur contenu. Cette action est irréversible."
            : "Cette action est irréversible.";

        // Suppression définitive côté machine distante : confirmation explicite.
        if (MessageBox.Show(detail, description,
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        await _session.DeleteAsync(rows.Select(r => r.Name), recursive: hasDirectory);
        await Task.Delay(300);
        await _session.ListAsync();
    }

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row) return;
        try { Clipboard.SetText(_session.FullPathOf(row.Entry)); } catch { }
    }

    private void FileMenu_Opened(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        var count = FileList.SelectedItems.Count;

        MenuOpen.IsEnabled = row is not null;
        MenuOpen.Header = row?.IsDirectory == true ? "Ouvrir" : "Télécharger";
        MenuDownload.IsEnabled = row is { IsDirectory: false };
        MenuRename.IsEnabled = count == 1;
        MenuDelete.IsEnabled = count >= 1;
        MenuCopyPath.IsEnabled = count == 1;
    }
}
