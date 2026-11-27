using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MeshControl.Core;

namespace MeshControl.UI;

public partial class MainWindow : ChromeWindow
{
    private MeshServerConnection? _connection;
    private AppSettings _settings = AppSettings.Load();
    private List<DeviceRow> _allRows = new();

    /// <summary>
    /// Rafraîchissement coalescé de la liste. Au démarrage, MeshCentral envoie une
    /// rafale d'événements (un par machine) ; reconstruire la vue groupée à chaque
    /// événement figeait l'interface. On ne reconstruit donc qu'une fois par salve.
    /// </summary>
    private readonly DispatcherTimer _refreshTimer = new()
    {
        // Sur un parc important, les événements de connexion arrivent en flux
        // continu. Un délai plus large regroupe davantage de changements par
        // mise à jour, sans que l'affichage paraisse en retard.
        Interval = TimeSpan.FromMilliseconds(800)
    };

    /// <summary>Raccourci global qui ouvre la palette de recherche rapide.</summary>
    private GlobalHotkey? _hotkey;
    private QuickSearchWindow? _quickSearch;

    /// <summary>Icône de la zone de notification.</summary>
    private TrayIcon? _tray;

    /// <summary>Vrai quand l'utilisateur quitte volontairement (menu « Quitter »).</summary>
    private bool _exiting;

