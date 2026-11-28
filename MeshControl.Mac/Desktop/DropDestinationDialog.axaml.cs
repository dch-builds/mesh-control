using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeshControl.Core;
using MeshControl.Files;

namespace MeshControl.Mac;

/// <summary>Un emplacement proposé : bureau, téléchargements, disque…</summary>
public sealed record DropShortcut(IconKind Glyph, string Label, string Detail, string Path)
{
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// Demande où envoyer des fichiers déposés sur le bureau à distance (portage de
/// Desktop/DropDestinationDialog.xaml.cs). Les dossiers courants de la machine
/// — bureau, téléchargements et documents de chaque utilisateur, disques — sont
/// proposés en raccourcis, et le chemin reste modifiable.
///
/// Les raccourcis sont trouvés en lisant les dossiers de la machine par le
/// tunnel de fichiers. En attendant, ou si la lecture échoue, des emplacements
/// génériques suffisent à envoyer.
/// </summary>
public partial class DropDestinationDialog : ChromeWindow
{
    /// <summary>Profils examinés au plus, les plus récemment modifiés d'abord.</summary>
    private const int MaxProfiles = 6;

    /// <summary>Dossiers de C:\Users qui ne sont pas des profils d'utilisateur.</summary>
    private static readonly HashSet<string> SystemProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Public", "Default", "Default User", "All Users", "defaultuser0", "WDAGUtilityAccount",
    };

    // Noms localisés des dossiers d'un utilisateur Linux (xdg-user-dirs).
    private static readonly string[] UnixDesktopNames = ["Desktop", "Bureau", "Schreibtisch", "Escritorio", "Scrivania"];
    private static readonly string[] UnixDownloadNames = ["Downloads", "Téléchargements", "Descargas", "Scaricati"];
    private static readonly string[] UnixDocumentNames = ["Documents", "Dokumente", "Documentos", "Documenti"];

    /// <summary>
    /// Dernier dossier choisi pour chaque machine, le temps de l'exécution :
    /// une série de dépôts vise le plus souvent le même endroit.
    /// </summary>
    private static readonly Dictionary<string, string> LastByNode = new();

    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;
    private readonly bool _isWindows;
    private readonly string? _last;
    private readonly ObservableCollection<DropShortcut> _shortcuts = new();
    private readonly CancellationTokenSource _closing = new();

    /// <summary>Vrai pendant que le code, et non l'utilisateur, écrit le chemin.</summary>
    private bool _settingPath;

    /// <summary>L'utilisateur a choisi ou saisi un chemin : les raccourcis arrivés ensuite ne le remplacent pas.</summary>
    private bool _pathChosen;

