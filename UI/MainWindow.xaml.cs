using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Animation;
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
        StateChanged += OnWindowStateChanged;
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
        ApplyListDensity();
        SetupHotkey();
        SetupTray();
        SetupResumeWatch();

        // Démarrage automatique de session (« --minimized ») ou réglage explicite :
        // on se connecte en tâche de fond sans ouvrir la fenêtre.
        bool startHidden = _settings.StartMinimized
            || Environment.GetCommandLineArgs().Any(a =>
                   a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (startHidden) HideToTray();

        // Connexion automatique si des identifiants sont mémorisés : l'utilisateur
        // n'a plus rien à ressaisir au lancement. Un jeton de connexion suffit,
        // même sans mot de passe enregistré.
        if (_settings.RememberCredentials
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
        SubtitleText.Text = _settings.Host;
        UserBox.Text = _settings.Username;
        RememberCheck.IsChecked = _settings.RememberCredentials;
        ApplyFolderView();

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
        ApplyFolderView();
        RebuildFolderPanel();
        ApplyFilter();
    }

    /// <summary>Montre ou masque le volet, et met le bouton en accord.</summary>
    private void ApplyFolderView()
    {
        bool on = _settings.FolderView;
        FolderPane.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ToggleViewButton.ToolTip = on ? "Revenir à la liste groupée"
                                      : "Afficher les dossiers à gauche";
        ToggleViewGlyph.Foreground = (System.Windows.Media.Brush)FindResource(
            on ? "AccentBrush" : "TextSecondaryBrush");
    }

    /// <summary>
    /// Reconstruit le volet depuis <c>_allRows</c>. Appelé quand la composition
    /// du parc change, et non à chaque événement serveur : la sélection serait
    /// perdue en continu. Elle est rétablie par clé, le volet étant recréé.
    /// </summary>
    private void RebuildFolderPanel()
    {
        if (!_settings.FolderView) return;

        // Segoe MDL2 Assets : E71D liste complète, E735 étoile, ED25 dossier.
        // Attention, E8B7 — que la documentation donne pour « Folder » — ne
        // dessine rien dans cette police et s'affiche en carré vide.
        var rows = new List<FolderRow>
        {
            new(FolderRow.AllKey, "Toutes les machines", "", "#8A8A8E",
                _allRows.Count, _allRows.Count(r => r.IsOnline)),
        };

        var favorites = _allRows.Where(r => r.IsFavorite).ToList();
        if (favorites.Count > 0)
            rows.Add(new FolderRow(FavoritesGroup, "Favoris", "", "#FFB300",
                                   favorites.Count, favorites.Count(r => r.IsOnline)));

        rows.AddRange(_allRows
            .GroupBy(r => r.RealGroupName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FolderRow(g.Key, g.Key, "", "#8A8A8E",
                                       g.Count(), g.Count(r => r.IsOnline))));

        _restoringFolder = true;
        FolderList.ItemsSource = rows;
        var wanted = _selectedFolderKey ?? _settings.SelectedFolder ?? FolderRow.AllKey;
        FolderList.SelectedItem = rows.FirstOrDefault(f => f.Key == wanted) ?? rows[0];
        _selectedFolderKey = (FolderList.SelectedItem as FolderRow)?.Key;
        _restoringFolder = false;
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringFolder) return;
        if (FolderList.SelectedItem is not FolderRow folder) return;

        _selectedFolderKey = folder.Key;
        _settings.SelectedFolder = folder.Key;
        _settings.Save();
        ApplyFilter();
    }

    // MARK: - Barre de titre

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>
    /// Fermer passe par Close() et non par un arrêt : c'est la logique
    /// habituelle qui décide ensuite de masquer la fenêtre dans la zone de
    /// notification ou de quitter pour de bon.
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Agrandie, une fenêtre dont l'application dessine elle-même le cadre
    /// déborde de sa bordure de redimensionnement : les bords passent sous ceux
    /// de l'écran et la barre de titre se retrouve pour partie hors champ. On
    /// compense par une marge, et on bascule au passage le glyphe du bouton
    /// entre « agrandir » et « restaurer ».
    /// </summary>
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;

        WindowRoot.Margin = maximized
            ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left
                            + SystemParameters.WindowNonClientFrameThickness.Left)
            : new Thickness(0);

        // Segoe MDL2 Assets : E922 agrandir, E923 restaurer.
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restaurer" : "Agrandir";
    }

    /// <summary>
    /// Windows 11 arrondit les angles des fenêtres, mais pas celles dont
    /// l'application dessine elle-même le cadre : il faut le lui demander.
    /// Sans cela, la fenêtre tranche par ses angles carrés au milieu d'un bureau
    /// qui les arrondit tous. L'attribut n'existe pas avant Windows 11, où
    /// l'appel échoue sans conséquence.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        const int CornerPreference = 33;   // DWMWA_WINDOW_CORNER_PREFERENCE
        const int Round = 2;               // DWMWCP_ROUND

        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int preference = Round;
            DwmSetWindowAttribute(handle, CornerPreference, ref preference, sizeof(int));
        }
        catch { /* antérieur à Windows 11 : les angles restent carrés */ }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
                                                    ref int value, int size);

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
        LoginPanel.Visibility = Visibility.Visible;
        DevicesPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        RefreshButton.Visibility = Visibility.Collapsed;

        // Le second facteur ne s'affiche que lorsque le serveur le réclame :
        // revenir à l'écran de connexion repart d'un formulaire propre.
        TwoFactorPanel.Visibility = Visibility.Collapsed;
        SendCodeButton.Visibility = Visibility.Collapsed;
        TokenBox.Clear();

        if (string.IsNullOrEmpty(UserBox.Text)) UserBox.Focus();
        else PassBox.Focus();
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

    /// <summary>
    /// Compose les identifiants présentés au serveur.
    ///
    /// Un code fraîchement saisi l'emporte sur tout : c'est lui qui permet de
    /// (re)mémoriser le poste. Sinon on présente ce qui évite la ressaisie —
    /// le jeton de connexion s'il existe, le cookie d'appareil de confiance
    /// à défaut.
    /// </summary>
    private MeshLogin BuildLogin(string password, string? token)
    {
        if (string.IsNullOrEmpty(token) && _settings.HasLoginToken)
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

    private async Task ConnectAsync(string user, string password, string? token, bool remember)
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
            var login = BuildLogin(password, token);
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

                login = BuildLogin(password, token);
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

            SubtitleText.Text = $"{_settings.Host} · {_connection.UserInfo?.Name ?? user}";
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
                    var profile = new ServerProfile
                    {
                        Host = _settings.Host,
                        Username = _sessionUser!,
                        AllowSelfSigned = _settings.AllowSelfSigned,
                    };

                    var connection = new MeshServerConnection(profile);
                    connection.StateChanged += OnStateChanged;
                    connection.DevicesChanged += OnDevicesChanged;

                    // Le second facteur mémorisé est représenté comme à la
                    // connexion initiale : sans lui, une 2FA obligatoire
                    // rendrait toute reconnexion automatique impossible.
                    await connection.ConnectAsync(BuildLogin(_sessionPassword ?? "", null));

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
                // L'ordre d'affichage ne dépend PAS de l'état en ligne (voir le
                // tri plus bas) : un passage en ligne ne recompose donc pas la
                // vue, seul le badge se met à jour. C'est ce qui évite que la
                // liste bouge sous le curseur à chaque événement du serveur.
                if (existing.NameChanged
                    || !string.Equals(existing.RealGroupName, groupName, StringComparison.Ordinal))
                {
                    structureChanged = true;
                }
                // Le filtre « en ligne uniquement » repose sur la connectivité :
                // la vue doit être recomposée, sinon une machine qui se connecte
                // n'apparaîtrait jamais dans la liste filtrée.
                if (existing.OnlineStateChanged && OnlineOnlyCheck.IsChecked == true)
                    structureChanged = true;
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

        // Le volet suit la composition du parc, pas chaque événement serveur.
        RebuildFolderPanel();
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
        Interval = TimeSpan.FromMilliseconds(150)
    };

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Le repère visuel du champ reste immédiat : seul le filtrage attend.
        var empty = string.IsNullOrEmpty(SearchBox.Text);
        SearchPlaceholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

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

    /// <summary>
    /// Restaure l'état d'un dossier au moment où son Expander apparaît.
    ///
    /// Loaded peut se déclencher plusieurs fois pour un même Expander : la
    /// virtualisation recycle les conteneurs quand on défile. On détache donc
    /// systématiquement avant de rattacher, sinon les handlers s'accumuleraient
    /// et chaque bascule serait comptée plusieurs fois — d'où des dossiers qui
    /// se refermaient ou restaient ouverts sans logique apparente.
    /// </summary>
    private void GroupExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander) return;
        var name = GroupNameOf(expander);
        if (name is null) return;

        // Détacher avant l'affectation : sinon elle déclencherait Expanded ou
        // Collapsed et réécrirait l'état qu'on est justement en train de lire.
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

    /// <summary>
    /// Tous les noms de groupes réellement affichés, sections virtuelles
    /// comprises. _allRows ne porte que les dossiers réels : les copies placées
    /// dans Favoris et Récentes vivent dans _virtualRows, et étaient donc
    /// ignorées par le bouton « tout replier ».
    /// </summary>
    private IEnumerable<string> AllDisplayedGroupNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Les sections virtuelles n'existent que si elles contiennent quelque
        // chose : les ajouter à l'aveugle mémoriserait des groupes fantômes.
        foreach (var row in _virtualRows) names.Add(row.GroupName);
        foreach (var row in _allRows) names.Add(row.GroupName);

        return names;
    }

    private void ToggleGroups_Click(object sender, RoutedEventArgs e)
    {
        _allGroupsCollapsed = !_allGroupsCollapsed;

        // N'agir que sur les dossiers réellement affichés : un filtre de
        // recherche peut en masquer, et un Clear() global oublierait leur état.
        var displayed = AllDisplayedGroupNames().ToList();
        foreach (var name in displayed)
        {
            if (_allGroupsCollapsed) _collapsedGroups.Add(name);
            else _collapsedGroups.Remove(name);
        }

        ApplyGroupStatesToVisibleExpanders();
        UpdateToggleGroupsButton();
        SaveCollapsedGroups();
    }

    /// <summary>
    /// Applique l'état mémorisé aux Expander déjà affichés. Ceux qui ne le sont
    /// pas encore (virtualisation) seront traités par GroupExpander_Loaded quand
    /// ils entreront à l'écran, à partir du même HashSet.
    /// </summary>
    private void ApplyGroupStatesToVisibleExpanders()
    {
        foreach (var expander in FindVisualChildren<Expander>(DeviceList))
        {
            var name = GroupNameOf(expander);
            if (name is null) continue;

            bool expanded = !_collapsedGroups.Contains(name);
            if (expander.IsExpanded == expanded) continue;   // rien à changer

            // Même précaution que dans Loaded : l'affectation déclencherait le
            // handler de bascule, qui réécrirait _collapsedGroups pendant qu'on
            // est en train de l'appliquer.
            expander.Expanded -= GroupExpander_Toggled;
            expander.Collapsed -= GroupExpander_Toggled;
            expander.IsExpanded = expanded;
            expander.Expanded += GroupExpander_Toggled;
            expander.Collapsed += GroupExpander_Toggled;
        }
    }

    private void UpdateToggleGroupsButton()
    {
        // Segoe MDL2 Assets : E70D chevron bas, E70E chevron haut.
        ToggleGroupsGlyph.Text = _allGroupsCollapsed ? "" : "";
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

        // Vue à deux volets : restreindre au dossier choisi. Une recherche en
        // cours reprend la main sur le dossier — sinon le champ paraîtrait ne
        // rien trouver dès que la machine cherchée se range ailleurs.
        bool folderFiltered = false;
        if (_settings.FolderView && string.IsNullOrEmpty(query)
            && _selectedFolderKey is { } folderKey && folderKey != FolderRow.AllKey)
        {
            rows = folderKey == FavoritesGroup
                ? rows.Where(r => r.IsFavorite).ToList()
                : rows.Where(r => string.Equals(r.RealGroupName, folderKey,
                                      StringComparison.OrdinalIgnoreCase)).ToList();
            folderFiltered = true;
        }

        // Sections virtuelles en tête : favoris puis machines récentes. Une même
        // machine peut apparaître deux fois (en favori et dans son dossier) ;
        // c'est voulu, comme un raccourci vers l'entrée réelle.
        _virtualRows.Clear();
        var display = new List<DeviceRow>();

        // Dans un dossier précis, ni favoris ni récentes : on a demandé ce
        // dossier-là, pas un florilège.
        if (string.IsNullOrEmpty(query) && !folderFiltered)
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

        // « Tout replier » actif : un dossier apparu depuis (nouvelle machine,
        // section Favoris qui vient de se créer) doit l'être aussi, sinon il
        // s'ouvrirait seul au milieu d'une liste repliée.
        if (_allGroupsCollapsed)
        {
            foreach (var name in display.Select(r => r.GroupName).Distinct())
                _collapsedGroups.Add(name);
        }

        // Un seul dossier affiché : vue plate, pour ne pas coiffer la liste d'un
        // en-tête de groupe unique qui répéterait le volet.
        DeviceList.ItemsSource = folderFiltered
            ? (System.Collections.IEnumerable)display
            : BuildGroupedView(display);

        var totalOnline = _allRows.Count(r => r.IsOnline);
        CountText.Text = rows.Count == _allRows.Count
            ? $"{_allRows.Count} appareils · {totalOnline} en ligne"
            : $"{rows.Count} affichés sur {_allRows.Count} · {totalOnline} en ligne";
        // Le bouton de repli n'a de sens qu'avec plusieurs dossiers affichés.
        ToggleGroupsButton.Visibility = !folderFiltered
            && rows.Select(r => r.GroupName).Distinct().Skip(1).Any()
            ? Visibility.Visible : Visibility.Collapsed;

        // Déduire le sens du bouton de l'état réel plutôt que de le mémoriser :
        // au lancement, les dossiers repliés sont restaurés depuis les réglages
        // et le bouton aurait sinon proposé « replier » alors que tout l'est.
        var shown = display.Select(r => r.GroupName).Distinct().ToList();
        _allGroupsCollapsed = shown.Count > 0 && shown.All(_collapsedGroups.Contains);
        UpdateToggleGroupsButton();
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
            _tray?.ShowMessage("Mesh Control", message);
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