    /// <summary>N'affiche l'info-bulle « toujours actif » qu'à la première réduction.</summary>
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        FitStartupSize();
        Motion.PulseWhileVisible(SkeletonRows);
        BuildFilterChips();
        ColumnHeaderAlign.Attach(DeviceList, scrollbar => ColumnHeader.Padding = new Thickness(10, 4, 10 + scrollbar, 7));
        CertificateTrust.Initialize(_settings);
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RebuildDeviceList();
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter();
        };
        _toastTimer.Tick += (_, _) => HideToast();
        _resyncTimer.Tick += (_, _) =>
        {
            _resyncTimer.Stop();
            _ = ResyncAsync();
        };
        Closing += MainWindow_Closing;

        // Échap referme le volet des réglages : réflexe attendu pour un panneau
        // superposé.
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && IsSettingsOpen)
            {
                CloseSettings();
                args.Handled = true;
                return;
            }

            // Ctrl+Maj+W ouvre la page web MeshCentral de la machine
            // sélectionnée, comme dans le bureau à distance et la palette.
            // Écarté quand les réglages sont ouverts : le champ de capture du
            // raccourci global doit pouvoir recevoir cette combinaison comme
            // n'importe quelle autre.
            if (args.Key == Key.W && !IsSettingsOpen
                && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                RunSelected(DeviceAction.WebPage);
                args.Handled = true;
            }

            // F2 renomme la machine sélectionnée, comme un fichier dans
            // l'Explorateur — sauf pendant une saisie, où F2 n'a rien à faire.
            if (args.Key == Key.F2 && !IsSettingsOpen && Keyboard.FocusedElement is not TextBox)
            {
                RunSelected(DeviceAction.Rename);
                args.Handled = true;
            }

            if (!args.Handled) HandleListShortcut(args);
        };
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _searchTimer.Stop();
            _toastTimer.Stop();
            _resyncTimer.Stop();
            ReleaseResumeWatch();
            _hotkey?.Dispose();
            _tray?.Dispose();
            _connection?.Disconnect();
            // ShutdownMode est OnExplicitShutdown : sans cet appel, le processus
            // resterait actif après la fermeture de la fenêtre principale.
            Application.Current.Shutdown();
        };
    }

    /// <summary>
    /// Taille d'ouverture : 1 240 × 820, assez pour la barre latérale et la
    /// liste en colonnes côte à côte, ramenée à 92 % de la zone de travail sur
    /// un petit écran pour que la fenêtre n'en déborde jamais.
    /// </summary>
    private void FitStartupSize()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width * 0.92));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height * 0.92));
    }

    // MARK: - Zone de notification

    private void SetupTray()
    {
        _tray = new TrayIcon();
        _tray.OpenRequested += RestoreMainWindow;
        _tray.QuickSearchRequested += ShowQuickSearch;
        _tray.BalloonClicked += OnBalloonClicked;
        _tray.ExitRequested += () =>
        {
            _exiting = true;
            Close();
        };
        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        if (_tray is null) return;
        if (_connection?.State == ConnectionState.Connected)
        {
            var online = _allRows.Count(r => r.IsOnline);
            _tray.SetTooltip($"Mesh Control — {online} machine(s) en ligne");
        }
        else
        {
            _tray.SetTooltip("Mesh Control — non connecté");
        }
    }

    /// <summary>
    /// La croix de fermeture réduit dans la zone de notification plutôt que de
    /// quitter : l'application doit rester joignable par le raccourci global.
    /// Le menu « Quitter » du tray est la seule sortie réelle.
    /// </summary>
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || _settings.MinimizeToTray == false) return;

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            ShowTrayMessage("Mesh Control reste actif",
                $"L'application continue de tourner ici. Raccourci : {_settings.QuickSearchHotkey}");
        }
    }

    /// <summary>
    /// Démarre l'application : affiche la fenêtre, ou non, puis se connecte.
    ///
    /// Démarrage automatique de session (« --minimized ») ou réglage explicite :
    /// on se connecte en tâche de fond sans ouvrir la fenêtre. Elle n'est alors
    /// jamais affichée — seul son handle est créé, dont ont besoin le raccourci
    /// global et le premier plan. La masquer depuis Loaded, comme auparavant,
    /// intervenait en plein premier affichage : Windows laissait la fenêtre à
    /// l'écran tandis que WPF, la croyant masquée, cessait de la dessiner. Elle
    /// restait noire jusqu'à un passage par la zone de notification.
    /// </summary>
    public async void Start()
    {
        // Sans serveur renseigné, il n'y a rien à faire en tâche de fond : la
        // fenêtre s'ouvre sur le choix du serveur, quels que soient les réglages.
        bool hasHost = !string.IsNullOrWhiteSpace(_settings.Host);
        // « --show » vient d'un redémarrage demandé par l'utilisateur (changement
        // de thème) : il attend la fenêtre, quel que soit le démarrage réduit.
        var args = Environment.GetCommandLineArgs();
        bool forceShow = args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase));
        bool startHidden = hasHost && !forceShow
            && (_settings.StartMinimized
                || args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)));
        if (startHidden)
            new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        else
            Show();

        Log.MinimumLevel = _settings.VerboseLogging ? LogLevel.Debug : LogLevel.Info;
        // Restaurer les dossiers repliés du lancement précédent.
        _collapsedGroups = new HashSet<string>(_settings.CollapsedGroups,
                                               StringComparer.OrdinalIgnoreCase);
        ApplySettingsToUi();
        ApplyListDensity();
        SetupHotkey();
        SetupTray();
        SetupResumeWatch();

        if (startHidden) HideToTray();

        // Connexion automatique si des identifiants sont mémorisés : l'utilisateur
        // n'a plus rien à ressaisir au lancement. Un jeton de connexion suffit,
        // même sans mot de passe enregistré.
        if (hasHost
            && _settings.RememberCredentials
            && !string.IsNullOrEmpty(_settings.Username)
            && (!string.IsNullOrEmpty(_settings.Password) || _settings.HasLoginToken))
        {
            await ConnectAsync(_settings.Username, _settings.Password, token: null, remember: true);
        }
        else
        {
            ShowLogin();
        }

        // Après la connexion, pour ne pas retarder l'affichage de la liste.
        // Volontairement non attendu : une recherche lente ou en échec ne doit
        // jamais bloquer le démarrage.
        if (_settings.CheckForUpdates)
            _ = CheckForUpdatesAsync(manual: false);
    }

    private void ApplySettingsToUi()
    {
        LoginServerLabel.Text = _settings.Host;
        // Sans serveur, l'en-tête garderait sinon un vide à la place du nom.
        SetHeader(string.IsNullOrEmpty(_settings.Host) ? "Contrôle à distance" : _settings.Host, null);
        UserBox.Text = _settings.Username;
        RememberCheck.IsChecked = _settings.RememberCredentials;
        ApplyFolderView();
        UpdateSortButton();

        // Rappel visuel du raccourci de recherche rapide. On le reformate au
        // passage : un réglage enregistré avant la francisation contiendrait
        // encore des noms internes WPF (« OemQuotes »), illisibles.
        if (_settings.QuickSearchEnabled
            && AppSettings.ParseHotkey(_settings.QuickSearchHotkey) is { } parsed)
        {
            var readable = AppSettings.FormatHotkey(parsed.Modifiers, parsed.Key);
            if (readable != _settings.QuickSearchHotkey)
            {
                _settings.QuickSearchHotkey = readable;
                _settings.Save();
            }
            HotkeyHintText.Text = readable;
            HotkeyHintBadge.Visibility = Visibility.Visible;
        }
        else
        {
            HotkeyHintBadge.Visibility = Visibility.Collapsed;
        }
    }

    // MARK: - Raccourci global & recherche rapide

    /// <summary>(Ré)enregistre le raccourci global selon les réglages.</summary>
    private void SetupHotkey()
    {
        _hotkey ??= new GlobalHotkey(this);
        _hotkey.Pressed -= ShowQuickSearch;
        _hotkey.Unregister();

        if (!_settings.QuickSearchEnabled) return;

        var parsed = AppSettings.ParseHotkey(_settings.QuickSearchHotkey);
        if (parsed is null) return;

        _hotkey.Pressed += ShowQuickSearch;
        if (!_hotkey.Register(parsed.Value.Modifiers, parsed.Value.Key))
        {
            // Windows refuse le raccourci : presque toujours un conflit avec une
            // autre application. On le signale sans bloquer le reste de l'app.
            SetHeader(_settings.Host, $"raccourci {_settings.QuickSearchHotkey} indisponible (déjà utilisé)",
                      HeaderStatus.Warning);
        }
    }

    /// <summary>
    /// Ouvre la palette de recherche rapide par-dessus les autres applications.
    /// Déclenché par le raccourci global, même si Mesh Control est en arrière-plan.
    /// </summary>
    private void ShowQuickSearch()
    {
        // Si la palette est déjà ouverte, le raccourci la ferme (bascule).
        // try/catch : elle peut être en cours de fermeture (perte de focus).
        if (_quickSearch is not null)
        {
            try { _quickSearch.Close(); } catch { /* déjà en fermeture */ }
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
            node => _settings.IsFavorite(node.Id),
            _settings.RecentNodeIds);
        _quickSearch.Closed += (_, _) => _quickSearch = null;
        _quickSearch.Show();
        _quickSearch.Activate();

        // Windows restreint l'activation depuis une application en arrière-plan :
        // Activate() seul ne suffit pas toujours à obtenir le focus clavier.
        var handle = new System.Windows.Interop.WindowInteropHelper(_quickSearch).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// Action lancée depuis la palette : on n'affiche pas la fenêtre principale,
    /// le but étant justement de rester dans son contexte de travail.
    /// </summary>
    private void RunActionFromQuickSearch(MeshNode node, DeviceAction action)
    {
        if (_connection is null) return;

        if (!IsActionAvailable(node, action))
        {
            MessageBox.Show(
                !node.IsOnline
                    ? $"« {node.Name} » est hors ligne."
                    : $"« {node.Name} » n'a pas d'agent installé.",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RunAction(node, action);
    }

    // MARK: - Vue à deux volets

    /// <summary>Dossier choisi dans le volet. Null ou AllKey : tout le parc.</summary>
    private string? _selectedFolderKey;

    /// <summary>
    /// Vrai le temps de rétablir la sélection du volet par programme : sans
    /// cela, chaque reconstruction relancerait un filtrage et un enregistrement
    /// des réglages.
    /// </summary>
    private bool _restoringFolder;

    private void ToggleView_Click(object sender, RoutedEventArgs e)
    {
        _settings.FolderView = !_settings.FolderView;
        _settings.Save();
        var width = Math.Clamp(_settings.FolderPaneWidth, MinFolderPaneWidth, MaxFolderPaneWidth);

        // La barre latérale s'ouvre en glissant ; à la fermeture, elle reste
        // affichée le temps de se replier, puis disparaît.
        _animateList = true;
        if (_settings.FolderView)
        {
            ApplyFolderView();
            RebuildFolderPanel();
            ApplyFilter();
            Motion.Width(FolderPane, 0, width);
        }
        else
        {
            ApplyFolderView(keepPaneForAnimation: true);
            ApplyFilter();
            Motion.Width(FolderPane, width, 0, completed: () =>
            {
                if (!_settings.FolderView) FolderPane.Visibility = Visibility.Collapsed;
            });
        }
    }

    /// <summary>Montre ou masque le volet, et met le bouton en accord.</summary>
    private void ApplyFolderView(bool keepPaneForAnimation = false)
    {
        bool on = _settings.FolderView;
        if (!keepPaneForAnimation) FolderPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FolderPane.Width = Math.Clamp(_settings.FolderPaneWidth, MinFolderPaneWidth, MaxFolderPaneWidth);
        ToggleViewButton.ToolTip = on ? "Masquer la barre latérale (liste en colonnes)"
                                      : "Afficher la barre latérale";
        ToggleViewGlyph.Foreground = (System.Windows.Media.Brush)FindResource(
            on ? "AccentBrush" : "TextSecondaryBrush");

        // Barre latérale : titre de la vue avec son compteur, lignes en carte.
        // Masquée : liste dense en colonnes, compteur dans la barre d'outils.
        SidebarTitleRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ColumnHeader.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (CountText.Parent is Panel current) current.Children.Remove(CountText);
        if (on) TitleCountHost.Children.Add(CountText);
        else ToolbarRight.Children.Insert(0, CountText);

        DeviceList.ItemTemplateSelector = new DeviceListTemplateSelector
        {
            Header = (DataTemplate)DeviceList.FindResource("GroupHeaderTemplate"),
            Card = (DataTemplate)DeviceList.FindResource("DeviceCardTemplate"),
            Columns = (DataTemplate)DeviceList.FindResource("DeviceColumnsTemplate"),
            UseColumns = !on,
        };
        ApplyColumnsPadding();

        // Favoris et récentes sont des vues de la barre latérale : leurs
        // pastilles feraient doublon.
        if (on && _activeFilters.Remove(FilterGroup.Set)) SyncFilterChips();
        UpdateFilterChips();
    }

    /// <summary>
    /// Lignes en colonnes : plus basses que les cartes, d'après la densité
    /// choisie. La marge gauche de l'en-tête suit celle des lignes.
    /// </summary>
    private void ApplyColumnsPadding()
    {
        if (_settings.FolderView)
        {
            DeviceList.Resources.Remove("DeviceRowPadding");
            return;
        }
        var vertical = _settings.ListDensity switch { 0 => 2.0, 2 => 7.0, _ => 4.0 };
        DeviceList.Resources["DeviceRowPadding"] = new Thickness(10, vertical, 10, vertical);
    }

    /// <summary>Titre de la vue, dans la disposition à barre latérale.</summary>
    private void UpdateViewTitle(string query)
    {
        if (!_settings.FolderView) return;
        ViewTitleText.Text = !string.IsNullOrEmpty(query)
            ? $"Recherche « {query} »"
            : _folderRows.FirstOrDefault(f => f.Key == (_selectedFolderKey ?? FolderRow.AllKey)) is { } folder
                ? folder.Name
                : "Toutes les machines";
    }

    /// <summary>Clic sur un en-tête de colonne : trie la liste par cette colonne.</summary>
    private void ColumnHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var mode)) return;
        _settings.DeviceSort = mode;
        _settings.Save();
        UpdateSortButton();
        _animateList = true;
        ApplyFilter();
    }

    /// <summary>
    /// Reconstruit le volet depuis <c>_allRows</c>. Appelé quand la composition
    /// du parc change, et non à chaque événement serveur : la sélection serait
    /// perdue en continu. Elle est rétablie par clé, le volet étant recréé.
    /// </summary>
    private void RebuildFolderPanel()
    {
        if (!_settings.FolderView) return;

        // Segoe MDL2 Assets : E71D liste complète, E735 étoile, ED25 dossier,
        // E718 punaise, ED1A œil barré. Attention, E8B7 — que la documentation
        // donne pour « Folder » — ne dessine rien dans cette police et
        // s'affiche en carré vide.
        // Vues en tête (toutes, favoris, récentes, en session), puis les
        // dossiers. Les intitulés de section ne se sélectionnent pas.
        var rows = new List<FolderRow>
        {
            FolderRow.Section("Vues"),
            new(FolderRow.AllKey, "Toutes les machines", "\uE71D", "#8A8A8E", 0, 0),
        };

        if (_allRows.Any(r => r.IsFavorite))
            rows.Add(new FolderRow(FavoritesGroup, "Favoris", "\uE735", "#FFB300", 0, 0));
        if (_settings.RecentNodeIds.Count > 0)
            rows.Add(new FolderRow(FolderRow.RecentKey, "Récentes", "\uE81C", "#8A8A8E", 0, 0));
        rows.Add(new FolderRow(FolderRow.SessionKey, "En session", "\uE77B", "#0A84FF", 0, 0));
        rows.Add(FolderRow.Section("Dossiers"));

        // Dossiers épinglés juste sous les favoris, puis les autres ; les
        // masqués sont regroupés sous une entrée unique, en bas.
        var folders = _allRows
            .Select(r => r.RealGroupName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !IsHiddenFolder(name))
            .OrderBy(name => IsPinnedFolder(name) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in folders)
        {
            rows.Add(IsPinnedFolder(name)
                ? new FolderRow(name, name, "", "#0A84FF", 0, 0, isPinned: true)
                : new FolderRow(name, name, "", "#8A8A8E", 0, 0));
        }

        var hidden = HiddenFolderNames().Count;
        if (hidden > 0)
            rows.Add(new FolderRow(FolderRow.HiddenKey,
                hidden == 1 ? "1 dossier masqué" : $"{hidden} dossiers masqués", "", "#8A8A8E", 0, 0));

        _folderRows = rows;
        RefreshFolderCounts();
        var wanted = _selectedFolderKey ?? _settings.SelectedFolder ?? FolderRow.AllKey;
        _selectedFolderKey = rows.Any(f => f.Key == wanted) ? wanted : FolderRow.AllKey;
        ShowFolderRows();
    }

    /// <summary>Toutes les entrées du volet, avant le filtre des dossiers.</summary>
    private List<FolderRow> _folderRows = new();

    /// <summary>
    /// Donne au volet les entrées qui répondent au filtre des dossiers, et y
    /// rétablit la sélection sans relancer le filtrage des machines.
    ///
    /// La liste lui est passée déjà filtrée : le filtre de la vue WPF
    /// (Items.Filter puis Items.Refresh) n'était pas appliqué, et le volet
    /// gardait tous ses dossiers quoi qu'on tape.
    /// </summary>
    private void ShowFolderRows()
    {
        var query = FolderFilterBox.Text.Trim();
        // Pendant un filtre, les intitulés de section n'ont plus rien à ranger.
        var visible = query.Length == 0
            ? _folderRows
            : _folderRows.Where(f => !f.IsSection
                                     && (!f.IsRealFolder || TextMatch.Contains(f.Name, query))).ToList();

        _restoringFolder = true;
        FolderList.ItemsSource = visible;
        FolderList.SelectedItem = visible.FirstOrDefault(f => f.Key == _selectedFolderKey);
        _restoringFolder = false;
    }

    // Ensembles tirés des réglages, consultés pour chaque machine à chaque
    // filtrage : une recherche dans une liste coûtait un parcours par machine.
    // Remis à null quand la liste d'origine change.
    private HashSet<string>? _pinnedSet;
    private HashSet<string>? _hiddenSet;
    private HashSet<string>? _recentSet;

    private bool IsPinnedFolder(string name)
        => (_pinnedSet ??= new HashSet<string>(_settings.PinnedFolders, StringComparer.OrdinalIgnoreCase))
            .Contains(name);

    private bool IsHiddenFolder(string name)
        => (_hiddenSet ??= new HashSet<string>(_settings.HiddenFolders, StringComparer.OrdinalIgnoreCase))
            .Contains(name);

    private bool IsRecent(string nodeId)
        => (_recentSet ??= new HashSet<string>(_settings.RecentNodeIds)).Contains(nodeId);

    /// <summary>Dossiers masqués qui existent encore dans le parc.</summary>
    private List<string> HiddenFolderNames() => _allRows
        .Select(r => r.RealGroupName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(IsHiddenFolder)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Machines d'une entrée du volet, en ligne ou non.</summary>
    private IEnumerable<DeviceRow> RowsOfFolder(string key) => key switch
    {
        FolderRow.AllKey => _allRows.Where(r => !IsHiddenFolder(r.RealGroupName)),
        FolderRow.HiddenKey => _allRows.Where(r => IsHiddenFolder(r.RealGroupName)),
        FavoritesGroup => _allRows.Where(r => r.IsFavorite),
        FolderRow.RecentKey => _allRows.Where(r => IsRecent(r.Node.Id)),
        FolderRow.SessionKey => _allRows.Where(r => r.IsOnline && r.Node.Sessions.Count > 0),
        _ => _allRows.Where(r => string.Equals(r.RealGroupName, key, StringComparison.OrdinalIgnoreCase)),
    };

    /// <summary>
    /// Compteurs du volet, mis à jour en place à chaque événement du serveur :
    /// sans cela, une machine qui se connecte ne faisait pas bouger le
    /// compteur de son dossier tant que la composition du parc restait la même.
    /// </summary>
    private void RefreshFolderCounts()
    {
        if (!_settings.FolderView) return;
        var folders = _folderRows;

        // Sans filtre, le volet compte les machines en ligne ; avec des
        // pastilles actives, celles qui y répondent : le chiffre d'un dossier
        // annonce alors ce que la liste montrera en l'ouvrant.
        var filtered = AnyFilterActive;
        Func<DeviceRow, bool> matches = filtered ? r => MatchesActiveFilters(r) : r => r.IsOnline;

        var byFolder = _allRows
            .GroupBy(r => r.RealGroupName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Total: g.Count(), Matching: g.Count(matches)),
                          StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            if (folder.IsSection) continue;
            if (folder.IsRealFolder)
            {
                var (total, matching) = byFolder.GetValueOrDefault(folder.Key);
                folder.SetCounts(total, matching, filtered);
            }
            else
            {
                var rows = RowsOfFolder(folder.Key).ToList();
                folder.SetCounts(rows.Count, rows.Count(matches), filtered);
            }
        }
    }

    // MARK: - Volet des dossiers : filtre, largeur, menu

    private void FolderFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = string.IsNullOrEmpty(FolderFilterBox.Text);
        FolderFilterPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearFolderFilterButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ShowFolderRows();
    }

    /// <summary>Entrée ouvre le premier dossier qui correspond ; Échap efface le filtre.</summary>
    private void FolderFilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FolderFilterBox.Text.Length > 0)
        {
            FolderFilterBox.Clear();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down)
        {
            var first = FolderList.Items.OfType<FolderRow>().FirstOrDefault(f => f.IsRealFolder);
            if (first is not null) FolderList.SelectedItem = first;
            e.Handled = true;
        }
    }

    private void ClearFolderFilter_Click(object sender, RoutedEventArgs e)
    {
        FolderFilterBox.Clear();
        FolderFilterBox.Focus();
    }

    private const double MinFolderPaneWidth = 180;
    private const double MaxFolderPaneWidth = 520;

    private void FolderPaneResizer_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        => FolderPane.Width = Math.Clamp(FolderPane.Width + e.HorizontalChange,
                                         MinFolderPaneWidth, MaxFolderPaneWidth);

    private void FolderPaneResizer_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _settings.FolderPaneWidth = FolderPane.Width;
        _settings.Save();
    }

    /// <summary>
    /// Clic droit sur un dossier : actions sur ses machines, et rangement du
    /// volet (épingler, masquer). Le menu est composé à chaque ouverture, ses
    /// entrées dépendant du dossier visé et de l'état de ses machines.
    /// </summary>
    private void FolderList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FolderList, (DependencyObject)e.OriginalSource)
                is not ListBoxItem { DataContext: FolderRow folder }) return;
        e.Handled = true;

        var menu = new ContextMenu
        {
            PlacementTarget = FolderList,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };

        void Add(string header, bool enabled, Action action)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        // Actions sur les machines : pas sur « Toutes les machines », où un
        // clic malheureux toucherait tout le parc.
        if (folder.Key != FolderRow.AllKey)
        {
            var nodes = RowsOfFolder(folder.Key).Select(r => r.Node).ToList();
            var offline = nodes.Where(n => !n.IsOnline).ToList();
            var reachable = nodes.Where(IsReachable).ToList();

            Add($"Réveiller les machines hors ligne ({offline.Count})", offline.Count > 0,
                () => RunBulk(offline, DeviceAction.Wake));
            Add($"Envoyer des fichiers aux machines en ligne ({reachable.Count})", reachable.Count > 0,
                () => OpenSendFiles(reachable));
            var anyUnwatched = offline.Any(n => !_watchOnline.Contains(n.Id));
            Add(anyUnwatched || offline.Count == 0
                    ? $"Me prévenir du retour des machines hors ligne ({offline.Count})"
                    : $"Ne plus me prévenir de leur retour ({offline.Count})",
                offline.Count > 0, () => SetWatched(offline, anyUnwatched));
        }

        if (folder.IsRealFolder)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            Add(folder.IsPinned ? "Désépingler" : "Épingler en haut du volet", true,
                () => TogglePinnedFolder(folder.Key));
            Add("Masquer ce dossier", true, () => SetFolderHidden(folder.Key, true));
        }
        else if (folder.Key == FolderRow.HiddenKey)
        {
            menu.Items.Add(new Separator());
            foreach (var name in HiddenFolderNames())
                Add($"Réafficher « {name} »", true, () => SetFolderHidden(name, false));
            Add("Tout réafficher", true, () =>
            {
                _settings.HiddenFolders.Clear();
                AfterFolderLayoutChanged();
            });
        }

        if (menu.Items.Count > 0) menu.IsOpen = true;
    }

    private void TogglePinnedFolder(string name)
    {
        if (_settings.PinnedFolders.RemoveAll(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)) == 0)
            _settings.PinnedFolders.Add(name);
        AfterFolderLayoutChanged();
    }

    private void SetFolderHidden(string name, bool hidden)
    {
        _settings.HiddenFolders.RemoveAll(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        if (hidden)
        {
            _settings.HiddenFolders.Add(name);
            // Le dossier affiché disparaît du volet : on revient à tout le parc.
            if (string.Equals(_selectedFolderKey, name, StringComparison.OrdinalIgnoreCase))
                _selectedFolderKey = FolderRow.AllKey;
            ShowToast($"« {name} » est masqué. Il reste accessible sous « dossiers masqués », en bas du volet.");
        }
        AfterFolderLayoutChanged();
    }

    /// <summary>Enregistre l'ordre du volet et recompose volet et liste.</summary>
    private void AfterFolderLayoutChanged()
    {
        _pinnedSet = null;
        _hiddenSet = null;
        _settings.Save();
        RebuildFolderPanel();
        ApplyFilter();
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringFolder) return;
        if (FolderList.SelectedItem is not FolderRow folder) return;

        _selectedFolderKey = folder.Key;
        _animateList = true;
        _settings.SelectedFolder = folder.Key;
        _settings.Save();
        ApplyFilter();
    }

    // MARK: - Mises à jour

    /// <summary>
    /// Cherche une version plus récente et la propose. Silencieux quand il n'y a
    /// rien : au démarrage, on ne veut aucune boîte de dialogue inutile.
    /// </summary>
    /// <param name="manual">
    /// Vrai pour une recherche demandée explicitement : on signale alors aussi
    /// l'absence de mise à jour, et on ignore la version précédemment refusée.
    /// </param>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        var update = await UpdateService.CheckAsync().ConfigureAwait(true);

        if (update is null)
        {
            if (manual)
            {
                MessageBox.Show(
                    UpdateService.IsAvailable
                        ? $"Mesh Control {UpdateService.CurrentVersion} est à jour."
                        : "Les mises à jour ne sont proposées que sur une version installée.",
                    "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        // Version déjà refusée : on ne réinsiste pas à chaque démarrage.
        if (!manual && _settings.SkippedUpdateVersion == UpdateService.TargetVersion(update))
            return;

        // Fenêtre principale masquée dans la zone de notification : l'afficher,
        // sinon la boîte de dialogue surgirait sans contexte.
        if (!IsVisible) RestoreMainWindow();

        // Une installation menée à bien ne revient pas ici : Velopack remplace le
        // processus par la nouvelle version. Si l'on reprend la main, c'est que
        // l'utilisateur a refusé ou que l'installation a échoué.
        if (new UpdateWindow(update) { Owner = this }.ShowDialog() != true)
        {
            _settings.SkippedUpdateVersion = UpdateService.TargetVersion(update);
            _settings.Save();
        }
    }

    private void RestoreMainWindow()
    {
        // L'utilisateur revient regarder la liste : c'est le moment de vérifier
        // qu'elle est juste, sans attendre la resynchronisation périodique.
        Resync("retour dans la fenêtre");

        Show();   // la fenêtre peut avoir été masquée dans le tray
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        // Même contrainte que pour la palette : forcer le premier plan quand
        // l'appel vient d'une application en arrière-plan.
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
    }

    // MARK: - Bascule de vues

    private void ShowLogin()
    {
        ConnectionDot.Visibility = Visibility.Collapsed;
        // Premier lancement : sans adresse de serveur, se connecter n'a aucun
        // sens. On commence par la demander.
        if (string.IsNullOrWhiteSpace(_settings.Host))
        {
            ShowServerSetup();
            return;
        }

        ServerPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        DevicesPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        RefreshButton.Visibility = Visibility.Collapsed;

        // Le second facteur ne s'affiche que lorsque le serveur le réclame :
        // revenir à l'écran de connexion repart d'un formulaire propre.
        TwoFactorPanel.Visibility = Visibility.Collapsed;
        SendCodeButton.Visibility = Visibility.Collapsed;
        TokenBox.Clear();

        LoginServerLabel.Text = _settings.Host;
        ConnectButton.IsEnabled = true;

        if (string.IsNullOrEmpty(UserBox.Text)) UserBox.Focus();
        else PassBox.Focus();
    }

    /// <summary>
    /// Choix du serveur : premier démarrage, ou « Changer » depuis l'écran de
    /// connexion. Plus direct que les réglages, où l'adresse se perd parmi
    /// une vingtaine d'options qu'un nouvel utilisateur n'a pas à voir.
    /// </summary>
    private void ShowServerSetup()
    {
        bool firstRun = string.IsNullOrWhiteSpace(_settings.Host);
        ServerTitle.Text = firstRun ? "Bienvenue" : "Changer de serveur";
        ServerHostBox.Text = _settings.Host;
        ServerSelfSignedCheck.IsChecked = _settings.AllowSelfSigned;
        ServerStatusText.Text = "";
        ServerContinueButton.IsEnabled = true;

        ServerPanel.Visibility = Visibility.Visible;
        LoginPanel.Visibility = Visibility.Collapsed;
        DevicesPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        RefreshButton.Visibility = Visibility.Collapsed;

        ServerHostBox.Focus();
        ServerHostBox.SelectAll();
    }

    private void ChangeServer_Click(object sender, RoutedEventArgs e) => ShowServerSetup();

    private void ServerHostBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ServerContinue_Click(sender, e);
    }

    /// <summary>
    /// Vérifie que le serveur répond, puis l'enregistre et passe à la connexion.
    /// </summary>
    private async void ServerContinue_Click(object sender, RoutedEventArgs e)
    {
        if (!ServerContinueButton.IsEnabled) return;

        var profile = new ServerProfile
        {
            Host = ServerHostBox.Text.Trim(),
            AllowSelfSigned = ServerSelfSignedCheck.IsChecked == true,
        };
        if (profile.BaseUri is not { } uri)
        {
            SetServerStatus(string.IsNullOrEmpty(profile.Host)
                ? "Renseignez l'adresse du serveur."
                : "Cette adresse n'est pas valide.", isError: true);
            ServerHostBox.Focus();
            return;
        }

        // Retenue sous sa forme canonique, nom[:port] : le schéma et la barre
        // finale qu'on colle souvent depuis le navigateur n'ont rien à y faire.
        profile.Host = uri.Authority;

        ServerContinueButton.IsEnabled = false;
        SetServerStatus($"Vérification de {profile.Host}…");
        var error = await MeshServerConnection.ProbeAsync(profile);
        ServerContinueButton.IsEnabled = true;
        if (error is not null)
        {
            SetServerStatus(error, isError: true);
            ServerHostBox.Focus();
            return;
        }

        // Un autre serveur, c'est un autre compte : ce qui ouvrait une session
        // sur l'ancien n'a plus cours.
        if (!string.Equals(profile.Host, _settings.Host, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(_settings.Host))
        {
            _settings.ProtectedPassword = null;
            _settings.ClearDeviceTrust();
        }

        _settings.Host = profile.Host;
        _settings.AllowSelfSigned = profile.AllowSelfSigned;
        _settings.SaveNow();
        Log.Info($"Serveur choisi : {profile.Host}", "Réglages");

        ApplySettingsToUi();
        SetStatus("");
        ShowLogin();
    }

    private void SetServerStatus(string text, bool isError = false)
    {
        ServerStatusText.Text = text;
        ServerStatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
    }

    /// <summary>
    /// Durée, en jours, pendant laquelle le serveur accepte qu'un poste soit
    /// mémorisé. Annoncée avec la demande de code ; zéro quand l'administrateur
    /// a désactivé l'option.
    /// </summary>
    private int _trustedDeviceDays;

    /// <summary>Canaux d'envoi de code proposés par le serveur, pour SendCode_Click.</summary>
    private string? _sendCodeToken;

    /// <summary>
    /// Affiche la demande de code à deux facteurs, avec les seules options que
    /// le serveur annonce : mémorisation du poste, envoi d'un code.
    /// </summary>
    private void ShowTwoFactorPrompt(MeshTwoFactorRequiredException request)
    {
        ShowLogin();

        _trustedDeviceDays = request.TrustedDeviceDays;
        TwoFactorPanel.Visibility = Visibility.Visible;

        // Mémoriser le poste n'a de sens que si le serveur l'autorise.
        if (_trustedDeviceDays > 0)
        {
            TrustDeviceCheck.Visibility = Visibility.Visible;
            TrustDeviceHint.Visibility = Visibility.Visible;
            TrustDeviceHint.Text = _trustedDeviceDays == 1
                ? "Le serveur autorise ce poste pour une journée."
                : $"Le serveur autorise ce poste pour {_trustedDeviceDays} jours. "
                + "Les réglages permettent ensuite de l'autoriser sans limite.";
        }
        else
        {
            TrustDeviceCheck.Visibility = Visibility.Collapsed;
            TrustDeviceHint.Visibility = Visibility.Collapsed;
        }

        // Codes envoyés par le serveur : un seul bouton, pour le premier canal
        // disponible.
        _sendCodeToken = request.EmailAvailable ? "**email**"
                       : request.SmsAvailable ? "**sms**"
                       : request.MessengerAvailable ? "**msg**"
                       : null;
        if (_sendCodeToken is not null && !request.CodeSent)
        {
            SendCodeButton.Content = _sendCodeToken switch
            {
                "**email**" => "Recevoir un code par courriel",
                "**sms**" => "Recevoir un code par SMS",
                _ => "Recevoir un code par messagerie",
            };
            SendCodeButton.Visibility = Visibility.Visible;
        }

        TokenBox.Focus();
    }

    /// <summary>Demande au serveur d'envoyer un code (courriel, SMS, messagerie).</summary>
    private async void SendCode_Click(object sender, RoutedEventArgs e)
    {
        if (_sendCodeToken is null) return;
        await ConnectAsync(UserBox.Text.Trim(), PassBox.Password, _sendCodeToken,
                           RememberCheck.IsChecked == true);
    }

    private void ShowLoading(string detail = "")
    {
        ServerPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Collapsed;
        DevicesPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        RefreshButton.Visibility = Visibility.Collapsed;
        LoadingDetail.Text = detail;
    }

    private void ShowDevices()
    {
        ServerPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        if (DevicesPanel.Visibility != Visibility.Visible)
        {
            DevicesPanel.Visibility = Visibility.Visible;
            Motion.FadeIn(DevicesPanel, 0, 240, slideY: 10);
        }
        RefreshButton.Visibility = Visibility.Visible;
    }

    // MARK: - Connexion

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        await ConnectAsync(
            UserBox.Text.Trim(),
            PassBox.Password,
            string.IsNullOrWhiteSpace(TokenBox.Text) ? null : TokenBox.Text.Trim(),
            RememberCheck.IsChecked == true,
            manual: true);
    }

    private void PassBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ConnectButton_Click(sender, e);
    }

    /// <summary>
    /// Compose les identifiants présentés au serveur.
    ///
    /// Un code fraîchement saisi l'emporte sur tout : c'est lui qui permet de
    /// (re)mémoriser le poste. Sinon on présente ce qui évite la ressaisie —
    /// le jeton de connexion s'il existe, le cookie d'appareil de confiance
    /// à défaut.
    /// </summary>
    private MeshLogin BuildLogin(string password, string? token, bool manual = false)
    {
        // Un mot de passe tapé à l'instant l'emporte sur le jeton. Sans cela,
        // posséder un jeton interdit toute autre connexion : le jeton est
        // présenté quoi qu'on saisisse, et comme il ne déclenche jamais de
        // second facteur, le champ du code ne s'affiche plus jamais — on ne peut
        // donc ni changer de compte, ni réautoriser le poste.
        //
        // La reconnexion automatique, elle, continue de préférer le jeton : elle
        // n'a personne pour saisir un code si le serveur en réclamait un.
        bool passwordWins = manual && !string.IsNullOrEmpty(password);

        if (string.IsNullOrEmpty(token) && _settings.HasLoginToken && !passwordWins)
        {
            return new MeshLogin
            {
                Password = _settings.LoginTokenPass,
                LoginTokenUser = _settings.LoginTokenUser,
            };
        }

        return new MeshLogin
        {
            Password = password,
            TwoFactorToken = token,
            TwoFactorCookie = string.IsNullOrEmpty(token) && _settings.HasTwoFactorCookie
                ? _settings.TwoFactorCookie
                : null,
        };
    }

    /// <param name="manual">
    /// Vrai quand l'utilisateur vient de remplir le formulaire : sa saisie prime
    /// alors sur le jeton de connexion mémorisé (voir BuildLogin).
    /// </param>
    private async Task ConnectAsync(string user, string password, string? token,
                                    bool remember, bool manual = false)
    {
        // Le jeton de connexion remplace le mot de passe : il suffit à ouvrir
        // la session, même si le mot de passe du compte n'est pas mémorisé.
        if (string.IsNullOrEmpty(user)
            || (string.IsNullOrEmpty(password) && !_settings.HasLoginToken))
        {
            SetStatus("Renseignez l'utilisateur et le mot de passe.", isError: true);
            ShowLogin();
            return;
        }

        // Fermer une éventuelle connexion précédente.
        if (_connection is not null)
        {
            _connection.StateChanged -= OnStateChanged;
            _connection.DevicesChanged -= OnDevicesChanged;
            _connection.Disconnect();
            _connection.Dispose();
            _connection = null;
        }

        // Repartir d'un inventaire vierge : les machines du serveur précédent
        // ne doivent pas subsister dans la liste.
        ClearDeviceInventory();

        var profile = new ServerProfile
        {
            Host = _settings.Host,
            Username = user,
            AllowSelfSigned = _settings.AllowSelfSigned,
        };

        _connection = new MeshServerConnection(profile);
        _connection.StateChanged += OnStateChanged;
        _connection.DevicesChanged += OnDevicesChanged;

        // Conservés en mémoire pour la reconnexion automatique (jamais écrits sur
        // disque si l'utilisateur n'a pas coché « mémoriser »).
        _sessionUser = user;
        _sessionPassword = password;
        _reconnectAttempt = 0;
        // Une connexion manuelle prend le pas sur une boucle de reconnexion en
        // cours : sans cela, les deux se disputeraient la connexion.
        _reconnecting = false;

        ConnectButton.IsEnabled = false;
        ShowLoading(_settings.Host);
        SetStatus("");

        // Retenu pour le diagnostic d'un refus : un cookie rejeté doit être
        // oublié, un code refusé ne remet pas la mémorisation en cause.
        var sentTrustedCookie = string.IsNullOrEmpty(token) && _settings.HasTwoFactorCookie;

        try
        {
            var login = BuildLogin(password, token, manual);
            try
            {
                await _connection.ConnectAsync(login);
            }
            catch (MeshException) when (login.LoginTokenUser is not null)
            {
                // Jeton révoqué ou expiré côté serveur : l'oublier et repartir
                // des identifiants du compte, quitte à redemander un code.
                Log.Warning("Jeton de connexion refusé par le serveur : retour aux "
                          + "identifiants du compte.", "Serveur");
                _settings.ClearLoginToken();
                if (string.IsNullOrEmpty(password)) throw;

                login = BuildLogin(password, token, manual);
                await _connection.ConnectAsync(login);
            }

            // Mémoriser (ou effacer) les identifiants selon la case cochée. Une
            // session ouverte par jeton n'apporte aucun mot de passe nouveau :
            // on ne touche pas à celui qui est enregistré.
            _settings.RememberCredentials = remember;
            _settings.Username = user;
            if (!remember)
            {
                // Ne rien laisser derrière soi : un jeton ou un cookie rouvrirait
                // une session alors que l'utilisateur a refusé toute mémorisation.
                _settings.ProtectedPassword = null;
                _settings.ClearDeviceTrust();
            }
            else if (!string.IsNullOrEmpty(password))
            {
                _settings.Password = password;
            }
            _settings.Save();

            // Le code vient d'être validé : c'est le seul moment où le serveur
            // accepte de délivrer un cookie d'appareil de confiance. Inutile de
            // le demander si l'administrateur a désactivé l'option.
            if (!string.IsNullOrEmpty(token)
                && remember
                && _trustedDeviceDays > 0
                && TrustDeviceCheck.IsChecked == true)
            {
                await RememberThisDeviceAsync();
            }

            SetHeader(_settings.Host, _connection.UserInfo?.Name ?? user, HeaderStatus.Connected);
            DeviceRow.CurrentUserName = _connection.UserInfo?.Name ?? user;
            ShowDevices();
        }
        catch (MeshTwoFactorRequiredException ex)
        {
            // Si un cookie mémorisé vient d'être présenté et que le serveur
            // redemande un code, c'est qu'il ne l'accepte plus : expiré, ou
            // révoqué par un changement de clé côté serveur.
            if (sentTrustedCookie) ForgetTwoFactorCookie();

            SetStatus(ex.Message, isError: !ex.CodeSent);
            ShowTwoFactorPrompt(ex);
        }
        catch (Exception ex)
        {
            SetStatus(_connection?.LastError ?? ex.Message, isError: true);
            PassBox.Clear();
            ShowLogin();
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Demande au serveur de mémoriser ce poste, pour que le code ne soit plus
    /// redemandé à chaque connexion.
    /// </summary>
    private async Task RememberThisDeviceAsync()
    {
        if (_connection is null) return;

        try
        {
            var cookie = await _connection.RequestTrustedDeviceCookieAsync();
            if (cookie is null)
            {
                Log.Warning("Le serveur n'autorise pas la mémorisation de ce poste.", "Serveur");
                SetStatus("Le serveur n'autorise pas à mémoriser ce poste.", isError: true);
                return;
            }

            _settings.TwoFactorCookie = cookie;
            _settings.TwoFactorCookieExpiry = _trustedDeviceDays > 0
                ? DateTime.Now.AddDays(_trustedDeviceDays)
                : null;
            _settings.SaveNow();
            Log.Info($"Ce poste est mémorisé pour {_trustedDeviceDays} jours.", "Serveur");
        }
        catch (Exception ex)
        {
            // Échec sans conséquence : la session est ouverte, seul le confort
            // de la prochaine connexion est perdu.
            Log.Warning($"Mémorisation de ce poste impossible : {ex.Message}", "Serveur");
        }
    }

    /// <summary>Oublie le cookie d'appareil de confiance, sans toucher au jeton.</summary>
    private void ForgetTwoFactorCookie()
    {
        _settings.ProtectedTwoFactorCookie = null;
        _settings.TwoFactorCookieExpiry = null;
        _settings.SaveNow();
    }

    // BeginInvoke : appelé depuis le fil de réception du serveur, qui ne doit pas
    // attendre l'interface pour continuer à lire les trames suivantes.
    private void OnStateChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_connection is null) return;
        if (_connection.State != ConnectionState.Failed) return;
        // Un essai raté pendant la reconnexion automatique : c'est la boucle de
        // reconnexion qui décide de la suite, pas ce gestionnaire, qui renvoyait
        // sinon à l'écran de connexion au premier essai infructueux.
        if (_reconnecting) return;

        // Coupure réseau alors qu'on était connecté : tenter de se rétablir seul
        // plutôt que de renvoyer l'utilisateur à l'écran de connexion. Un refus
        // d'authentification, lui, ne doit pas être retenté (risque de bannissement).
        if (CanAttemptReconnect())
        {
            _ = ReconnectLoopAsync();
            return;
        }

        SetStatus(_connection.LastError ?? "Connexion perdue.", isError: true);
        ShowLogin();
    });

    // MARK: - Reconnexion automatique

    private string? _sessionUser;
    private string? _sessionPassword;
    private int _reconnectAttempt;
    private bool _reconnecting;

    /// <summary>Délais entre tentatives : progressifs, pour ne pas marteler le serveur.</summary>
    private static readonly int[] RetryDelaysSeconds = [3, 5, 10, 20, 30, 60];

    private bool CanAttemptReconnect()
    {
        if (_reconnecting) return false;
        if (string.IsNullOrEmpty(_sessionUser)) return false;
        // Un jeton de connexion rouvre la session à lui seul.
        if (string.IsNullOrEmpty(_sessionPassword) && !_settings.HasLoginToken)
            return false;

        // Ne pas insister si le serveur a rejeté le compte : réessayer avec les
        // mêmes identifiants échouerait et pourrait déclencher un bannissement d'IP.
        var error = _connection?.LastError ?? "";
        if (error.Contains("mot de passe", StringComparison.OrdinalIgnoreCase)
            || error.Contains("deux facteurs", StringComparison.OrdinalIgnoreCase)
            || error.Contains("bannie", StringComparison.OrdinalIgnoreCase)
            || error.Contains("clients API", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// Retente la connexion avec un délai croissant, jusqu'à réussir ou jusqu'à
    /// ce que l'utilisateur intervienne. Reste discret : la fenêtre affiche l'état
    /// mais l'application demeure utilisable.
    /// </summary>
    private async Task ReconnectLoopAsync()
    {
        if (_reconnecting) return;
        _reconnecting = true;

        try
        {
            while (true)
            {
                var delay = RetryDelaysSeconds[Math.Min(_reconnectAttempt, RetryDelaysSeconds.Length - 1)];
                _reconnectAttempt++;

                ShowReconnecting(delay);
                Log.Info($"Reconnexion dans {delay} s (tentative {_reconnectAttempt})", "Serveur");
                await WaitBeforeRetryAsync(delay);

                // L'utilisateur s'est reconnecté manuellement entre-temps.
                if (_connection?.State == ConnectionState.Connected) return;

                try
                {
                    // On reprend la MÊME connexion quand elle vise toujours ce
                    // serveur et ce compte. Les sessions ouvertes (bureau à
                    // distance, fichiers, processus…) en gardent la référence :
                    // la remplacer les laissait liées à une connexion morte, et
                    // leur propre reconnexion échouait indéfiniment. Les fiches
                    // des machines sont conservées (MergeNodeInventory).
                    var connection = _connection;
                    if (connection is null
                        || !string.Equals(connection.Profile.Host, _settings.Host, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(connection.Profile.Username, _sessionUser, StringComparison.OrdinalIgnoreCase))
                    {
                        var profile = new ServerProfile
                        {
                            Host = _settings.Host,
                            Username = _sessionUser!,
                            AllowSelfSigned = _settings.AllowSelfSigned,
                        };
                        connection = new MeshServerConnection(profile);
                        connection.StateChanged += OnStateChanged;
                        connection.DevicesChanged += OnDevicesChanged;
                    }

                    // Le second facteur mémorisé est représenté comme à la
                    // connexion initiale : sans lui, une 2FA obligatoire
                    // rendrait toute reconnexion automatique impossible.
                    await connection.ConnectAsync(BuildLogin(_sessionPassword ?? "", null));

                    // Nouvelle instance (serveur ou compte changés) : elle
                    // remplace l'ancienne une fois établie.
                    if (!ReferenceEquals(_connection, connection))
                    {
                        if (_connection is not null)
                        {
                            _connection.StateChanged -= OnStateChanged;
                            _connection.DevicesChanged -= OnDevicesChanged;
                            _connection.Dispose();
                        }
                        _connection = connection;
                    }
                    _reconnectAttempt = 0;

                    Log.Info("Reconnexion réussie", "Serveur");
                    SetHeader(_settings.Host, connection.UserInfo?.Name ?? _sessionUser, HeaderStatus.Connected);
                    DeviceRow.CurrentUserName = connection.UserInfo?.Name ?? _sessionUser;
                    ShowDevices();
                    SetStatus("");
                    UpdateTrayTooltip();
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Tentative de reconnexion échouée : {ex.Message}", "Serveur");

                    // L'autorisation de ce poste n'est plus valable : rendre la
                    // main à l'utilisateur pour qu'il saisisse un code.
                    if (ex is MeshTwoFactorRequiredException twoFactor)
                    {
                        ForgetTwoFactorCookie();
                        SetStatus(twoFactor.Message, isError: true);
                        ShowTwoFactorPrompt(twoFactor);
                        return;
                    }

                    // Identifiants refusés : inutile d'insister.
                    if (ex is MeshException && !ex.Message.Contains("joindre", StringComparison.OrdinalIgnoreCase))
                    {
                        SetStatus(ex.Message, isError: true);
                        ShowLogin();
                        return;
                    }
                }
            }
        }
        finally
        {
            _reconnecting = false;
        }
    }

    /// <summary>
    /// Signal qui écourte l'attente avant la prochaine tentative, quand une
    /// reprise réseau ou une sortie de veille rend le serveur de nouveau
    /// joignable.
    /// </summary>
    private TaskCompletionSource? _reconnectWakeup;

    /// <summary>
    /// Attend le délai prévu, ou moins si une reprise est signalée entre-temps :
    /// après un réveil du poste, attendre soixante secondes de plus alors que le
    /// réseau est revenu ne se justifie pas.
    /// </summary>
    private async Task WaitBeforeRetryAsync(int seconds)
    {
        var wakeup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _reconnectWakeup = wakeup;
        try
        {
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(seconds)), wakeup.Task);
        }
        finally
        {
            if (ReferenceEquals(_reconnectWakeup, wakeup)) _reconnectWakeup = null;
        }
    }

    /// <summary>
    /// Interrompt la reconnexion automatique. Appelé à la déconnexion : sans
    /// cela, la boucle rétablirait la session que l'on vient de fermer.
    /// </summary>
    private void StopReconnecting()
    {
        _reconnecting = false;
        _sessionUser = null;
        _sessionPassword = null;
        _reconnectAttempt = 0;
    }

    /// <summary>Vide l'inventaire des machines (changement de serveur, déconnexion).</summary>
    private void ClearDeviceInventory()
    {
        _rowsById.Clear();
        _virtualRows.Clear();
        _allRows = new List<DeviceRow>();
        _chipScope = new List<DeviceRow>();
        _lastPlan = new();
        _lastDisplay = new();
        _groupHeaders.Clear();
        DeviceList.ItemsSource = null;
        CountText.Text = "";
        EmptyState.Visibility = Visibility.Collapsed;
        UpdateSelectionBar();
    }

    /// <summary>Informe discrètement que la connexion est en cours de rétablissement.</summary>
    private void ShowReconnecting(int seconds)
    {
        SetHeader(_settings.Host, $"reconnexion dans {seconds} s…", HeaderStatus.Warning);
        _tray?.SetTooltip("Mesh Control — reconnexion en cours");
    }

    /// <summary>
    /// Appelé depuis les threads de fond à chaque événement serveur. On ne fait
    /// que (re)démarrer le timer : BeginInvoke est non bloquant, contrairement à
    /// Invoke qui figeait l'UI sous une rafale d'événements.
    /// </summary>
    /// <summary>
    /// Programme une mise à jour de la liste au plus tard 800 ms après le premier
    /// événement d'une rafale. Relancer le délai à chaque événement (Stop puis
    /// Start) la repoussait indéfiniment sous un flux continu — l'allumage du
    /// parc le matin — et la liste ne bougeait plus du tout.
    /// </summary>
    private void OnDevicesChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
    });

    /// <summary>
    /// Lignes existantes, indexées par identifiant de machine. Permet de mettre à
    /// jour la liste en place plutôt que de la reconstruire : sans cela, chaque
    /// événement serveur détruirait tous les éléments visuels, réinitialisant le
    /// défilement, l'état des dossiers et la sélection.
    /// </summary>
    private readonly Dictionary<string, DeviceRow> _rowsById = new();

    /// <summary>
    /// Met à jour la liste depuis l'état du serveur. Ne reconstruit la vue que si
    /// la composition a réellement changé (machine ajoutée, retirée, ou changement
    /// d'état modifiant l'ordre d'affichage).
    /// </summary>
    private void RebuildDeviceList()
    {
        if (_connection is null) return;

        var groupNames = _connection.Meshes
            .GroupBy(m => m.Id)
            .ToDictionary(g => g.Key, g => g.First().Name);

        bool structureChanged = false;
        var seen = new HashSet<string>();

        foreach (var node in _connection.Nodes.Values)
        {
            seen.Add(node.Id);
            var groupName = groupNames.TryGetValue(node.MeshId, out var g) ? g : "Sans dossier";

            // ReferenceEquals : une reconnexion au serveur reconstruit
            // l'inventaire, et la ligne existante pointerait alors vers une fiche
            // orpheline que plus aucun événement ne met à jour. On la recrée.
            if (_rowsById.TryGetValue(node.Id, out var existing)
                && ReferenceEquals(existing.Node, node))
            {
                // Le tri dépend de l'état en ligne, du nom et du dossier : un
                // changement sur ces valeurs impose de recomposer la vue.
                // La comparaison porte sur l'état AFFICHÉ, car existing.Node est
                // la même instance que node — déjà mise à jour par la connexion.
                // Par défaut, l'ordre d'affichage ne dépend PAS de l'état en
                // ligne (voir SortRows) : un passage en ligne ne recompose donc
                // pas la vue, seul le badge se met à jour. C'est ce qui évite que
                // la liste bouge sous le curseur à chaque événement du serveur —
                // sauf si l'utilisateur a choisi un tri qui en dépend.
                if (existing.NameChanged
                    || !string.Equals(existing.RealGroupName, groupName, StringComparison.Ordinal))
                {
                    structureChanged = true;
                }
                // Les filtres « En ligne », « Hors ligne » et « En session »
                // reposent sur la connectivité : la vue doit être recomposée,
                // sinon une machine qui se connecte n'apparaîtrait jamais dans
                // la liste filtrée.
                if (existing.OnlineStateChanged
                    && _activeFilters.ContainsKey(FilterGroup.State))
                    structureChanged = true;
                if (existing.SessionsChanged && IsFilterActive(DeviceFilter.InSession))
                    structureChanged = true;
                // Même chose quand le tri choisi repose sur l'état en ligne :
                // l'utilisateur a demandé que la liste suive les connexions.
                if (existing.OnlineStateChanged && SortFollowsConnectivity)
                    structureChanged = true;
                // Rafraîchit badge, libellé et icône sans toucher à la vue.
                existing.RefreshFromNode();
            }
            else
            {
                _rowsById[node.Id] = new DeviceRow(node, groupName, _settings.IsFavorite(node.Id))
                {
                    IsWatched = _watchOnline.Contains(node.Id),
                };
                structureChanged = true;
            }
        }

        // Machines disparues de l'inventaire du serveur.
        foreach (var goneId in _rowsById.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _rowsById.Remove(goneId);
            structureChanged = true;
        }

        // Les copies affichées dans les sections Favoris/Récentes partagent le
        // même nœud, mais ont leur propre état affiché : elles doivent être
        // rafraîchies séparément.
        foreach (var copy in _virtualRows) copy.RefreshFromNode();

        CheckWatchedNodes();

        if (!structureChanged)
        {
            // Seules des valeurs d'affichage ont changé : les liaisons de données
            // s'en chargent, la vue reste intacte.
            UpdateCounters();
            return;
        }

        // Ordre de base, par dossier puis par nom. L'ordre affiché est celui que
        // l'utilisateur a choisi, appliqué au filtrage (SortRows) ; le tri par
        // nom, par défaut, reste indépendant de l'état en ligne pour que la
        // liste ne saute pas sous le curseur à chaque connexion.
        _allRows = _rowsById.Values
            .OrderBy(r => r.RealGroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Le volet suit la composition du parc, pas chaque événement serveur.
        RebuildFolderPanel();
        ApplyFilter();
    }

    /// <summary>Met à jour les compteurs sans toucher à la liste.</summary>
    private void UpdateCounters()
    {
        var displayed = DeviceList.ItemsSource is null ? _allRows.Count : _shownRowCount;
        var totalOnline = _allRows.Count(r => r.IsOnline);

        CountText.Text = displayed >= _allRows.Count
            ? $"{_allRows.Count} appareils · {totalOnline} en ligne"
            : $"{displayed} affichés sur {_allRows.Count} · {totalOnline} en ligne";
        UpdateFilterChips();
        RefreshFolderCounts();
        UpdateSelectionBar();
        UpdateTrayTooltip();
    }

    // MARK: - En-tête

    private enum HeaderStatus { Unchanged, Idle, Connected, Warning }

    /// <summary>
    /// En-tête de la fenêtre : le serveur en gras, puis le compte ou l'état en
    /// retrait, et le point d'état de la liaison.
    /// </summary>
    private void SetHeader(string? host, string? detail, HeaderStatus status = HeaderStatus.Unchanged)
    {
        HeaderHostRun.Text = string.IsNullOrEmpty(host) ? "Contrôle à distance" : host;
        HeaderDetailRun.Text = string.IsNullOrEmpty(detail) ? "" : $"  ·  {detail}";

        if (status == HeaderStatus.Unchanged) return;
        ConnectionDot.Visibility = status == HeaderStatus.Idle ? Visibility.Collapsed : Visibility.Visible;
        ConnectionDot.Fill = (System.Windows.Media.Brush)FindResource(
            status == HeaderStatus.Connected ? "OnlineBrush" : "WarningTextBrush");
        ConnectionDot.ToolTip = status == HeaderStatus.Connected ? "Connecté au serveur" : detail;
    }

    // MARK: - Recherche

    /// <summary>Un clic n'importe où dans le cadre place le curseur dans le champ.</summary>
    private void SearchFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SearchBox.IsKeyboardFocusWithin) return;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    /// <summary>
    /// Filtrage différé pendant la frappe.
    ///
    /// Filtrer à chaque caractère refiltrait l'ensemble des machines, recomposait
    /// les sections virtuelles et reconstruisait la vue groupée — donc WPF
    /// régénérait tous ses conteneurs. Sur un parc important, la saisie devenait
    /// saccadée. Le délai est court : il regroupe les frappes d'une même rafale
    /// sans que l'attente soit perceptible entre deux mots.
    /// </summary>
    private readonly DispatcherTimer _searchTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(120)
    };

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Le repère visuel du champ reste immédiat : seul le filtrage attend.
        var empty = string.IsNullOrEmpty(SearchBox.Text);
        SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        SearchShortcutHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();

        // Effacement explicite : on rétablit la liste complète sans attendre.
        _searchTimer.Stop();
        ApplyFilter();
    }

    // MARK: - Favoris et machines récentes

    private const string FavoritesGroup = MainWindowKeys.Favorites;
    private const string RecentGroup = "🕘  Récentes";

    /// <summary>
    /// Copies affichées dans les sections virtuelles. Conservées pour pouvoir les
    /// rafraîchir : une même instance ne peut pas figurer dans deux groupes WPF,
    /// il faut donc un doublon, mais il doit suivre l'état de l'original.
    /// </summary>
    private readonly List<DeviceRow> _virtualRows = new();

    /// <summary>Copie une ligne dans une section virtuelle (favoris / récentes).</summary>
    private DeviceRow Clone(DeviceRow row, string groupName)
    {
        var copy = new DeviceRow(row.Node, row.RealGroupName, row.IsFavorite)
        {
            GroupName = groupName,
            IsWatched = row.IsWatched,
        };
        _virtualRows.Add(copy);
        return copy;
    }

    /// <summary>Épingle ou retire une machine des favoris.</summary>
    private void ToggleFavorite(MeshNode node)
    {
        _settings.ToggleFavorite(node.Id);
        _settings.Save();

        // Mettre à jour les lignes existantes sans reconstruire toute la liste.
        var isFavorite = _settings.IsFavorite(node.Id);
        if (_rowsById.TryGetValue(node.Id, out var row)) row.IsFavorite = isFavorite;

        // La composition change (section Favoris) : la vue doit être recomposée.
        ApplyFilter();
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is DeviceRow row) ToggleFavorite(row.Node);
        // Empêche le clic d'atteindre la ligne (qui ouvrirait la machine).
        e.Handled = true;
    }

    private void MenuFavorite_Click(object sender, RoutedEventArgs e)
        => RunSelected(DeviceAction.ToggleFavorite);

    /// <summary>Mémorise une machine comme récemment utilisée.</summary>
    private void RememberRecent(MeshNode node)
    {
        _settings.RememberRecent(node.Id);
        _recentSet = null;
        _settings.Save();
        ApplyFilter();
    }

    // MARK: - Repli des dossiers

    /// <summary>
    /// Dossiers repliés, par nom. Un dossier replié garde son en-tête dans la
    /// liste plate, mais ses machines n'y sont pas placées.
    /// </summary>
    private HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>En-têtes de dossier, conservés d'un filtrage à l'autre.</summary>
    private readonly Dictionary<string, GroupHeaderRow> _groupHeaders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dossiers dont l'en-tête figure dans la liste affichée.</summary>
    private List<string> _shownGroupNames = new();

    /// <summary>Enregistre les dossiers repliés pour les retrouver au prochain lancement.</summary>
    private void SaveCollapsedGroups()
    {
        _settings.CollapsedGroups = _collapsedGroups.ToList();
        _settings.Save();
    }

    /// <summary>Vrai quand le bouton global doit tout déplier plutôt que tout replier.</summary>
    private bool _allGroupsCollapsed;

    private GroupHeaderRow HeaderFor(string name, int count)
    {
        if (!_groupHeaders.TryGetValue(name, out var header))
            _groupHeaders[name] = header = new GroupHeaderRow(name);
        header.Count = count;
        header.IsCollapsed = _collapsedGroups.Contains(name);
        return header;
    }

    /// <summary>
    /// Clic sur un en-tête de dossier : le replie ou le déplie. Intercepté avant
    /// la liste, pour que l'en-tête ne devienne pas la sélection.
    /// </summary>
    private void DeviceList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(DeviceList, source) is not ListBoxItem { DataContext: GroupHeaderRow header })
            return;
        e.Handled = true;

        if (!_collapsedGroups.Remove(header.Name)) _collapsedGroups.Add(header.Name);
        SaveCollapsedGroups();
        ApplyFilter();
    }

    /// <summary>Pas de menu des machines sur un en-tête de dossier.</summary>
    private void DeviceList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(DeviceList, source) is ListBoxItem { DataContext: GroupHeaderRow })
            e.Handled = true;
    }

    private void ToggleGroups_Click(object sender, RoutedEventArgs e)
    {
        _allGroupsCollapsed = !_allGroupsCollapsed;

        // N'agir que sur les dossiers réellement affichés : un filtre de
        // recherche peut en masquer, et un Clear() global oublierait leur état.
        foreach (var name in _shownGroupNames)
        {
            if (_allGroupsCollapsed) _collapsedGroups.Add(name);
            else _collapsedGroups.Remove(name);
        }

        SaveCollapsedGroups();
        ApplyFilter();
    }

    private void UpdateToggleGroupsButton()
    {
        // Segoe MDL2 Assets : E70D chevron bas, E70E chevron haut.
        ToggleGroupsGlyph.Text = _allGroupsCollapsed ? "\uE70D" : "\uE70E";
        ToggleGroupsButton.ToolTip = _allGroupsCollapsed
            ? "Déplier tous les dossiers"
            : "Replier tous les dossiers";
    }

    // MARK: - Filtres rapides

    private enum DeviceFilter { All, Online, Offline, InSession, Favorites, Recent, Windows, Linux, MacOS }

    /// <summary>
    /// Catégories de pastilles. Une pastille au plus par catégorie, et les
    /// catégories se cumulent : « Windows » et « En ligne » donnent les machines
    /// Windows en ligne.
    /// </summary>
    private enum FilterGroup { None, State, Set, System }

    /// <summary>Pastille active de chaque catégorie. Vide : aucun filtre (« Tous »).</summary>
    private readonly Dictionary<FilterGroup, DeviceFilter> _activeFilters = new();

    private bool AnyFilterActive => _activeFilters.Count > 0;

    /// <summary>
    /// Machines sur lesquelles portent les compteurs des pastilles : celles du
    /// dossier choisi et de la recherche en cours, avant le filtre des pastilles.
    /// </summary>
    private List<DeviceRow> _chipScope = new();

    /// <summary>Pastilles affichées, avec le texte de leur compteur.</summary>
    private readonly Dictionary<DeviceFilter, (ToggleButton Chip, TextBlock Count)> _filterChips = new();

    private static readonly (DeviceFilter Filter, FilterGroup Group, string Label, string Tooltip)[] FilterDefinitions =
    [
        (DeviceFilter.All, FilterGroup.None, "Tous", "Retirer tous les filtres"),
        (DeviceFilter.Online, FilterGroup.State, "En ligne", "Machines connectées au serveur"),
        (DeviceFilter.Offline, FilterGroup.State, "Hors ligne", "Machines éteintes ou injoignables"),
        (DeviceFilter.InSession, FilterGroup.State, "En session", "Machines où un utilisateur MeshCentral est connecté"),
        (DeviceFilter.Favorites, FilterGroup.Set, "Favoris", "Machines épinglées"),
        (DeviceFilter.Recent, FilterGroup.Set, "Récents", "Dernières machines ouvertes, de la plus récente à la plus ancienne"),
        (DeviceFilter.Windows, FilterGroup.System, "Windows", "Machines sous Windows"),
        (DeviceFilter.Linux, FilterGroup.System, "Linux", "Machines sous Linux"),
        (DeviceFilter.MacOS, FilterGroup.System, "macOS", "Machines sous macOS"),
    ];

    private static FilterGroup GroupOf(DeviceFilter filter)
        => FilterDefinitions.First(f => f.Filter == filter).Group;

    private bool IsFilterActive(DeviceFilter filter) => filter == DeviceFilter.All
        ? !AnyFilterActive
        : _activeFilters.TryGetValue(GroupOf(filter), out var active) && active == filter;

    /// <summary>Crée les pastilles. Leurs compteurs sont tenus par UpdateFilterChips.</summary>
    private void BuildFilterChips()
    {
        var previousGroup = FilterGroup.None;
        foreach (var (filter, group, label, tooltip) in FilterDefinitions)
        {
            var count = new TextBlock { Opacity = 0.7, Margin = new Thickness(6, 0, 0, 0) };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = label });
            content.Children.Add(count);

            var chip = new ToggleButton
            {
                Content = content,
                ToolTip = tooltip,
                Style = (Style)FindResource("FilterChip"),
            };
            // Un écart plus large entre deux catégories : on voit ce qui se
            // cumule (d'une catégorie à l'autre) et ce qui s'exclut (au sein
            // d'une même catégorie).
            if (group != previousGroup && previousGroup != FilterGroup.None)
                chip.Margin = new Thickness(10, 0, 6, 0);
            previousGroup = group;

            chip.Click += (_, _) =>
            {
                Motion.Pop(chip);
                _animateList = true;
                ToggleFilter(filter);
            };
            FilterChipsPanel.Children.Add(chip);
            _filterChips[filter] = (chip, count);
        }
        SyncFilterChips();
        UpdateFilterChips();
    }

    /// <summary>
    /// Clic sur une pastille : « Tous » retire tout ; une autre remplace celle
    /// de sa catégorie, ou se retire si elle était déjà active.
    /// </summary>
    private void ToggleFilter(DeviceFilter filter)
    {
        if (filter == DeviceFilter.All) _activeFilters.Clear();
        else if (IsFilterActive(filter)) _activeFilters.Remove(GroupOf(filter));
        else _activeFilters[GroupOf(filter)] = filter;

        SyncFilterChips();
        ApplyFilter();
    }

    /// <summary>État coché des pastilles, d'après les filtres actifs.</summary>
    private void SyncFilterChips()
    {
        foreach (var (filter, (chip, _)) in _filterChips)
            chip.IsChecked = IsFilterActive(filter);
    }

    /// <summary>
    /// Message de liste vide : la recherche, un filtre ou un dossier vide en
    /// est la cause, et le bouton rétablit tout d'un clic.
    /// </summary>
    private void UpdateEmptyState(int displayed, string query, bool folderFiltered)
    {
        if (displayed > 0 || _allRows.Count == 0)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        var labels = FilterDefinitions
            .Where(f => f.Filter != DeviceFilter.All && IsFilterActive(f.Filter))
            .Select(f => $"« {f.Label} »")
            .ToList();
        var causes = new List<string>();
        if (!string.IsNullOrEmpty(query)) causes.Add($"la recherche « {query} »");
        if (labels.Count == 1) causes.Add($"le filtre {labels[0]}");
        else if (labels.Count > 1) causes.Add($"les filtres {string.Join(" + ", labels)}");

        if (causes.Count == 0 && folderFiltered)
        {
            EmptyStateTitle.Text = "Ce dossier est vide";
            EmptyStateDetail.Text = "Aucune machine n'y est rangée pour le moment.";
            EmptyStateClearButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyStateTitle.Text = "Aucune machine ne correspond";
            var where = folderFiltered ? " dans ce dossier" : "";
            EmptyStateDetail.Text = $"Rien ne répond à {string.Join(" et ", causes)}{where}.";
            EmptyStateClearButton.Visibility = Visibility.Visible;
        }
        if (EmptyState.Visibility != Visibility.Visible)
        {
            EmptyState.Visibility = Visibility.Visible;
            Motion.FadeIn(EmptyState, 0, 220, slideY: 8);
        }
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        _searchTimer.Stop();
        _activeFilters.Clear();
        SyncFilterChips();
        ApplyFilter();
        SearchBox.Focus();
    }

    private bool MatchesFilter(DeviceRow row, DeviceFilter filter) => filter switch
    {
        DeviceFilter.Online => row.IsOnline,
        DeviceFilter.Offline => !row.IsOnline,
        DeviceFilter.Favorites => row.IsFavorite,
        DeviceFilter.Recent => IsRecent(row.Node.Id),
        DeviceFilter.InSession => row.IsOnline && row.Node.Sessions.Count > 0,
        DeviceFilter.Windows => row.Node.OsFamily == OSFamily.Windows,
        DeviceFilter.Linux => row.Node.OsFamily == OSFamily.Linux,
        DeviceFilter.MacOS => row.Node.OsFamily == OSFamily.MacOS,
        _ => true,
    };

    /// <summary>
    /// La machine passe-t-elle tous les filtres actifs ? <paramref name="except"/>
    /// ignore une catégorie : c'est ce que donnerait une autre pastille de
    /// cette catégorie à la place de l'actuelle.
    /// </summary>
    private bool MatchesActiveFilters(DeviceRow row, FilterGroup except = FilterGroup.None)
    {
        foreach (var (group, filter) in _activeFilters)
        {
            if (group != except && !MatchesFilter(row, filter)) return false;
        }
        return true;
    }

    /// <summary>
    /// Compteurs des pastilles : chacun dit combien de machines resteraient en
    /// l'activant, compte tenu des pastilles des autres catégories, du dossier
    /// et de la recherche. Ainsi, avec « Windows » actif, « En ligne » compte
    /// les machines Windows en ligne. Une pastille sans aucune machine dans le
    /// périmètre disparaît (pas de « Linux 0 » sur un parc tout Windows) ; une
    /// pastille que les autres filtres réduisent à zéro reste, en retrait.
    /// </summary>
    private void UpdateFilterChips()
    {
        foreach (var (filter, (chip, count)) in _filterChips)
        {
            var active = IsFilterActive(filter);
            int n;
            bool exists;
            if (filter == DeviceFilter.All)
            {
                n = _chipScope.Count;
                exists = true;
            }
            else
            {
                var group = GroupOf(filter);
                n = _chipScope.Count(r => MatchesFilter(r, filter) && MatchesActiveFilters(r, group));
                exists = n > 0 || filter == DeviceFilter.Online || _chipScope.Any(r => MatchesFilter(r, filter));
            }
            count.Text = n.ToString();
            if (_settings.FolderView && filter != DeviceFilter.All && GroupOf(filter) == FilterGroup.Set)
                exists = active = false;
            chip.Visibility = exists || active ? Visibility.Visible : Visibility.Collapsed;
            chip.Opacity = n == 0 && !active && filter != DeviceFilter.All ? 0.45 : 1.0;
        }
        FilterChipsPanel.Visibility = _allRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: - Tri

    private static readonly string[] SortLabels = ["Nom", "En ligne d'abord", "Dernière présence", "Système"];

    /// <summary>Le tri choisi dépend-il de l'état en ligne des machines ?</summary>
    private bool SortFollowsConnectivity => _settings.DeviceSort is 1 or 2;

    /// <summary>
    /// Ordonne les machines selon le tri choisi, le nom départageant toujours.
    /// Les dossiers restent groupés : le tri s'applique à l'intérieur de chacun.
    /// </summary>
    private IEnumerable<DeviceRow> SortRows(IEnumerable<DeviceRow> rows, bool byGroup)
    {
        var ordered = byGroup
            ? rows.OrderBy(r => r.RealGroupName, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(_ => 0);

        ordered = _settings.DeviceSort switch
        {
            1 => ordered.ThenByDescending(r => r.IsOnline),
            // Les machines en ligne d'abord, « vues à l'instant » ; puis les
            // autres, de la plus récemment vue à la plus ancienne.
            2 => ordered.ThenByDescending(r => r.IsOnline)
                        .ThenByDescending(r => r.Node.LastConnect ?? DateTime.MinValue),
            // Par famille, puis par version : Windows 10 et 11 se suivent.
            3 => ordered.ThenBy(r => r.Node.OsFamily)
                        .ThenBy(r => r.Node.OsDescription is null)
                        .ThenBy(r => r.Node.OsDescription ?? "", StringComparer.OrdinalIgnoreCase),
            _ => ordered,
        };
        return ordered.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        SortMenu.PlacementTarget = SortButton;
        SortMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        UpdateSortButton();
        SortMenu.IsOpen = true;
    }

    private void SortChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out var mode)) return;

        _settings.DeviceSort = mode;
        _settings.Save();
        UpdateSortButton();
        _animateList = true;
        ApplyFilter();
    }

    /// <summary>Libellé du bouton et coche du menu, d'après le tri retenu.</summary>
    private void UpdateSortButton()
    {
        var mode = Math.Clamp(_settings.DeviceSort, 0, SortLabels.Length - 1);
        SortLabel.Text = SortLabels[mode];
        SortArrowName.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed;
        SortArrowSystem.Visibility = mode == 3 ? Visibility.Visible : Visibility.Collapsed;
        SortArrowSeen.Visibility = mode is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in SortMenu.Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is string tag && tag == mode.ToString();
    }

    private void ApplyFilter()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var query = SearchBox.Text?.Trim() ?? "";
        var selectedIds = DeviceList.SelectedItems.OfType<DeviceRow>()
            .Select(r => r.Node.Id).ToHashSet();

        // Vue à deux volets : dossier choisi. Une recherche en cours reprend la
        // main sur le dossier — sinon le champ paraîtrait ne rien trouver dès
        // que la machine cherchée se range ailleurs.
        var folderKey = _settings.FolderView && string.IsNullOrEmpty(query)
                        && _selectedFolderKey is { } key && key != FolderRow.AllKey
            ? key : null;
        bool folderFiltered = folderKey is not null;

        IEnumerable<DeviceRow> source = _allRows;
        if (!string.IsNullOrEmpty(query))
            // Sans accents ni casse (TextMatch) : « neg » trouve « Négoce ».
            source = source.Where(r =>
                TextMatch.Contains(r.Name, query)
                || TextMatch.Contains(r.GroupName, query)
                || TextMatch.Contains(r.Node.OsDescription, query)
                // Retrouver un poste par son adresse, ou par le collègue connecté dessus.
                || TextMatch.Contains(r.Node.Ip, query)
                || (r.IsOnline && r.Node.Sessions.Any(s => TextMatch.Contains(s.UserName, query))));
        else if (folderKey != FolderRow.HiddenKey)
            // Dossiers masqués : hors de la liste, sauf pendant une recherche
            // ou quand on ouvre l'entrée « dossiers masqués » du volet.
            source = source.Where(r => !IsHiddenFolder(r.RealGroupName));

        if (folderKey is not null)
            source = source.Where(r => folderKey switch
            {
                FolderRow.HiddenKey => IsHiddenFolder(r.RealGroupName),
                FavoritesGroup => r.IsFavorite,
                FolderRow.RecentKey => IsRecent(r.Node.Id),
                FolderRow.SessionKey => r.IsOnline && r.Node.Sessions.Count > 0,
                _ => string.Equals(r.RealGroupName, folderKey, StringComparison.OrdinalIgnoreCase),
            });

        // Les pastilles comptent sur ce périmètre (recherche et dossier), avant
        // leur propre filtre : elles disent ce que chacune donnerait ici.
        _chipScope = source.ToList();
        if (AnyFilterActive) source = _chipScope.Where(r => MatchesActiveFilters(r));
        var set = _activeFilters.GetValueOrDefault(FilterGroup.Set, DeviceFilter.All);

        var rows = SortRows(source, byGroup: true).ToList();

        // Favoris et Récents forment une liste plate : on a demandé ces
        // machines-là, quel que soit leur dossier. Les récents gardent l'ordre
        // d'utilisation, le plus récent en tête. Les dossiers masqués, eux,
        // restent groupés : ils sont plusieurs.
        bool flat = (folderFiltered && folderKey != FolderRow.HiddenKey)
                    || set is DeviceFilter.Favorites or DeviceFilter.Recent;
        if (set == DeviceFilter.Recent || folderKey == FolderRow.RecentKey)
        {
            var recent = _settings.RecentNodeIds
                .Select((id, index) => (id, index))
                .ToDictionary(x => x.id, x => x.index);
            rows = rows.OrderBy(r => recent.GetValueOrDefault(r.Node.Id, int.MaxValue)).ToList();
        }
        else if (set == DeviceFilter.Favorites)
        {
            rows = SortRows(rows, byGroup: false).ToList();
        }

        // Entrées : chaque machine et la section où elle paraît. Sections
        // virtuelles en tête, favoris puis machines récentes ; une même machine
        // peut apparaître deux fois (en favori et dans son dossier), c'est voulu,
        // comme un raccourci vers l'entrée réelle.
        var entries = new List<(DeviceRow Source, string Group)>(rows.Count + 16);

        // Dans un dossier précis ou sous un filtre, ni favoris ni récentes : on
        // a demandé ces machines-là, pas un florilège. Avec la barre latérale
        // non plus : favoris et récentes y sont des vues à part entière.
        if (string.IsNullOrEmpty(query) && !folderFiltered && !AnyFilterActive && !_settings.FolderView)
        {
            // Même ordre que les dossiers, mais les favoris de tous les
            // dossiers mêlés en une seule section.
            foreach (var favorite in SortRows(rows.Where(r => r.IsFavorite), byGroup: false))
                entries.Add((favorite, FavoritesGroup));

            // Récentes : dans l'ordre d'utilisation, favoris exclus (déjà en haut).
            var byId = rows.ToDictionary(r => r.Node.Id, r => r);
            foreach (var id in _settings.RecentNodeIds)
            {
                if (byId.TryGetValue(id, out var row) && !row.IsFavorite)
                    entries.Add((row, RecentGroup));
            }
        }

        foreach (var row in rows) entries.Add((row, row.GroupName));

        // Plan : les entrées précédées de l'en-tête de leur dossier, sauf en
        // liste plate ; un dossier replié garde son en-tête, sans ses machines.
        // Les entrées d'un même dossier se suivent : les machines sont triées
        // par dossier (SortRows) et les sections virtuelles sont en tête.
        var plan = new List<(DeviceRow? Source, string Group)>(entries.Count + 64);
        var groupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (flat)
        {
            foreach (var entry in entries) plan.Add(entry);
        }
        else
        {
            foreach (var (_, group) in entries)
                groupCounts[group] = groupCounts.GetValueOrDefault(group) + 1;

            // « Tout replier » actif : un dossier apparu depuis (nouvelle
            // machine, section Favoris qui vient de se créer) doit l'être aussi,
            // sinon il s'ouvrirait seul au milieu d'une liste repliée. Seuls les
            // dossiers NOUVEAUX sont concernés : replier aussi ceux déjà affichés
            // annulait aussitôt le dépli d'un dossier, qui restait alors replié
            // pour de bon (cas d'un dossier seul à l'écran, ou du dernier
            // dossier déplié qu'on replie).
            if (_allGroupsCollapsed)
            {
                var alreadyShown = new HashSet<string>(_shownGroupNames, StringComparer.OrdinalIgnoreCase);
                foreach (var name in groupCounts.Keys.Where(n => !alreadyShown.Contains(n)))
                    _collapsedGroups.Add(name);
            }

            string? current = null;
            foreach (var entry in entries)
            {
                if (!string.Equals(entry.Group, current, StringComparison.Ordinal))
                {
                    current = entry.Group;
                    plan.Add((null, current));
                }
                if (!_collapsedGroups.Contains(entry.Group)) plan.Add(entry);
            }
        }

        // Même résultat que l'affichage en place (recherche affinée sans effet,
        // événement serveur sans conséquence visible…) : on garde la liste telle
        // quelle, défilement et sélection compris.
        var unchanged = DeviceList.ItemsSource is not null && plan.Count == _lastPlan.Count;
        for (var i = 0; unchanged && i < plan.Count; i++)
        {
            unchanged = ReferenceEquals(plan[i].Source, _lastPlan[i].Source)
                        && plan[i].Group == _lastPlan[i].Group;
        }

        // Compteurs des en-têtes, à jour même quand la liste est gardée.
        foreach (var (name, count) in groupCounts) HeaderFor(name, count);

        List<object> display;
        if (unchanged)
        {
            display = _lastDisplay;
        }
        else
        {
            _virtualRows.Clear();
            display = new List<object>(plan.Count);
            foreach (var (row, group) in plan)
            {
                display.Add(row is null ? HeaderFor(group, groupCounts[group])
                          : group == row.GroupName ? row
                          : Clone(row, group));
            }
            DeviceList.ItemsSource = display;
            if (_animateList && DeviceList.IsVisible) Motion.FadeIn(DeviceList, 0.3, 170, slideY: 6);

            _lastPlan = plan;
            _lastDisplay = display;
        }
        _shownGroupNames = groupCounts.Keys.ToList();
        _shownRowCount = entries.Count;
        Highlight.SetQuery(DeviceList, query);
        _animateList = false;
        UpdateEmptyState(entries.Count, query, folderFiltered);
        UpdateViewTitle(query);

        var totalOnline = _allRows.Count(r => r.IsOnline);
        CountText.Text = rows.Count == _allRows.Count
            ? $"{_allRows.Count} appareils · {totalOnline} en ligne"
            : $"{rows.Count} affichés sur {_allRows.Count} · {totalOnline} en ligne";
        UpdateFilterChips();
        RefreshFolderCounts();
        // Le bouton de repli n'a de sens qu'avec plusieurs dossiers affichés.
        ToggleGroupsButton.Visibility = !flat
            && rows.Select(r => r.GroupName).Distinct().Skip(1).Any()
            ? Visibility.Visible : Visibility.Collapsed;

        // Déduire le sens du bouton de l'état réel plutôt que de le mémoriser :
        // au lancement, les dossiers repliés sont restaurés depuis les réglages
        // et le bouton aurait sinon proposé « replier » alors que tout l'est.
        _allGroupsCollapsed = _shownGroupNames.Count > 0 && _shownGroupNames.All(_collapsedGroups.Contains);
        UpdateToggleGroupsButton();
        UpdateTrayTooltip();

        // Restaure la sélection, multiple comprise, sur les lignes réelles. Vue
        // gardée telle quelle : la sélection n'a pas bougé.
        if (selectedIds.Count > 0 && !unchanged)
        {
            foreach (var row in rows.Where(r => selectedIds.Contains(r.Node.Id)))
                DeviceList.SelectedItems.Add(row);
        }
        UpdateSelectionBar();

        Log.Debug($"Liste filtrée en {watch.Elapsed.TotalMilliseconds:F1} ms "
                  + $"({display.Count} lignes, {(unchanged ? "liste conservée" : "liste reconstruite")})", "Interface");
    }

    /// <summary>Dernier plan d'affichage appliqué, pour éviter une reconstruction inutile.</summary>
    /// <summary>
    /// Vrai quand le prochain filtrage vient d'un choix de l'utilisateur (vue,
    /// pastille, tri, dossier) : la liste apparaît alors en fondu. Ni la frappe
    /// ni les événements du serveur ne l'animent, la liste clignoterait.
    /// </summary>
    private bool _animateList;

    private List<(DeviceRow? Source, string Group)> _lastPlan = new();
    private List<object> _lastDisplay = new();

    /// <summary>Machines de la liste filtrée, dossiers repliés compris.</summary>
    private int _shownRowCount;

    // MARK: - Actions : double-clic = connexion directe

    private void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Le double-clic d'un bouton de la ligne (action rapide, étoile)
        // remonte jusqu'ici : il ne doit pas ouvrir en plus le bureau.
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        // La ligne sous le curseur, pas la sélection : avec la sélection
        // multiple, SelectedItem peut désigner une autre machine.
        if (DeviceRowAt(e.OriginalSource) is { } row) OpenDesktop(row.Node);
    }

    private bool IsInsideButton(DependencyObject? element)
    {
        for (var current = element; current is not null && current != DeviceList;
             current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ButtonBase) return true;
        }
        return false;
    }

    /// <summary>
    /// Action rapide d'une ligne, montrée au survol. Passe par le même
    /// dispatcheur que le menu contextuel et la palette.
    /// </summary>
    private void RowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DeviceRow row, CommandParameter: string name }) return;

        // La ligne devient la sélection : c'est sur elle que portera un F2 ou
        // un clic droit juste après.
        SelectOnly(row);

        if (name == "Watch")
        {
            SetWatched([row.Node], !row.IsWatched);
            return;
        }
        if (Enum.TryParse<DeviceAction>(name, out var action)) RunAction(row.Node, action);
    }

    /// <summary>Ouvre le bureau à distance, en signalant les cas impossibles.</summary>
    private void OpenDesktop(MeshNode node)
    {
        if (_connection is null) return;

        if (!node.IsOnline)
        {
            MessageBox.Show($"« {node.Name} » est hors ligne.\n\nUtilisez le clic droit pour tenter un réveil (Wake-on-LAN).",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!node.HasAgent)
        {
            MessageBox.Show($"« {node.Name} » n'a pas d'agent installé : le bureau à distance n'est pas disponible.",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RememberRecent(node);
        OpenSession(new Desktop.DesktopWindow(_connection, node, _settings), node.Name, "🖥");
    }

    /// <summary>
    /// Présente une session selon la préférence : dans un onglet de la fenêtre
    /// commune, ou dans sa propre fenêtre.
    /// </summary>
    private void OpenSession(Window session, string title, string glyph)
    {
        if (_settings.UseTabbedSessions)
        {
            SessionsWindow.AddSession(session, title, glyph);
        }
        else
        {
            // Owner volontairement non défini : la session survit à une
            // réduction de la fenêtre principale dans la zone de notification.
            session.Show();
            session.Activate();
        }
    }

    // MARK: - Envoi de fichiers vers plusieurs machines

    private void SendFiles_Click(object sender, RoutedEventArgs e)
        => OpenSendFiles(SelectedNodes.Where(IsReachable).ToList());

    /// <summary>
    /// Ouvre la fenêtre d'envoi, ces machines déjà cochées et, le cas échéant,
    /// ces fichiers déjà choisis.
    /// </summary>
    private void OpenSendFiles(IReadOnlyList<MeshNode> nodes, IEnumerable<string>? files = null)
    {
        if (_connection is null) return;

        new Files.SendFilesWindow(_connection, _settings, nodes, files) { Owner = ActionOwner }.Show();
    }

    /// <summary>
    /// Survol d'un dépôt de fichiers sur la liste : la machine visée se
    /// sélectionne, pour qu'on sache où les fichiers vont partir.
    /// </summary>
    private void DeviceList_DragOver(object sender, DragEventArgs e)
    {
        var row = DeviceRowAt(e.OriginalSource);
        var acceptable = e.Data.GetDataPresent(DataFormats.FileDrop)
                         && row?.Node is { IsOnline: true, HasAgent: true };

        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (acceptable) SelectOnly(row!);
    }

    /// <summary>
    /// Fichiers lâchés sur une machine : la fenêtre d'envoi s'ouvre, prête à
    /// partir, et l'on peut encore y cocher d'autres machines.
    /// </summary>
    private void DeviceList_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        if (DeviceRowAt(e.OriginalSource)?.Node is not { IsOnline: true, HasAgent: true } node) return;

        OpenSendFiles([node], paths);
    }

    private DeviceRow? DeviceRowAt(object source)
        => source is DependencyObject element
           && ItemsControl.ContainerFromElement(DeviceList, element) is ListBoxItem { DataContext: DeviceRow row }
            ? row : null;

    // MARK: - Actions : clic droit = menu contextuel

    /// <summary>
    /// Sélectionne la ligne sous le curseur avant d'ouvrir le menu. Une ligne
    /// déjà sélectionnée garde la sélection multiple : le menu porte alors sur
    /// toutes les machines choisies.
    /// </summary>
    private void DeviceList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(DeviceList, (DependencyObject)e.OriginalSource)
                   as ListBoxItem;
        if (item is { IsSelected: false, DataContext: DeviceRow row }) SelectOnly(row);
    }

    /// <summary>Active/désactive les entrées selon l'état réel de la machine.</summary>
    private void DeviceMenu_Opened(object sender, RoutedEventArgs e)
    {
        var nodes = SelectedNodes;
        if (nodes.Count > 1)
        {
            ConfigureMenuForSelection(nodes);
            return;
        }
        MenuCopyName.Header = "Copier le nom";
        foreach (var single in SingleOnlyMenuItems) single.Visibility = Visibility.Visible;

        var node = nodes.FirstOrDefault();
        bool reachable = node is { IsOnline: true, HasAgent: true };

        MenuWatch.Visibility = node is { IsOnline: false } ? Visibility.Visible : Visibility.Collapsed;
        MenuWatch.Header = node is not null && _watchOnline.Contains(node.Id)
            ? "Ne plus me prévenir de son retour"
            : "Me prévenir à son retour";

        MenuDesktop.IsEnabled = reachable;
        // Terminal et fichiers passent par l'agent : mêmes prérequis que le
        // bureau à distance.
        MenuTerminal.IsEnabled = reachable;
        MenuFiles.IsEnabled = reachable;
        MenuSendFiles.IsEnabled = reachable;
        // SSH passe par le serveur (c'est lui le client SSH) : l'agent n'est pas
        // requis, une machine sans agent mais joignable en réseau fonctionne.
        MenuSsh.IsEnabled = node is not null
            && (node.IsOnline || _connection?.IsLocalDevice(node) == true);
        MenuReboot.IsEnabled = reachable;
        MenuSleep.IsEnabled = reachable;
        MenuPowerOff.IsEnabled = reachable;
        MenuWake.IsEnabled = node is not null;      // le réveil vise justement les machines éteintes
        // Processus et ouverture d'URL passent par l'agent : machine joignable requise.
        MenuProcesses.IsEnabled = reachable;
        MenuOpenUrl.IsEnabled = reachable;

        MenuFavorite.IsEnabled = node is not null;
        MenuFavorite.Header = node is not null && _settings.IsFavorite(node.Id)
            ? "Retirer des favoris"
            : "Ajouter aux favoris";

        // Le partage se crée côté serveur : la machine n'a pas besoin d'être en
        // ligne au moment où on génère le lien.
        MenuShare.IsEnabled = node is not null;
        // Le nom vit sur le serveur : on renomme aussi une machine hors ligne.
        MenuRename.IsEnabled = node is not null;
        MenuCopyName.IsEnabled = node is not null;
        MenuInfo.IsEnabled = node is not null;
        // La page web est servie par MeshCentral : elle s'ouvre même pour une
        // machine hors ligne (journaux, notes, réglages du groupe).
        MenuWebPage.IsEnabled = node is not null;
    }

    /// <summary>
    /// Une action est-elle applicable à cette machine ? Règles communes au menu
    /// contextuel et à la palette, pour éviter deux vérités divergentes.
    /// </summary>
    public bool IsActionAvailable(MeshNode node, DeviceAction action)
    {
        bool reachable = node is { IsOnline: true, HasAgent: true };

        return action switch
        {
            // Ces quatre passent par l'agent installé sur la machine.
            DeviceAction.Desktop or DeviceAction.Terminal
                or DeviceAction.Files or DeviceAction.SendFiles or DeviceAction.Processes
                or DeviceAction.OpenUrl => reachable,

            DeviceAction.Reboot or DeviceAction.Sleep
                or DeviceAction.PowerOff => reachable,

            // SSH passe par le serveur : l'agent n'est pas requis.
            DeviceAction.Ssh => node.IsOnline || _connection?.IsLocalDevice(node) == true,

            // Le reste est traité côté serveur ou localement.
            _ => true,
        };
    }

    // MARK: - Sélection multiple

    /// <summary>
    /// Machines sélectionnées, sans doublon : une machine peut figurer deux
    /// fois dans la liste (section Favoris et son dossier).
    /// </summary>
    private List<MeshNode> SelectedNodes => DeviceList.SelectedItems.OfType<DeviceRow>()
        .Select(r => r.Node).DistinctBy(n => n.Id).ToList();

    private static bool IsReachable(MeshNode node) => node is { IsOnline: true, HasAgent: true };

    /// <summary>Ne garde que cette ligne sélectionnée.</summary>
    private void SelectOnly(DeviceRow row)
    {
        if (DeviceList.SelectedItems.Count == 1 && ReferenceEquals(DeviceList.SelectedItem, row)) return;
        DeviceList.SelectedItems.Clear();
        DeviceList.SelectedItem = row;
    }

    private void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionBar();

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        DeviceList.UnselectAll();
        DeviceList.Focus();
    }

    /// <summary>
    /// Barre des actions groupées : visible à partir de deux machines, chaque
    /// bouton actif seulement si une machine au moins s'y prête.
    /// </summary>
    private void UpdateSelectionBar()
    {
        var nodes = SelectedNodes;
        if (nodes.Count < 2)
        {
            SelectionBar.Visibility = Visibility.Collapsed;
            return;
        }

        var online = nodes.Count(n => n.IsOnline);
        SelectionText.Text = $"{nodes.Count} machines sélectionnées · {online} en ligne";
        BulkWakeButton.IsEnabled = nodes.Any(n => !n.IsOnline);
        var anyReachable = nodes.Any(IsReachable);
        BulkRebootButton.IsEnabled = anyReachable;
        BulkPowerOffButton.IsEnabled = anyReachable;
        BulkSendFilesButton.IsEnabled = anyReachable;
        BulkFavoriteButton.Content = nodes.Any(n => !_settings.IsFavorite(n.Id))
            ? "Ajouter aux favoris" : "Retirer des favoris";
        if (SelectionBar.Visibility != Visibility.Visible)
        {
            SelectionBar.Visibility = Visibility.Visible;
            Motion.FadeIn(SelectionBar, 0, 180, slideY: 10);
        }
    }

    private void BulkWake_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Wake);
    private void BulkReboot_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Reboot);
    private void BulkPowerOff_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.PowerOff);
    private void BulkFavorite_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.ToggleFavorite);
    private void BulkSendFiles_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.SendFiles);

    /// <summary>Entrées du menu qui ne valent que pour une machine à la fois.</summary>
    private IEnumerable<FrameworkElement> SingleOnlyMenuItems =>
    [
        MenuDesktop, MenuTerminal, MenuFiles, MenuSsh, MenuToolsSeparator, MenuProcesses,
        MenuOpenUrl, MenuShare, MenuRename, MenuInfo, MenuWebPage,
    ];

    /// <summary>Menu contextuel sur plusieurs machines : seules les actions groupées.</summary>
    private void ConfigureMenuForSelection(List<MeshNode> nodes)
    {
        foreach (var single in SingleOnlyMenuItems) single.Visibility = Visibility.Collapsed;

        var anyReachable = nodes.Any(IsReachable);
        var offline = nodes.Where(n => !n.IsOnline).ToList();
        MenuSendFiles.IsEnabled = anyReachable;
        MenuWake.IsEnabled = offline.Count > 0;
        MenuReboot.IsEnabled = anyReachable;
        MenuSleep.IsEnabled = anyReachable;
        MenuPowerOff.IsEnabled = anyReachable;

        MenuWatch.Visibility = offline.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MenuWatch.Header = offline.Any(n => !_watchOnline.Contains(n.Id))
            ? $"Me prévenir de leur retour ({offline.Count})"
            : $"Ne plus me prévenir de leur retour ({offline.Count})";

        MenuFavorite.IsEnabled = true;
        MenuFavorite.Header = nodes.Any(n => !_settings.IsFavorite(n.Id))
            ? $"Ajouter aux favoris ({nodes.Count})"
            : $"Retirer des favoris ({nodes.Count})";
        MenuCopyName.IsEnabled = true;
        MenuCopyName.Header = $"Copier les noms ({nodes.Count})";
    }

    /// <summary>Actions qui ont un sens sur plusieurs machines à la fois.</summary>
    private static bool IsBulkAction(DeviceAction action) => action is DeviceAction.Wake
        or DeviceAction.Reboot or DeviceAction.Sleep or DeviceAction.PowerOff
        or DeviceAction.SendFiles or DeviceAction.ToggleFavorite or DeviceAction.CopyName;

    private static string Machines(int count) => count == 1 ? "1 machine" : $"{count} machines";

    /// <summary>Noms à confirmer, tronqués au-delà de dix.</summary>
    private static string NameList(IReadOnlyList<MeshNode> nodes)
    {
        var names = nodes.Take(10).Select(n => $"• {n.Name}");
        var more = nodes.Count > 10 ? $"\n… et {nodes.Count - 10} autres" : "";
        return string.Join("\n", names) + more;
    }

    /// <summary>
    /// Action sur plusieurs machines. Les ordres d'alimentation partent en un
    /// seul message au serveur, après une confirmation qui liste les machines ;
    /// celles qui ne peuvent pas les recevoir (hors ligne, sans agent) sont
    /// écartées et signalées.
    /// </summary>
    private async void RunBulk(IReadOnlyList<MeshNode> nodes, DeviceAction action)
    {
        if (_connection is null || nodes.Count == 0) return;
        var connection = _connection;
        var reachable = nodes.Where(IsReachable).ToList();
        var skipped = nodes.Count - reachable.Count;
        var skippedNote = skipped > 0
            ? $"\n\n{Machines(skipped)} hors ligne ou sans agent ne {(skipped == 1 ? "sera" : "seront")} pas concernée{(skipped == 1 ? "" : "s")}."
            : "";

        switch (action)
        {
            case DeviceAction.Wake:
                var asleep = nodes.Where(n => !n.IsOnline).ToList();
                if (asleep.Count == 0) return;
                await RunPowerAsync(() => connection.WakeAsync(asleep.Select(n => n.Id)),
                    $"Signal de réveil envoyé à {Machines(asleep.Count)}.");
                break;

            case DeviceAction.Reboot or DeviceAction.Sleep or DeviceAction.PowerOff:
                if (reachable.Count == 0)
                {
                    ShowToast("Aucune machine de la sélection n'est joignable.");
                    return;
                }
                var (verb, detail, type, done) = action switch
                {
                    DeviceAction.Reboot => ("Redémarrer", "Les travaux non enregistrés seront perdus.",
                                            PowerActionType.Reboot, "de redémarrage"),
                    DeviceAction.Sleep => ("Mettre en veille", "Elles ne répondront plus jusqu'à leur réveil.",
                                           PowerActionType.Sleep, "de mise en veille"),
                    _ => ("Éteindre", "Il faudra un réveil Wake-on-LAN ou une action physique pour les rallumer.",
                          PowerActionType.PowerOff, "d'extinction"),
                };
                if (!Confirm($"{verb} {Machines(reachable.Count)} ?",
                             $"{NameList(reachable)}\n\n{detail}{skippedNote}")) return;
                await RunPowerAsync(() => connection.PowerActionAsync(reachable.Select(n => n.Id), type),
                    $"Ordre {done} envoyé à {Machines(reachable.Count)}.");
                break;

            case DeviceAction.SendFiles:
                OpenSendFiles(reachable);
                break;

            case DeviceAction.ToggleFavorite:
                // Ajout si l'une d'elles n'est pas favorite, retrait sinon :
                // comme une case à cocher sur une sélection mêlée.
                var add = nodes.Any(n => !_settings.IsFavorite(n.Id));
                foreach (var node in nodes.Where(n => _settings.IsFavorite(n.Id) != add))
                {
                    _settings.ToggleFavorite(node.Id);
                    if (_rowsById.TryGetValue(node.Id, out var row)) row.IsFavorite = add;
                }
                _settings.Save();
                ApplyFilter();
                ShowToast(add ? $"{Machines(nodes.Count)} dans les favoris."
                              : $"{Machines(nodes.Count)} retirée{(nodes.Count == 1 ? "" : "s")} des favoris.");
                break;

            case DeviceAction.CopyName:
                try
                {
                    Clipboard.SetText(string.Join(Environment.NewLine, nodes.Select(n => n.Name)));
                    ShowToast($"{nodes.Count} noms copiés.");
                }
                catch { /* presse-papiers occupé */ }
                break;
        }
    }

    // MARK: - Alerte de retour en ligne

    /// <summary>
    /// Machines dont on attend le retour en ligne. En mémoire seulement : une
    /// alerte sert pendant qu'on attend, pas d'une semaine sur l'autre.
    /// </summary>
    private readonly HashSet<string> _watchOnline = new();

    /// <summary>Machine visée par la dernière bulle de notification, le cas échéant.</summary>
    private string? _balloonNodeId;

    private void MenuWatch_Click(object sender, RoutedEventArgs e)
    {
        var offline = SelectedNodes.Where(n => !n.IsOnline).ToList();
        if (offline.Count == 0) return;
        SetWatched(offline, offline.Any(n => !_watchOnline.Contains(n.Id)));
    }

    /// <summary>Pose ou retire l'alerte de retour en ligne sur ces machines.</summary>
    private void SetWatched(IReadOnlyList<MeshNode> nodes, bool watch)
    {
        foreach (var node in nodes)
        {
            if (watch && !node.IsOnline) _watchOnline.Add(node.Id);
            else _watchOnline.Remove(node.Id);
        }
        RefreshWatchMarks();

        if (nodes.Count == 1)
            ShowToast(watch
                ? $"Vous serez prévenu dès que « {nodes[0].Name} » sera en ligne."
                : $"Alerte retirée pour « {nodes[0].Name} ».");
        else
            ShowToast(watch
                ? $"Vous serez prévenu du retour de {Machines(nodes.Count)}."
                : $"Alertes retirées pour {Machines(nodes.Count)}.");
    }

    /// <summary>Aligne les cloches des lignes (réelles et copies) sur les alertes posées.</summary>
    private void RefreshWatchMarks()
    {
        foreach (var row in _rowsById.Values.Concat(_virtualRows))
            row.IsWatched = _watchOnline.Contains(row.Node.Id);
    }

    /// <summary>
    /// Appelée à chaque mise à jour de l'inventaire : une machine attendue qui
    /// vient de se connecter déclenche une notification Windows, une seule fois.
    /// </summary>
    private void CheckWatchedNodes()
    {
        if (_watchOnline.Count == 0 || _connection is not { } connection) return;

        var back = _watchOnline
            .Select(id => connection.Nodes.TryGetValue(id, out var node) ? node : null)
            .OfType<MeshNode>()
            .Where(n => n.IsOnline)
            .ToList();
        if (back.Count == 0) return;

        foreach (var node in back)
        {
            _watchOnline.Remove(node.Id);
            Log.Info($"Retour en ligne signalé : {node.Name}", "Appareil");
        }
        RefreshWatchMarks();

        var first = back[0];
        var title = back.Count == 1 ? $"« {first.Name} » est en ligne" : $"{back.Count} machines sont en ligne";
        var message = back.Count == 1
            ? "Cliquez ici pour ouvrir le bureau à distance."
            : string.Join(", ", back.Select(n => n.Name));
        ShowTrayMessage(title, message, back.Count == 1 ? first.Id : null);
        if (IsVisible)
        {
            ToastText.Text = back.Count == 1 ? $"« {first.Name} » est de retour en ligne." : $"{title}.";
            ToastBanner.Visibility = Visibility.Visible;
            ToastBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }

    /// <summary>
    /// Bulle de la zone de notification. nodeId désigne la machine à ouvrir si
    /// l'on clique dessus ; sans elle, le clic ramène la fenêtre principale.
    /// </summary>
    private void ShowTrayMessage(string title, string message, string? nodeId = null)
    {
        _balloonNodeId = nodeId;
        _tray?.ShowMessage(title, message);
    }

    private void OnBalloonClicked()
    {
        var nodeId = _balloonNodeId;
        _balloonNodeId = null;
        if (nodeId is not null && _connection?.Nodes.TryGetValue(nodeId, out var node) == true
            && IsReachable(node))
        {
            OpenDesktop(node);
            return;
        }
        RestoreMainWindow();
    }

    // MARK: - Clavier dans la liste

    /// <summary>
    /// Raccourcis de la liste : Entrée ouvre le bureau à distance, Ctrl+T le
    /// terminal, Ctrl+E les fichiers, Échap défait une sélection multiple.
    /// Ctrl+F renvoie à la recherche depuis n'importe où dans la fenêtre.
    /// </summary>
    private void HandleListShortcut(KeyEventArgs args)
    {
        if (IsSettingsOpen || DevicesPanel.Visibility != Visibility.Visible) return;
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;

        if (ctrl && args.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            args.Handled = true;
            return;
        }

        if (!DeviceList.IsKeyboardFocusWithin || Keyboard.FocusedElement is TextBox) return;

        if (args.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            // La ligne qui porte le focus : c'est elle qu'on voit encadrée.
            var row = (Keyboard.FocusedElement as ListBoxItem)?.DataContext as DeviceRow
                      ?? DeviceList.SelectedItem as DeviceRow;
            if (row is not null) OpenDesktop(row.Node);
            args.Handled = true;
        }
        else if (ctrl && args.Key == Key.T)
        {
            RunSelected(DeviceAction.Terminal);
            args.Handled = true;
        }
        else if (ctrl && args.Key == Key.E)
        {
            RunSelected(DeviceAction.Files);
            args.Handled = true;
        }
        else if (args.Key == Key.Escape && DeviceList.SelectedItems.Count > 1)
        {
            DeviceList.UnselectAll();
            args.Handled = true;
        }
    }

    /// <summary>
    /// Taper une lettre dans la liste bascule dans la recherche, la lettre
    /// comprise : pas besoin de cliquer d'abord dans le champ.
    /// </summary>
    private void DeviceList_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        if (char.IsWhiteSpace(e.Text[0]) && string.IsNullOrEmpty(SearchBox.Text)) return;

        SearchBox.Focus();
        SearchBox.Text += e.Text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    /// <summary>
    /// Depuis la recherche : Flèche bas ou Entrée descend dans la liste ; Entrée
    /// ouvre directement le bureau quand une seule machine correspond. Échap
    /// efface la recherche.
    /// </summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
        {
            ClearSearch_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Down or Key.Enter)) return;

        // Résultat à jour avant de choisir : la frappe est filtrée en différé.
        if (_searchTimer.IsEnabled)
        {
            _searchTimer.Stop();
            ApplyFilter();
        }

        var rows = DeviceList.Items.OfType<DeviceRow>().ToList();
        if (rows.Count == 0) return;
        e.Handled = true;

        if (e.Key == Key.Enter && rows.Select(r => r.Node.Id).Distinct().Count() == 1)
        {
            SelectOnly(rows[0]);
            OpenDesktop(rows[0].Node);
            return;
        }
        FocusFirstRow(rows);
    }

    /// <summary>Donne le focus clavier à la première ligne visible.</summary>
    private void FocusFirstRow(List<DeviceRow> rows)
    {
        DeviceList.UpdateLayout();
        foreach (var row in rows)
        {
            if (DeviceList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem { IsVisible: true } item)
            {
                SelectOnly(row);
                item.Focus();
                return;
            }
        }
        DeviceList.Focus();
    }

    // MARK: - Dispatcheur d'actions

    /// <summary>
    /// Point d'entrée unique de toutes les actions sur une machine. Le menu
    /// contextuel et la palette de recherche rapide passent tous deux par ici :
    /// les deux offrent ainsi rigoureusement les mêmes possibilités, et une
    /// action ajoutée ici apparaît automatiquement des deux côtés.
    /// </summary>
    public async void RunAction(MeshNode node, DeviceAction action)
    {
        if (_connection is null) return;

        switch (action)
        {
            case DeviceAction.Desktop:
                OpenDesktop(node);
                break;

            case DeviceAction.Terminal:
                RememberRecent(node);
                OpenSession(new Ssh.TerminalWindow(_connection, node), node.Name, "▶");
                break;

            case DeviceAction.Files:
                RememberRecent(node);
                OpenSession(new Files.FilesWindow(_connection, node, _settings), node.Name, "📁");
                break;

            case DeviceAction.SendFiles:
                OpenSendFiles([node]);
                break;

            case DeviceAction.Ssh:
                RememberRecent(node);
                OpenSession(new Ssh.SshWindow(_connection, node), node.Name, "▶");
                break;

            case DeviceAction.Wake:
                // Le réveil n'est pas destructif : pas de confirmation.
                await RunPowerAsync(() => _connection.WakeAsync(node.Id),
                    $"Signal de réveil envoyé à « {node.Name} ».");
                break;

            case DeviceAction.Reboot:
                if (!Confirm($"Redémarrer « {node.Name} » ?",
                             "La machine va redémarrer. Les travaux non enregistrés seront perdus.")) return;
                await RunPowerAsync(() => _connection.PowerActionAsync(node.Id, PowerActionType.Reboot),
                    $"Ordre de redémarrage envoyé à « {node.Name} ».");
                break;

            case DeviceAction.Sleep:
                if (!Confirm($"Mettre « {node.Name} » en veille ?",
                             "La machine sera suspendue et ne répondra plus jusqu'à son réveil.")) return;
                await RunPowerAsync(() => _connection.PowerActionAsync(node.Id, PowerActionType.Sleep),
                    $"Ordre de mise en veille envoyé à « {node.Name} ».");
                break;

            case DeviceAction.PowerOff:
                if (!Confirm($"Éteindre « {node.Name} » ?",
                             "La machine sera arrêtée. Il faudra un réveil Wake-on-LAN ou une action physique pour la rallumer.")) return;
                await RunPowerAsync(() => _connection.PowerActionAsync(node.Id, PowerActionType.PowerOff),
                    $"Ordre d'extinction envoyé à « {node.Name} ».");
                break;

            case DeviceAction.Processes:
                new ProcessWindow(_connection, node) { Owner = ActionOwner }.Show();
                break;

            case DeviceAction.OpenUrl:
                await OpenRemoteUrlAsync(node);
                break;

            case DeviceAction.ToggleFavorite:
                ToggleFavorite(node);
                break;

            case DeviceAction.Share:
                new ShareWindow(_connection, node) { Owner = ActionOwner }.ShowDialog();
                break;

            case DeviceAction.Rename:
                await RenameDeviceAsync(node);
                break;

            case DeviceAction.CopyName:
                try
                {
                    Clipboard.SetText(node.Name);
                    ShowToast($"« {node.Name} » copié.");
                }
                catch { /* presse-papiers occupé */ }
                break;

            case DeviceAction.SystemInfo:
                new SystemInfoWindow(_connection, node) { Owner = ActionOwner }.Show();
                break;

            case DeviceAction.WebPage:
                OpenDeviceWebPage(node);
                break;
        }
    }

    /// <summary>
    /// Propriétaire des fenêtres ouvertes par une action. Quand l'action vient de
    /// la palette, la fenêtre principale peut être masquée dans la zone de
    /// notification : WPF refuse alors une fenêtre invisible comme propriétaire.
    /// </summary>
    private Window? ActionOwner => IsLoaded && IsVisible ? this : null;

    /// <summary>
    /// Ouvre la fiche de la machine dans MeshCentral, avec le navigateur par
    /// défaut. Utile pour accéder aux fonctions du serveur que l'application
    /// ne reprend pas (journaux, notes, réglages du groupe…).
    /// </summary>
    private void OpenDeviceWebPage(MeshNode node)
    {
        if (_connection?.Profile.DeviceWebUrl(node.Id) is not { } url)
        {
            MessageBox.Show("Adresse du serveur indisponible.", "Mesh Control",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            Log.Info($"Page web ouverte pour {node.Name}", "Appareil");
        }
        catch (Exception ex)
        {
            // Pas de navigateur associé : on laisse au moins l'adresse sous la main.
            try { Clipboard.SetText(url); } catch { }
            MessageBox.Show($"Impossible d'ouvrir le navigateur : {ex.Message}\n\n"
                          + "L'adresse a été copiée dans le presse-papiers.",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Renomme une machine dans MeshCentral. Le nouveau nom vaut pour tous les
    /// utilisateurs du serveur ; le nom réseau de la machine, lui, ne change pas.
    /// </summary>
    private async Task RenameDeviceAsync(MeshNode node)
    {
        if (_connection is null) return;

        // Saisie libre : un nom d'appareil n'est pas un nom de fichier, et les
        // « / » y servent souvent de séparateurs (site / service / poste).
        var dialog = new TextPromptDialog(
            "Renommer l'appareil",
            $"Nouveau nom de « {node.Name} » :",
            node.Name, "Renommer", allowAnyCharacter: true) { Owner = ActionOwner };
        if (dialog.ShowDialog() != true || dialog.Value is not { } name) return;
        if (name == node.Name) return;

        // Limite imposée par le serveur, qui refuserait sans dire pourquoi.
        if (name.Length > 256)
        {
            MessageBox.Show("Le nom ne peut pas dépasser 256 caractères.", "Mesh Control",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var previous = node.Name;
        try
        {
            await _connection.RenameDeviceAsync(node.Id, name);
            Log.Info($"Appareil renommé : « {previous} » → « {name} »", "Appareil");
            ShowToast($"« {previous} » s'appelle désormais « {name} ».");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Renommer l'appareil",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task OpenRemoteUrlAsync(MeshNode node)
    {
        if (_connection is null) return;

        var dialog = new TextPromptDialog(
            "Ouvrir une URL",
            $"Adresse à ouvrir dans le navigateur de « {node.Name} » :",
            "https://", "Ouvrir", allowAnyCharacter: true) { Owner = ActionOwner };
        if (dialog.ShowDialog() != true || dialog.Value is null) return;

        try
        {
            await _connection.OpenUrlAsync(node.Id, dialog.Value);
            Log.Info($"URL ouverte sur {node.Name} : {dialog.Value}", "Appareil");
            ShowToast($"Adresse envoyée à « {node.Name} ».");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Mesh Control",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // MARK: - Entrées du menu contextuel

    private void MenuDesktop_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Desktop);
    private void MenuTerminal_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Terminal);
    private void MenuFiles_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Files);
    private void MenuSendFiles_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.SendFiles);
    private void MenuSsh_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Ssh);
    private void MenuWake_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Wake);
    private void MenuReboot_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Reboot);
    private void MenuSleep_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Sleep);
    private void MenuPowerOff_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.PowerOff);
    private void MenuProcesses_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Processes);
    private void MenuOpenUrl_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.OpenUrl);
    private void MenuShare_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Share);
    private void MenuRename_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Rename);
    private void MenuCopyName_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.CopyName);
    private void MenuInfo_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.SystemInfo);
    private void MenuWebPage_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.WebPage);

    /// <summary>
    /// Action du menu, de la barre de sélection ou d'un raccourci. Sur
    /// plusieurs machines, seules les actions groupées s'appliquent.
    /// </summary>
    private void RunSelected(DeviceAction action)
    {
        var nodes = SelectedNodes;
        if (nodes.Count > 1)
        {
            if (IsBulkAction(action)) RunBulk(nodes, action);
            return;
        }
        if (nodes.Count == 1) RunAction(nodes[0], action);
    }

    private static bool Confirm(string title, string detail) =>
        MessageBox.Show(detail, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            == MessageBoxResult.OK;

    /// <summary>
    /// Exécute une action d'alimentation. Le serveur ne confirme que la prise en
    /// compte de l'ordre, pas son exécution effective sur la machine.
    /// </summary>
    private async Task RunPowerAsync(Func<Task> action, string successMessage)
    {
        try
        {
            await action();
            ShowToast(successMessage);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Échec de l'action : {ex.Message}", "Mesh Control",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connection is null) return;
        await _connection.RefreshDeviceListAsync();
    }

    /// <summary>Ouvre le volet des réglages (voir MainWindow.Settings.cs).</summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsSettingsOpen) CloseSettings();
        else OpenSettings();
    }

    private void SetStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
    }

    // MARK: - Reprise après veille ou coupure réseau

    /// <summary>
    /// Reprise différée : au réveil du poste, la pile réseau met quelques
    /// secondes à redevenir utilisable, et les événements arrivent en rafale
    /// (plusieurs changements d'adresse successifs). Le minuteur les regroupe.
    /// </summary>
    private readonly DispatcherTimer _resyncTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5),
    };

    private string _resyncReason = "";

    /// <summary>
    /// Écoute la sortie de veille et les changements d'adresse réseau.
    ///
    /// Dans ces deux cas, le canal de contrôle est souvent mort sans que rien ne
    /// l'ait signalé : le poste n'a pas eu le temps de fermer proprement ses
    /// connexions, et le serveur ne peut plus rien envoyer. Sans cette écoute, il
    /// fallait attendre le contrôle de silence — voire relancer l'application.
    /// </summary>
    private void SetupResumeWatch()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged
            += OnNetworkAddressChanged;
    }

    /// <summary>
    /// Se détache de ces événements : ils sont portés par des objets statiques,
    /// qui garderaient la fenêtre en vie après sa fermeture.
    /// </summary>
    private void ReleaseResumeWatch()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged
            -= OnNetworkAddressChanged;
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        ScheduleResync("sortie de veille");
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
        => ScheduleResync("changement d'adresse réseau");

    /// <summary>Programme une vérification, en regroupant les appels rapprochés.</summary>
    private void ScheduleResync(string reason)
    {
        // Ces événements arrivent depuis des fils système : tout ce qui touche à
        // l'interface doit repasser par le sien.
        Dispatcher.BeginInvoke(() =>
        {
            _resyncReason = reason;
            _resyncTimer.Stop();
            _resyncTimer.Start();
        });
    }

    /// <summary>Vérifie sans attendre.</summary>
    private void Resync(string reason)
    {
        _resyncTimer.Stop();
        _resyncReason = reason;
        _ = ResyncAsync();
    }

    /// <summary>
    /// Remet l'affichage en phase avec le serveur : relecture de l'inventaire si
    /// la connexion tient, reprise immédiate de la reconnexion sinon.
    /// </summary>
    private async Task ResyncAsync()
    {
        if (_connection is null) return;

        if (_connection.State == ConnectionState.Connected)
        {
            Log.Info($"Relecture de l'inventaire ({_resyncReason})", "Serveur");
            try { await _connection.RefreshDeviceListAsync(); }
            catch { /* la perte de connexion est détectée par le contrôle de silence */ }
            return;
        }

        // Une attente entre deux tentatives est en cours : la raison probable de
        // l'échec vient de disparaître, inutile de patienter jusqu'au bout.
        _reconnectAttempt = 0;
        if (_reconnectWakeup?.TrySetResult() == true)
        {
            Log.Info($"Reconnexion avancée ({_resyncReason})", "Serveur");
            return;
        }

        if (CanAttemptReconnect()) _ = ReconnectLoopAsync();
    }

    // MARK: - Bandeau de confirmation

    /// <summary>Durée d'affichage d'une confirmation avant effacement.</summary>
    private readonly DispatcherTimer _toastTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5),
    };

    /// <summary>
    /// Affiche une confirmation temporaire.
    ///
    /// Ces messages allaient auparavant dans le compteur d'appareils, que le
    /// prochain événement du serveur réécrivait aussitôt : sur un parc actif,
    /// « Ordre de redémarrage envoyé » disparaissait en moins d'une seconde et
    /// l'utilisateur ne savait pas si son action était partie.
    /// </summary>
    private void ShowToast(string message)
    {
        // Action lancée depuis la palette de recherche rapide : la fenêtre
        // principale est alors souvent masquée dans la zone de notification, où
        // le bandeau serait invisible. On passe par une bulle système.
        if (!IsVisible)
        {
            ShowTrayMessage("Mesh Control", message);
            return;
        }

        ToastText.Text = message;
        ToastBanner.Visibility = Visibility.Visible;
        ToastBanner.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));

        // Un nouveau message relance le compte à rebours plutôt que de s'ajouter :
        // un seul bandeau, toujours le plus récent.
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>Efface le bandeau en fondu, à l'expiration du minuteur.</summary>
    private void HideToast()
    {
        _toastTimer.Stop();
        if (ToastBanner.Visibility != Visibility.Visible) return;

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        // Le retrait n'a lieu qu'à la fin du fondu : masquer tout de suite ferait
        // disparaître le bandeau d'un coup.
        fade.Completed += (_, _) => ToastBanner.Visibility = Visibility.Collapsed;
        ToastBanner.BeginAnimation(OpacityProperty, fade);
    }
}