    /// <param name="summary">Ce qui part : « 3 fichiers · 12,4 Mo ».</param>
    public DropDestinationDialog(MeshServerConnection connection, MeshNode node, string summary)
    {
        _connection = connection;
        _node = node;
        _isWindows = node.OsFamily == OSFamily.Windows;
        _last = LastByNode.GetValueOrDefault(node.Id);

        InitializeComponent();
        Title = "Envoyer vers la machine";
        SummaryText.Text = $"{summary} vers « {node.Name} »";
        ShortcutList.ItemsSource = _shortcuts;
        FolderList.ItemsSource = _folders;

        ShowShortcuts(GenericShortcuts());
        SetPath(_last ?? (_isWindows ? @"C:\Users\Public\Desktop" : "/tmp"));

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close(null);
        };
        Opened += async (_, _) =>
        {
            PathBox.Focus();
            PathBox.SelectAll();
            await LoadShortcutsAsync();
        };
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _files?.Stop();
        };
    }

    // MARK: - Raccourcis

    /// <summary>Emplacements valables sur toute machine, sans rien lire.</summary>
    private List<DropShortcut> GenericShortcuts() => _isWindows
        ?
        [
            new(IconKind.Desktop, "Bureau public", @"Tous les utilisateurs · C:\Users\Public\Desktop", @"C:\Users\Public\Desktop"),
            new(IconKind.Download, "Téléchargements publics", @"C:\Users\Public\Downloads", @"C:\Users\Public\Downloads"),
            new(IconKind.Drive, "Disque C:", @"C:\", @"C:\"),
        ]
        :
        [
            new(IconKind.Folder, "Fichiers temporaires", "/tmp", "/tmp"),
            new(IconKind.Drive, "Racine", "/", "/"),
        ];

    /// <summary>Affiche une liste, précédée du dernier dossier utilisé.</summary>
    private void ShowShortcuts(IEnumerable<DropShortcut> shortcuts)
    {
        _shortcuts.Clear();
        if (_last is not null)
            _shortcuts.Add(new DropShortcut(IconKind.History, "Dernier dossier utilisé", _last, _last));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_last is not null) seen.Add(_last);
        foreach (var shortcut in shortcuts)
            if (seen.Add(shortcut.Path)) _shortcuts.Add(shortcut);
    }

    private async Task LoadShortcutsAsync()
    {
        var ct = _closing.Token;
        try
        {
            var session = await EnsureSessionAsync();
            var found = _isWindows ? await DiscoverWindowsAsync(session, ct) : await DiscoverUnixAsync(session, ct);
            if (ct.IsCancellationRequested) return;

            var chosen = _pathChosen;
            ShowShortcuts(found);
            LoadingText.IsVisible = false;

            // Sans choix de l'utilisateur ni dossier précédent, le bureau du
            // premier profil — le plus récemment utilisé — est le plus probable.
            if (!chosen && _last is null && _shortcuts.FirstOrDefault(s => s.Glyph == IconKind.Desktop) is { } desktop)
            {
                ShortcutList.SelectedItem = desktop;
                _pathChosen = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Boîte refermée avant la fin de la lecture.
        }
        catch (Exception ex)
        {
            Log.Info($"{_node.Name} : dossiers courants illisibles ({ex.Message}).", "Bureau");
            LoadingText.Text = "Dossiers de la machine indisponibles";
        }
    }

    // MARK: - Tunnel de fichiers

    /// <summary>Tunnel partagé par la recherche des raccourcis et la navigation, ouvert le temps de la boîte.</summary>
    private FilesSession? _files;
    private Task<FilesSession>? _filesStart;

    private Task<FilesSession> EnsureSessionAsync()
    {
        if (_files is { State: FilesSessionState.Ready } ready) return Task.FromResult(ready);
        if (_filesStart is { IsCompleted: false } pending) return pending;
        return _filesStart = StartSessionAsync();
    }

    private async Task<FilesSession> StartSessionAsync()
    {
        _files?.Stop();
        var session = new FilesSession(_connection, _node, AvaloniaUiDispatcher.Instance);
        _files = session;

        await session.StartAsync(listOnStart: false);
        if (session.State != FilesSessionState.Ready)
            throw new MeshException(session.CloseReason ?? "Connexion à la machine impossible.");
        return session;
    }

    // MARK: - Navigation libre

    private readonly ObservableCollection<DropShortcut> _folders = new();

    /// <summary>Dossier affiché : chaîne vide pour la liste des disques sous Windows.</summary>
    private string _browsePath = "";

    /// <summary>Numéro de la dernière lecture : une réponse tardive ne remplace pas la suivante.</summary>
    private int _browseRequest;

    private void Browse_Click(object? sender, RoutedEventArgs e)
    {
        ShortcutsHeader.IsVisible = false;
        ShortcutList.IsVisible = false;
        BrowseHeader.IsVisible = true;
        FolderList.IsVisible = true;
        // On part du chemin déjà choisi : c'est souvent un voisin du bon dossier.
        _ = BrowseToAsync(Normalize(PathBox.Text ?? "") ?? RootPath);
    }

    private void BackToShortcuts_Click(object? sender, RoutedEventArgs e)
    {
        BrowseHeader.IsVisible = false;
        FolderList.IsVisible = false;
        ShortcutsHeader.IsVisible = true;
        ShortcutList.IsVisible = true;
    }

    private void Up_Click(object? sender, RoutedEventArgs e)
    {
        if (ParentOf(_browsePath) is { } parent) _ = BrowseToAsync(parent);
    }

    private void FolderList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FolderList.SelectedItem is not DropShortcut folder) return;
        _pathChosen = true;
        SetPath(folder.Path);
    }

    private void FolderList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FolderList.SelectedItem is DropShortcut folder) _ = BrowseToAsync(folder.Path);
    }

    /// <summary>
    /// Affiche les sous-dossiers d'un dossier, qui devient la destination. Un
    /// dossier introuvable — chemin saisi qui n'existe pas encore — cède la
    /// place à son parent.
    /// </summary>
    private async Task BrowseToAsync(string path)
    {
        var request = ++_browseRequest;
        _browsePath = path;
        BrowsePathText.Text = path.Length == 0 ? "Ce PC" : path;
        ToolTip.SetTip(BrowsePathText, BrowsePathText.Text);
        UpButton.IsEnabled = ParentOf(path) is not null;
        BrowseStatus.Text = "Lecture…";
        _folders.Clear();

        if (path.Length > 0)
        {
            _pathChosen = true;
            SetPath(path);
        }

        try
        {
            var session = await EnsureSessionAsync();
            var entries = await session.ListDirectoryAsync(path, _closing.Token);
            if (request != _browseRequest) return;

            if (entries is null)
            {
                if (ParentOf(path) is { } parent)
                {
                    await BrowseToAsync(parent);
                    return;
                }
                BrowseStatus.Text = "Dossier illisible";
                return;
            }

            var folders = entries
                .Where(e => e.Kind != FileKind.File)
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var entry in folders)
            {
                var name = entry.Name.TrimEnd('\\', '/');
                _folders.Add(entry.Kind == FileKind.Drive
                    ? new DropShortcut(IconKind.Drive, $"Disque {name}", "", name + @"\")
                    : new DropShortcut(IconKind.Folder, name, "", ChildOf(path, name)));
            }
            BrowseStatus.Text = folders.Count == 0 ? "Aucun sous-dossier" : "";
        }
        catch (OperationCanceledException)
        {
            // Boîte refermée pendant la lecture.
        }
        catch (Exception ex)
        {
            if (request != _browseRequest) return;
            BrowseStatus.Text = "Lecture impossible";
            Log.Info($"{_node.Name} : lecture de « {path} » impossible ({ex.Message}).", "Bureau");
        }
    }

    /// <summary>Point de départ : la liste des disques sous Windows, « / » ailleurs.</summary>
    private string RootPath => _isWindows ? "" : "/";

    private string ChildOf(string path, string name) => _isWindows
        ? path.TrimEnd('\\') + @"\" + name
        : path == "/" ? "/" + name : path + "/" + name;

    /// <summary>Dossier parent, ou null à la racine.</summary>
    private string? ParentOf(string path)
    {
        if (_isWindows)
        {
            if (path.Length == 0) return null;
            var trimmed = path.TrimEnd('\\');
            // « C:\ » remonte à la liste des disques.
            if (trimmed.Length <= 2) return "";
            var index = trimmed.LastIndexOf('\\');
            return index <= 2 ? trimmed[..2] + @"\" : trimmed[..index];
        }

        if (path == "/") return null;
        var slash = path.TrimEnd('/').LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private async Task<List<DropShortcut>> DiscoverWindowsAsync(FilesSession session, CancellationToken ct)
    {
        var result = new List<DropShortcut>();

        var profiles = (await session.ListDirectoryAsync(@"C:\Users", ct) ?? [])
            .Where(e => e.Kind == FileKind.Directory && !SystemProfiles.Contains(e.Name))
            .OrderByDescending(e => e.Modified ?? DateTime.MinValue)
            .Take(MaxProfiles)
            .ToList();

        // Lectures en parallèle : le tunnel les distingue par numéro de requête.
        var perProfile = await Task.WhenAll(profiles.Select(p => DiscoverWindowsProfileAsync(session, p.Name, ct)));
        foreach (var shortcuts in perProfile) result.AddRange(shortcuts);

        var generic = GenericShortcuts();
        result.AddRange(generic.Where(s => s.Glyph != IconKind.Drive));

        // Les disques, à la racine du système de fichiers de l'agent Windows.
        var drives = (await session.ListDirectoryAsync("", ct) ?? [])
            .Where(e => e.Kind == FileKind.Drive)
            .Select(e => e.Name.TrimEnd('\\', '/'))
            .ToList();
        if (drives.Count == 0) result.AddRange(generic.Where(s => s.Glyph == IconKind.Drive));
        foreach (var drive in drives)
            result.Add(new DropShortcut(IconKind.Drive, $"Disque {drive}", drive + @"\", drive + @"\"));

        return result;
    }

    /// <summary>
    /// Bureau, téléchargements et documents d'un profil Windows. Avec la
    /// sauvegarde des dossiers par OneDrive, le bureau affiché est celui de
    /// OneDrive — sous un nom traduit — et le dossier Desktop du profil, resté
    /// vide, induirait en erreur.
    /// </summary>
    private static async Task<List<DropShortcut>> DiscoverWindowsProfileAsync(
        FilesSession session, string user, CancellationToken ct)
    {
        var home = $@"C:\Users\{user}";
        var entries = await session.ListDirectoryAsync(home, ct);
        if (entries is null) return [];
        var dirs = DirectoryNames(entries);

        string? desktop = Find(dirs, "Desktop") is { } d ? $@"{home}\{d}" : null;
        string? downloads = Find(dirs, "Downloads") is { } dl ? $@"{home}\{dl}" : null;
        string? documents = Find(dirs, "Documents") is { } doc ? $@"{home}\{doc}" : null;
        string? oneDriveDocuments = null;
        var desktopInOneDrive = false;

        foreach (var oneDrive in dirs.Where(n => n.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)))
        {
            var root = $@"{home}\{oneDrive}";
            var inner = await session.ListDirectoryAsync(root, ct);
            if (inner is null) continue;
            var innerDirs = DirectoryNames(inner);

            if (Find(innerDirs, "Desktop", "Bureau", "Schreibtisch", "Escritorio", "Scrivania") is { } od)
            {
                desktop = $@"{root}\{od}";
                desktopInOneDrive = true;
            }
            if (Find(innerDirs, "Documents", "Dokumente", "Documentos", "Documenti") is { } odoc)
                oneDriveDocuments = $@"{root}\{odoc}";
        }

        var list = new List<DropShortcut>();
        if (desktop is not null)
            list.Add(new(IconKind.Desktop, desktopInOneDrive ? "Bureau (OneDrive)" : "Bureau", $"{user} · {desktop}", desktop));
        if (downloads is not null)
            list.Add(new(IconKind.Download, "Téléchargements", $"{user} · {downloads}", downloads));
        if (documents is not null)
            list.Add(new(IconKind.Document, "Documents", $"{user} · {documents}", documents));
        if (oneDriveDocuments is not null)
            list.Add(new(IconKind.Document, "Documents (OneDrive)", $"{user} · {oneDriveDocuments}", oneDriveDocuments));
        return list;
    }

    private async Task<List<DropShortcut>> DiscoverUnixAsync(FilesSession session, CancellationToken ct)
    {
        var mac = _node.OsFamily == OSFamily.MacOS;
        var homesRoot = mac ? "/Users" : "/home";

        var users = (await session.ListDirectoryAsync(homesRoot, ct) ?? [])
            .Where(e => e.Kind == FileKind.Directory && !e.Name.StartsWith('.') && e.Name != "Shared")
            .OrderByDescending(e => e.Modified ?? DateTime.MinValue)
            .Take(MaxProfiles)
            .Select(e => (Home: $"{homesRoot}/{e.Name}", User: e.Name))
            .ToList();
        if (!mac) users.Add(("/root", "root"));

        var perUser = await Task.WhenAll(users.Select(u => DiscoverUnixHomeAsync(session, u.Home, u.User, ct)));

        var result = perUser.SelectMany(s => s).ToList();
        result.AddRange(GenericShortcuts());
        return result;
    }

    private static async Task<List<DropShortcut>> DiscoverUnixHomeAsync(
        FilesSession session, string home, string user, CancellationToken ct)
    {
        var entries = await session.ListDirectoryAsync(home, ct);
        if (entries is null) return [];
        var dirs = DirectoryNames(entries);

        var list = new List<DropShortcut>();
        if (Find(dirs, UnixDesktopNames) is { } desktop)
            list.Add(new(IconKind.Desktop, "Bureau", $"{user} · {home}/{desktop}", $"{home}/{desktop}"));
        if (Find(dirs, UnixDownloadNames) is { } downloads)
            list.Add(new(IconKind.Download, "Téléchargements", $"{user} · {home}/{downloads}", $"{home}/{downloads}"));
        if (Find(dirs, UnixDocumentNames) is { } documents)
            list.Add(new(IconKind.Document, "Documents", $"{user} · {home}/{documents}", $"{home}/{documents}"));
        list.Add(new(IconKind.Home, "Dossier personnel", $"{user} · {home}", home));
        return list;
    }

    private static List<string> DirectoryNames(IEnumerable<FileEntry> entries) =>
        entries.Where(e => e.Kind == FileKind.Directory).Select(e => e.Name).ToList();

    /// <summary>Premier des noms candidats présent, sous son écriture réelle.</summary>
    private static string? Find(List<string> names, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = names.FirstOrDefault(n => string.Equals(n, candidate, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }

    // MARK: - Choix

    private void SetPath(string path)
    {
        _settingPath = true;
        PathBox.Text = path;
        PathBox.CaretIndex = path.Length;
        _settingPath = false;
    }

    private void PathBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ErrorText.IsVisible = false;
        if (_settingPath) return;
        // Saisie libre : le raccourci ou le dossier surligné ne correspond plus.
        _pathChosen = true;
        ShortcutList.SelectedItem = null;
        FolderList.SelectedItem = null;
    }

    private void PathBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Send_Click(sender, e);
    }

    private void ShortcutList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ShortcutList.SelectedItem is not DropShortcut shortcut) return;
        _pathChosen = true;
        SetPath(shortcut.Path);
    }

    private void ShortcutList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ShortcutList.SelectedItem is DropShortcut) Send_Click(sender, e);
    }

    private void Send_Click(object? sender, RoutedEventArgs e)
    {
        if (Normalize(PathBox.Text ?? "") is not { } path)
        {
            ErrorText.Text = _isWindows
                ? @"Indiquez un chemin complet, par exemple C:\Users\Public\Desktop."
                : "Indiquez un chemin complet, commençant par « / ».";
            ErrorText.IsVisible = true;
            PathBox.Focus();
            return;
        }

        LastByNode[_node.Id] = path;
        Close(path);
    }

    /// <summary>
    /// Chemin complet sous la forme attendue par l'agent, ou null s'il n'est
    /// pas absolu. Sous Windows, « C:/x » devient « C:\x » et « C: » devient « C:\ ».
    /// </summary>
    private string? Normalize(string text)
    {
        var path = text.Trim().Trim('"');
        if (_isWindows)
        {
            path = path.Replace('/', '\\');
            if (path.Length == 2 && path[1] == ':') path += @"\";
            if (!Regex.IsMatch(path, @"^[A-Za-z]:\\")) return null;
            return path.Length > 3 ? path.TrimEnd('\\') : path;
        }

        if (!path.StartsWith('/')) return null;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
