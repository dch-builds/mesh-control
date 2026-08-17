using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using MeshControl.Core;

namespace MeshControl.UI;

public partial class MainWindow : Window
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
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            RebuildDeviceList();
        };
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        // Échap referme le volet des réglages : réflexe attendu pour un panneau
        // superposé.
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && IsSettingsOpen)
            {
                CloseSettings();
                args.Handled = true;
            }
        };
        Closed += (_, _) =>
        {
            _hotkey?.Dispose();
            _tray?.Dispose();
            _connection?.Disconnect();
            // ShutdownMode est OnExplicitShutdown : sans cet appel, le processus
            // resterait actif après la fermeture de la fenêtre principale.
            Application.Current.Shutdown();
        };
    }

    // MARK: - Zone de notification

    private void SetupTray()
    {
        _tray = new TrayIcon();
        _tray.OpenRequested += RestoreMainWindow;
        _tray.QuickSearchRequested += ShowQuickSearch;
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
            _tray?.ShowMessage("Mesh Control reste actif",
                $"L'application continue de tourner ici. Raccourci : {_settings.QuickSearchHotkey}");
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Log.MinimumLevel = _settings.VerboseLogging ? LogLevel.Debug : LogLevel.Info;
        // Restaurer les dossiers repliés du lancement précédent.
        _collapsedGroups = new HashSet<string>(_settings.CollapsedGroups,
                                               StringComparer.OrdinalIgnoreCase);
        ApplySettingsToUi();
        SetupHotkey();
        SetupTray();

        // Démarrage automatique de session (« --minimized ») ou réglage explicite :
        // on se connecte en tâche de fond sans ouvrir la fenêtre.
        bool startHidden = _settings.StartMinimized
            || Environment.GetCommandLineArgs().Any(a =>
                   a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (startHidden) HideToTray();

        // Connexion automatique si des identifiants sont mémorisés : l'utilisateur
        // n'a plus rien à ressaisir au lancement.
        if (_settings.RememberCredentials
            && !string.IsNullOrEmpty(_settings.Username)
            && !string.IsNullOrEmpty(_settings.Password))
        {
            await ConnectAsync(_settings.Username, _settings.Password, token: null, remember: true);
        }
        else
        {
            ShowLogin();
        }
    }

    private void ApplySettingsToUi()
    {
        LoginServerLabel.Text = _settings.Host;
        SubtitleText.Text = _settings.Host;
        UserBox.Text = _settings.Username;
        RememberCheck.IsChecked = _settings.RememberCredentials;

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
            HotkeyHintText.Text = $"⌨ {readable} · recherche rapide";
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
            SubtitleText.Text = $"{_settings.Host} · raccourci {_settings.QuickSearchHotkey} indisponible (déjà utilisé)";
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
            node => _settings.IsFavorite(node.Id));
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

    private void RestoreMainWindow()
    {
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
        LoginPanel.Visibility = Visibility.Visible;
        DevicesPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        RefreshButton.Visibility = Visibility.Collapsed;
        if (string.IsNullOrEmpty(UserBox.Text)) UserBox.Focus();
        else PassBox.Focus();
    }

    private void ShowLoading(string detail = "")
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        DevicesPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        RefreshButton.Visibility = Visibility.Collapsed;
        LoadingDetail.Text = detail;
    }

    private void ShowDevices()
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        DevicesPanel.Visibility = Visibility.Visible;
        RefreshButton.Visibility = Visibility.Visible;
    }

    // MARK: - Connexion

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        await ConnectAsync(
            UserBox.Text.Trim(),
            PassBox.Password,
            string.IsNullOrWhiteSpace(TokenBox.Text) ? null : TokenBox.Text.Trim(),
            RememberCheck.IsChecked == true);
    }

    private void PassBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ConnectButton_Click(sender, e);
    }

    private async Task ConnectAsync(string user, string password, string? token, bool remember)
    {
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
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

        try
        {
            await _connection.ConnectAsync(password, token);

            // Mémoriser (ou effacer) les identifiants selon la case cochée.
            _settings.RememberCredentials = remember;
            _settings.Username = user;
            if (remember) _settings.Password = password;
            else _settings.ProtectedPassword = null;
            _settings.Save();

            SubtitleText.Text = $"{_settings.Host} · {_connection.UserInfo?.Name ?? user}";
            ShowDevices();
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

    private void OnStateChanged() => Dispatcher.Invoke(() =>
    {
        if (_connection is null) return;
        if (_connection.State != ConnectionState.Failed) return;

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
        if (string.IsNullOrEmpty(_sessionUser) || string.IsNullOrEmpty(_sessionPassword))
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
                await Task.Delay(TimeSpan.FromSeconds(delay));

                // L'utilisateur s'est reconnecté manuellement entre-temps.
                if (_connection?.State == ConnectionState.Connected) return;

                try
                {
                    var profile = new ServerProfile
                    {
                        Host = _settings.Host,
                        Username = _sessionUser!,
                        AllowSelfSigned = _settings.AllowSelfSigned,
                    };

                    var connection = new MeshServerConnection(profile);
                    connection.StateChanged += OnStateChanged;
                    connection.DevicesChanged += OnDevicesChanged;

                    await connection.ConnectAsync(_sessionPassword!);

                    // Remplacer l'ancienne connexion une fois la nouvelle établie.
                    if (_connection is not null)
                    {
                        _connection.StateChanged -= OnStateChanged;
                        _connection.DevicesChanged -= OnDevicesChanged;
                        _connection.Dispose();
                    }
                    _connection = connection;
                    _reconnectAttempt = 0;

                    Log.Info("Reconnexion réussie", "Serveur");
                    SubtitleText.Text = $"{_settings.Host} · {connection.UserInfo?.Name ?? _sessionUser}";
                    ShowDevices();
                    SetStatus("");
                    UpdateTrayTooltip();
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warning($"Tentative de reconnexion échouée : {ex.Message}", "Serveur");

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
        DeviceList.ItemsSource = null;
        CountText.Text = "";
    }

    /// <summary>Informe discrètement que la connexion est en cours de rétablissement.</summary>
    private void ShowReconnecting(int seconds)
    {
        SubtitleText.Text = $"{_settings.Host} · reconnexion dans {seconds} s…";
        _tray?.SetTooltip("Mesh Control — reconnexion en cours");
    }

    /// <summary>
    /// Appelé depuis les threads de fond à chaque événement serveur. On ne fait
    /// que (re)démarrer le timer : BeginInvoke est non bloquant, contrairement à
    /// Invoke qui figeait l'UI sous une rafale d'événements.
    /// </summary>
    private void OnDevicesChanged() => Dispatcher.BeginInvoke(() =>
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
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

            if (_rowsById.TryGetValue(node.Id, out var existing))
            {
                // Le tri dépend de l'état en ligne, du nom et du dossier : un
                // changement sur ces valeurs impose de recomposer la vue.
                // La comparaison porte sur l'état AFFICHÉ, car existing.Node est
                // la même instance que node — déjà mise à jour par la connexion.
                // L'ordre d'affichage ne dépend PAS de l'état en ligne (voir le
                // tri plus bas) : un passage en ligne ne recompose donc pas la
                // vue, seul le badge se met à jour. C'est ce qui évite que la
                // liste bouge sous le curseur à chaque événement du serveur.
                if (existing.NameChanged
                    || !string.Equals(existing.RealGroupName, groupName, StringComparison.Ordinal))
                {
                    structureChanged = true;
                }
                // Rafraîchit badge, libellé et icône sans toucher à la vue.
                existing.RefreshFromNode();
            }
            else
            {
                _rowsById[node.Id] = new DeviceRow(node, groupName, _settings.IsFavorite(node.Id));
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

        if (!structureChanged)
        {
            // Seules des valeurs d'affichage ont changé : les liaisons de données
            // s'en chargent, la vue reste intacte.
            UpdateCounters();
            return;
        }

        // Tri volontairement indépendant de l'état en ligne : sinon chaque
        // machine qui se connecte changerait l'ordre, et la liste sauterait sous
        // le curseur. Le badge et la pastille signalent l'état, c'est suffisant.
        _allRows = _rowsById.Values
            .OrderBy(r => r.RealGroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ApplyFilter();
    }

    /// <summary>Met à jour les compteurs sans toucher à la liste.</summary>
    private void UpdateCounters()
    {
        var displayed = (DeviceList.ItemsSource as System.Collections.IEnumerable)?
            .Cast<object>().Count() ?? _allRows.Count;
        var totalOnline = _allRows.Count(r => r.IsOnline);

        CountText.Text = displayed >= _allRows.Count
            ? $"{_allRows.Count} appareils · {totalOnline} en ligne"
            : $"{displayed} affichés sur {_allRows.Count} · {totalOnline} en ligne";
        UpdateTrayTooltip();
    }

    // MARK: - Recherche

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = string.IsNullOrEmpty(SearchBox.Text);
        SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilter();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    // MARK: - Favoris et machines récentes

    private const string FavoritesGroup = "★  Favoris";
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
        };
        _virtualRows.Add(copy);
        return copy;
    }

    /// <summary>
    /// Construit la vue groupée en gardant Favoris et Récentes en tête, puis les
    /// dossiers réels par ordre alphabétique.
    /// </summary>
    private System.ComponentModel.ICollectionView BuildGroupedView(List<DeviceRow> rows)
    {
        var ordered = rows
            .OrderBy(r => r.GroupName == FavoritesGroup ? 0
                        : r.GroupName == RecentGroup ? 1 : 2)
            .ToList();

        var view = new CollectionViewSource { Source = ordered };
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceRow.GroupName)));
        return view.View;
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
        _settings.Save();
        ApplyFilter();
    }

    // MARK: - Repli des dossiers

    /// <summary>
    /// Dossiers repliés, par nom. La liste étant reconstruite à chaque événement
    /// serveur, l'état doit vivre en dehors des Expander eux-mêmes : sinon ils
    /// se rouvriraient à la première mise à jour de statut.
    /// </summary>
    private HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Enregistre les dossiers repliés pour les retrouver au prochain lancement.</summary>
    private void SaveCollapsedGroups()
    {
        _settings.CollapsedGroups = _collapsedGroups.ToList();
        _settings.Save();
    }

    /// <summary>Vrai quand le bouton global doit tout déplier plutôt que tout replier.</summary>
    private bool _allGroupsCollapsed;

    /// <summary>Restaure l'état d'un dossier au moment où son Expander apparaît.</summary>
    private void GroupExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander) return;
        var name = GroupNameOf(expander);
        if (name is null) return;

        // Sans détacher les handlers, l'affectation déclencherait Expanded/Collapsed
        // et réécrirait l'état qu'on est justement en train de restaurer.
        expander.Expanded -= GroupExpander_Toggled;
        expander.Collapsed -= GroupExpander_Toggled;
        expander.IsExpanded = !_collapsedGroups.Contains(name);
        expander.Expanded += GroupExpander_Toggled;
        expander.Collapsed += GroupExpander_Toggled;
    }

    /// <summary>Mémorise le repli/dépli déclenché par l'utilisateur.</summary>
    private void GroupExpander_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander) return;
        var name = GroupNameOf(expander);
        if (name is null) return;

        if (expander.IsExpanded) _collapsedGroups.Remove(name);
        else _collapsedGroups.Add(name);
        SaveCollapsedGroups();
    }

    /// <summary>Nom du dossier porté par l'Expander (via le groupe lié).</summary>
    private static string? GroupNameOf(Expander expander)
        => (expander.DataContext as CollectionViewGroup)?.Name as string;

    private void ToggleGroups_Click(object sender, RoutedEventArgs e)
    {
        _allGroupsCollapsed = !_allGroupsCollapsed;

        _collapsedGroups.Clear();
        if (_allGroupsCollapsed)
        {
            foreach (var name in _allRows.Select(r => r.GroupName).Distinct())
                _collapsedGroups.Add(name);
        }

        ApplyGroupStatesToVisibleExpanders();
        UpdateToggleGroupsButton();
        SaveCollapsedGroups();
    }

    /// <summary>Applique l'état mémorisé aux Expander déjà affichés.</summary>
    private void ApplyGroupStatesToVisibleExpanders()
    {
        foreach (var expander in FindVisualChildren<Expander>(DeviceList))
        {
            var name = GroupNameOf(expander);
            if (name is null) continue;
            expander.IsExpanded = !_collapsedGroups.Contains(name);
        }
    }

    private void UpdateToggleGroupsButton()
    {
        ToggleGroupsGlyph.Text = _allGroupsCollapsed ? "⌄" : "⌃";
        ToggleGroupsButton.ToolTip = _allGroupsCollapsed
            ? "Déplier tous les dossiers"
            : "Replier tous les dossiers";
    }

    /// <summary>Parcourt l'arbre visuel à la recherche d'un type d'élément.</summary>
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private void OnlineOnly_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var onlineOnly = OnlineOnlyCheck.IsChecked == true;
        var selectedId = (DeviceList.SelectedItem as DeviceRow)?.Node.Id;

        IEnumerable<DeviceRow> source = _allRows;
        if (onlineOnly) source = source.Where(r => r.IsOnline);
        if (!string.IsNullOrEmpty(query))
            source = source.Where(r =>
                r.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (r.Node.OsDescription?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));

        var rows = source.ToList();

        // Sections virtuelles en tête : favoris puis machines récentes. Une même
        // machine peut apparaître deux fois (en favori et dans son dossier) ;
        // c'est voulu, comme un raccourci vers l'entrée réelle.
        _virtualRows.Clear();
        var display = new List<DeviceRow>();

        if (string.IsNullOrEmpty(query))
        {
            // Ordre stable, indépendant de l'état en ligne (voir RebuildDeviceList).
            var favorites = rows.Where(r => r.IsFavorite)
                                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                                .Select(r => Clone(r, FavoritesGroup));
            display.AddRange(favorites);

            // Récentes : dans l'ordre d'utilisation, favoris exclus (déjà en haut).
            var byId = rows.ToDictionary(r => r.Node.Id, r => r);
            foreach (var id in _settings.RecentNodeIds)
            {
                if (byId.TryGetValue(id, out var row) && !row.IsFavorite)
                    display.Add(Clone(row, RecentGroup));
            }
        }

        display.AddRange(rows);
        DeviceList.ItemsSource = BuildGroupedView(display);

        var totalOnline = _allRows.Count(r => r.IsOnline);
        CountText.Text = rows.Count == _allRows.Count
            ? $"{_allRows.Count} appareils · {totalOnline} en ligne"
            : $"{rows.Count} affichés sur {_allRows.Count} · {totalOnline} en ligne";
        // Le bouton de repli n'a de sens qu'avec plusieurs dossiers affichés.
        ToggleGroupsButton.Visibility = rows.Select(r => r.GroupName).Distinct().Skip(1).Any()
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateTrayTooltip();

        if (selectedId is not null)
            DeviceList.SelectedItem = rows.FirstOrDefault(r => r.Node.Id == selectedId);
    }

    // MARK: - Actions : double-clic = connexion directe

    private void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceRow row) OpenDesktop(row.Node);
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

    // MARK: - Actions : clic droit = menu contextuel

    /// <summary>Sélectionne la ligne sous le curseur avant d'ouvrir le menu.</summary>
    private void DeviceList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemsControl.ContainerFromElement(DeviceList, (DependencyObject)e.OriginalSource)
                   as ListBoxItem;
        if (item is not null) item.IsSelected = true;
    }

    /// <summary>Active/désactive les entrées selon l'état réel de la machine.</summary>
    private void DeviceMenu_Opened(object sender, RoutedEventArgs e)
    {
        var node = (DeviceList.SelectedItem as DeviceRow)?.Node;
        bool reachable = node is { IsOnline: true, HasAgent: true };

        MenuDesktop.IsEnabled = reachable;
        // Terminal et fichiers passent par l'agent : mêmes prérequis que le
        // bureau à distance.
        MenuTerminal.IsEnabled = reachable;
        MenuFiles.IsEnabled = reachable;
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
                or DeviceAction.Files or DeviceAction.Processes
                or DeviceAction.OpenUrl => reachable,

            DeviceAction.Reboot or DeviceAction.Sleep
                or DeviceAction.PowerOff => reachable,

            // SSH passe par le serveur : l'agent n'est pas requis.
            DeviceAction.Ssh => node.IsOnline || _connection?.IsLocalDevice(node) == true,

            // Le reste est traité côté serveur ou localement.
            _ => true,
        };
    }

    private MeshNode? SelectedNode => (DeviceList.SelectedItem as DeviceRow)?.Node;

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
                OpenSession(new Files.FilesWindow(_connection, node), node.Name, "📁");
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

            case DeviceAction.CopyName:
                try { Clipboard.SetText(node.Name); } catch { /* presse-papiers occupé */ }
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
            CountText.Text = $"Adresse envoyée à « {node.Name} ».";
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
    private void MenuSsh_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Ssh);
    private void MenuWake_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Wake);
    private void MenuReboot_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Reboot);
    private void MenuSleep_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Sleep);
    private void MenuPowerOff_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.PowerOff);
    private void MenuProcesses_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Processes);
    private void MenuOpenUrl_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.OpenUrl);
    private void MenuShare_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.Share);
    private void MenuCopyName_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.CopyName);
    private void MenuInfo_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.SystemInfo);
    private void MenuWebPage_Click(object sender, RoutedEventArgs e) => RunSelected(DeviceAction.WebPage);

    private void RunSelected(DeviceAction action)
    {
        if (SelectedNode is { } node) RunAction(node, action);
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
            CountText.Text = successMessage;
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
}
