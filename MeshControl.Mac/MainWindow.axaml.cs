using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MeshControl.Core;
using MeshControl.UI;

namespace MeshControl.Mac;

/// <summary>
/// Fenêtre principale — portage de UI/MainWindow.xaml.cs (version Windows) :
/// choix du serveur, connexion, liste des appareils (volet des dossiers,
/// pastilles de filtre, tri, favoris, récentes), volet des réglages.
/// </summary>
public partial class MainWindow : ChromeWindow
{
    private static MacSettings Settings => App.Settings;

    private MeshServerConnection? _connection;

    /// <summary>Mot de passe de la session en cours, pour les reconnexions.</summary>
    private string? _password;

    private readonly List<DesktopWindow> _desktops = new();
    private readonly List<TerminalWindow> _terminals = new();
    private readonly List<FilesWindow> _filesWindows = new();

    /// <summary>Ferme toutes les sessions ouvertes (bureau, terminal, fichiers) : elles reposent sur la liaison.</summary>
    private void CloseSessions()
    {
        foreach (var window in _desktops.ToList()) window.Close();
        foreach (var window in _terminals.ToList()) window.Close();
        foreach (var window in _filesWindows.ToList()) window.Close();
    }

    /// <summary>Touche de commande : Cmd sur Mac, Ctrl ailleurs.</summary>
    private static readonly KeyModifiers CommandKey =
        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public MainWindow()
    {
        InitializeComponent();

        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyFilter();
        };
        _toastTimer.Tick += (_, _) => HideToast();

        SearchShortcutText.Text = OperatingSystem.IsMacOS() ? "⌘ F" : "Ctrl F";
        BuildSkeleton();
        BuildFilterChips();
        UpdateSortButton();
        ApplyFolderView();
        SetHeader(null, null, HeaderStatus.Idle);

        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => Initialize();
    }

    // MARK: - Démarrage et vues

    private bool _initialized;

    /// <summary>Une seule fois : à l'ouverture de la fenêtre, ou au démarrage réduit.</summary>
    private void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        SetupHotkey();
        SetupTray();
        Start();

        // Volontairement non attendu : une recherche lente ou en échec ne doit
        // jamais bloquer le démarrage.
        if (Settings.CheckForUpdates) _ = CheckForUpdatesAsync(manual: false);
    }

    private void Start()
    {
        if (string.IsNullOrWhiteSpace(Settings.Host))
        {
            ShowServer(firstRun: true);
            return;
        }

        var saved = Settings.RememberPassword
            ? Keychain.Read(PasswordAccount(Settings.Host, Settings.Username))
            : null;
        if (!string.IsNullOrEmpty(saved) && !string.IsNullOrEmpty(Settings.Username))
        {
            // Identifiants mémorisés : connexion directe, comme sous Windows.
            _ = ConnectAsync(Settings.Username, saved, token: null, automatic: true);
        }
        else
        {
            ShowLogin();
        }
    }

    private void ShowOnly(Control view)
    {
        foreach (var panel in new Control[] { ServerPanel, LoginPanel, DevicesPanel, LoadingPanel })
            panel.IsVisible = panel == view;
        RefreshButton.IsVisible = view == DevicesPanel;
    }

    private void ShowServer(bool firstRun)
    {
        ServerTitle.Text = firstRun ? "Bienvenue" : "Changer de serveur";
        ServerHostBox.Text = Settings.Host;
        ServerSelfSignedCheck.IsChecked = Settings.AllowSelfSigned;
        ServerStatusText.Text = "";
        ServerContinueButton.IsEnabled = true;
        ShowOnly(ServerPanel);
        SetHeader(null, null, HeaderStatus.Idle);
        ServerHostBox.Focus();
    }

    private void ShowLogin()
    {
        LoginServerLabel.Text = Settings.Host;
        UserBox.Text = Settings.Username;
        RememberCheck.IsChecked = Settings.RememberPassword;
        ConnectButton.IsEnabled = true;
        ConnectButton.Content = "Se connecter";
        SendCodeButton.IsVisible = false;
        ShowOnly(LoginPanel);
        SetHeader(Settings.Host, null, HeaderStatus.Idle);
        if (string.IsNullOrEmpty(UserBox.Text)) UserBox.Focus();
        else PassBox.Focus();
    }

    private void ShowLoading(string detail)
    {
        LoadingDetail.Text = detail;
        ShowOnly(LoadingPanel);
    }

    private void ShowDevices()
    {
        ShowOnly(DevicesPanel);
        RebuildDeviceList();
        SearchBox.Focus();
    }

    /// <summary>Lignes grises du chargement : on devine déjà la forme de la liste.</summary>
    private void BuildSkeleton()
    {
        foreach (var (first, second) in new[] { (260, 180), (220, 150), (280, 200), (200, 130), (240, 170), (210, 160) })
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 16) };
            grid.Children.Add(new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(9),
                Background = Palette.Resource("SubtleFillHoverBrush"),
            });
            var lines = new StackPanel { Margin = new Thickness(12, 2, 0, 0) };
            lines.Children.Add(new Border
            {
                Width = first, Height = 10, CornerRadius = new CornerRadius(5),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Palette.Resource("SubtleFillHoverBrush"),
            });
            lines.Children.Add(new Border
            {
                Width = second, Height = 8, CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 7, 0, 0),
                Background = Palette.Resource("SubtleFillBrush"),
            });
            Grid.SetColumn(lines, 1);
            grid.Children.Add(lines);
            SkeletonRows.Children.Add(grid);
        }
    }

    // MARK: - En-tête

    private enum HeaderStatus { Unchanged, Idle, Connected, Warning }

    /// <summary>Le serveur en gras, puis le compte ou l'état en retrait, et le point d'état.</summary>
    private void SetHeader(string? host, string? detail, HeaderStatus status = HeaderStatus.Unchanged)
    {
        SubtitleText.Inlines = new InlineCollection
        {
            new Run(string.IsNullOrEmpty(host) ? "Contrôle à distance" : host) { FontWeight = FontWeight.SemiBold },
            new Run(string.IsNullOrEmpty(detail) ? "" : $"  ·  {detail}")
            {
                FontSize = 15,
                Foreground = Palette.Resource("TextSecondaryBrush"),
            },
        };

        if (status == HeaderStatus.Unchanged) return;
        ConnectionDot.IsVisible = status != HeaderStatus.Idle;
        ConnectionDot.Fill = Palette.Resource(status == HeaderStatus.Connected ? "OnlineBrush" : "WarningTextBrush");
        ToolTip.SetTip(ConnectionDot, status == HeaderStatus.Connected ? "Connecté au serveur" : detail);
    }

    // MARK: - Choix du serveur

    private void ServerHostBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ServerContinue_Click(sender, e);
    }

    private async void ServerContinue_Click(object? sender, RoutedEventArgs e)
    {
        var host = (ServerHostBox.Text ?? "").Trim();
        if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) host = host[8..];
        host = host.TrimEnd('/');
        if (host.Length == 0)
        {
            ServerStatusText.Text = "Indiquez l'adresse du serveur.";
            return;
        }

        ServerContinueButton.IsEnabled = false;
        ServerStatusText.Text = "Vérification du serveur…";
        var profile = new ServerProfile { Host = host, AllowSelfSigned = ServerSelfSignedCheck.IsChecked == true };
        var error = await MeshServerConnection.ProbeAsync(profile);
        ServerContinueButton.IsEnabled = true;
        if (error is not null)
        {
            ServerStatusText.Text = error;
            return;
        }

        if (!string.Equals(Settings.Host, host, StringComparison.OrdinalIgnoreCase))
            CertificateTrust.Forget(Settings.Host);
        Settings.Host = host;
        Settings.AllowSelfSigned = profile.AllowSelfSigned;
        Settings.Save();
        ShowLogin();
    }

    private void ChangeServer_Click(object? sender, RoutedEventArgs e) => ShowServer(firstRun: false);

    // MARK: - Connexion

    private static string PasswordAccount(string host, string user) => $"{user}@{host}";
    private static string CookieAccount(string host, string user) => $"{user}@{host}#2fa";

    private string? _sendCodeToken;

    private void PassBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ConnectButton_Click(sender, e);
    }

    private void ConnectButton_Click(object? sender, RoutedEventArgs e)
    {
        var user = (UserBox.Text ?? "").Trim();
        var password = PassBox.Text ?? "";
        var token = TwoFactorPanel.IsVisible ? (TokenBox.Text ?? "").Trim() : null;
        if (user.Length == 0 || password.Length == 0)
        {
            StatusText.Text = "Renseignez l'utilisateur et le mot de passe.";
            return;
        }
        _ = ConnectAsync(user, password, string.IsNullOrEmpty(token) ? null : token, automatic: false);
    }

    private void SendCode_Click(object? sender, RoutedEventArgs e)
    {
        if (_sendCodeToken is null) return;
        var user = (UserBox.Text ?? "").Trim();
        _ = ConnectAsync(user, PassBox.Text ?? "", _sendCodeToken, automatic: false);
    }

    private async Task ConnectAsync(string user, string password, string? token, bool automatic)
    {
        var host = Settings.Host;
        StatusText.Text = "";
        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "Connexion…";
        if (automatic) ShowLoading(host);

        var profile = new ServerProfile { Host = host, Username = user, AllowSelfSigned = Settings.AllowSelfSigned };
        DisposeConnection();
        var connection = new MeshServerConnection(profile);
        connection.StateChanged += () => Dispatcher.UIThread.Post(() => OnConnectionStateChanged(connection));
        connection.DevicesChanged += ScheduleListRefresh;
        _connection = connection;

        try
        {
            await connection.ConnectAsync(new MeshLogin
            {
                Password = password,
                TwoFactorToken = token,
                TwoFactorCookie = Keychain.Read(CookieAccount(host, user)),
            });
        }
        catch (MeshTwoFactorRequiredException request)
        {
            if (_connection == connection) _connection = null;
            ShowTwoFactor(request, codeRequested: token is "**email**" or "**sms**" or "**msg**");
            return;
        }
        catch (Exception ex)
        {
            if (_connection == connection) _connection = null;
            ShowLogin();
            StatusText.Text = connection.LastError ?? ex.Message;
            return;
        }

        _password = password;
        bool remember = automatic || RememberCheck.IsChecked == true;
        Settings.Username = user;
        Settings.RememberPassword = remember;
        Settings.Save();
        if (remember) Keychain.Write(PasswordAccount(host, user), password);
        else Keychain.Delete(PasswordAccount(host, user));

        // Code saisi : on demande un cookie « appareil de confiance ».
        if (!string.IsNullOrEmpty(token) && !token.StartsWith("**") && remember && TrustDeviceCheck.IsChecked == true)
        {
            try
            {
                var cookie = await connection.RequestTrustedDeviceCookieAsync();
                if (!string.IsNullOrEmpty(cookie)) Keychain.Write(CookieAccount(host, user), cookie);
            }
            catch (Exception ex)
            {
                Log.Warning($"Cookie d'appareil de confiance refusé : {ex.Message}", "Serveur");
            }
        }

        TwoFactorPanel.IsVisible = false;
        PassBox.Text = "";
        TokenBox.Text = "";
        DeviceRow.CurrentUserName = connection.UserInfo?.Name ?? user;
        SetHeader(host, connection.UserInfo?.Name ?? user, HeaderStatus.Connected);
        ShowDevices();
    }

    private void ShowTwoFactor(MeshTwoFactorRequiredException request, bool codeRequested)
    {
        ShowLogin();
        TwoFactorPanel.IsVisible = true;

        var days = request.TrustedDeviceDays;
        TrustDeviceCheck.IsVisible = days > 0;
        TrustDeviceHint.IsVisible = days > 0;
        TrustDeviceHint.Text = days == 1
            ? "Le serveur autorise ce poste pour une journée."
            : $"Le serveur autorise ce poste pour {days} jours.";

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
            SendCodeButton.IsVisible = true;
        }
        StatusText.Text = codeRequested ? "Code envoyé." : "Le serveur demande un code à deux facteurs.";
        TokenBox.Text = "";
        TokenBox.Focus();
    }

    // MARK: - Liaison avec le serveur

    private int _reconnectAttempt;

    private void OnConnectionStateChanged(MeshServerConnection connection)
    {
        if (connection != _connection || !DevicesPanel.IsVisible) return;
        switch (connection.State)
        {
            case ConnectionState.Connected:
                SetHeader(connection.Profile.Host, connection.UserInfo?.Name, HeaderStatus.Connected);
                UpdateTrayTooltip();
                break;
            case ConnectionState.Failed:
            case ConnectionState.Disconnected:
                _ = ReconnectAsync(connection);
                break;
        }
    }

    /// <summary>
    /// Liaison perdue : nouvelle connexion avec les mêmes identifiants, à
    /// intervalle croissant. La liste reste affichée en attendant.
    /// </summary>
    private async Task ReconnectAsync(MeshServerConnection lost)
    {
        if (_password is null) { ShowLogin(); return; }
        var host = lost.Profile.Host;
        var user = lost.Profile.Username;

        while (_connection == lost)
        {
            _reconnectAttempt++;
            SetHeader(host, "reconnexion…", HeaderStatus.Warning);
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * _reconnectAttempt)));
            if (_connection != lost) return;

            var connection = new MeshServerConnection(new ServerProfile
            {
                Host = host, Username = user, AllowSelfSigned = Settings.AllowSelfSigned,
            });
            try
            {
                await connection.ConnectAsync(new MeshLogin
                {
                    Password = _password,
                    TwoFactorCookie = Keychain.Read(CookieAccount(host, user)),
                });
            }
            catch (MeshTwoFactorRequiredException)
            {
                connection.Dispose();
                DisposeConnection();
                ShowLogin();
                StatusText.Text = "Le serveur demande à nouveau le code à deux facteurs.";
                return;
            }
            catch
            {
                connection.Dispose();
                continue;
            }

            if (_connection != lost) { connection.Dispose(); return; }

            // Les sessions ouvertes reposaient sur l'ancienne liaison.
            CloseSessions();
            lost.DevicesChanged -= ScheduleListRefresh;
            lost.Dispose();
            connection.StateChanged += () => Dispatcher.UIThread.Post(() => OnConnectionStateChanged(connection));
            connection.DevicesChanged += ScheduleListRefresh;
            _connection = connection;
            _reconnectAttempt = 0;
            _rowsById.Clear();
            SetHeader(host, connection.UserInfo?.Name ?? user, HeaderStatus.Connected);
            RebuildDeviceList();
        }
    }

    private void DisposeConnection()
    {
        var connection = _connection;
        _connection = null;
        if (connection is null) return;
        CloseSessions();
        connection.DevicesChanged -= ScheduleListRefresh;
        try { connection.Disconnect(); } catch { }
        connection.Dispose();
        _rowsById.Clear();
        _allRows = new();
        DeviceList.ItemsSource = null;
        _lastPlan.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeConnection();
        base.OnClosed(e);
    }

    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_connection is null) return;
        try
        {
            await _connection.RefreshDeviceListAsync();
            ShowToast("Liste des machines actualisée.");
        }
        catch (Exception ex) { ShowToast($"Actualisation impossible : {ex.Message}"); }
    }

    // MARK: - Liste des appareils : construction

    private readonly Dictionary<string, DeviceRow> _rowsById = new();
    private List<DeviceRow> _allRows = new();
    private int _listRefreshScheduled;

    /// <summary>Un seul rafraîchissement programmé à la fois (rafale d'événements).</summary>
    private void ScheduleListRefresh()
    {
        if (Interlocked.Exchange(ref _listRefreshScheduled, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _listRefreshScheduled, 0);
            RebuildDeviceList();
        }, DispatcherPriority.Background);
    }

    private void RebuildDeviceList()
    {
        try
        {
            RebuildDeviceListCore();
        }
        finally
        {
            CheckWatchedNodes();
            UpdateTrayTooltip();
        }
    }

    private void RebuildDeviceListCore()
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

            if (_rowsById.TryGetValue(node.Id, out var existing) && ReferenceEquals(existing.Node, node))
            {
                if (existing.NameChanged
                    || !string.Equals(existing.RealGroupName, groupName, StringComparison.Ordinal))
                    structureChanged = true;
                if (existing.OnlineStateChanged && _activeFilters.ContainsKey(FilterGroup.State))
                    structureChanged = true;
                if (existing.SessionsChanged && IsFilterActive(DeviceFilter.InSession))
                    structureChanged = true;
                if (existing.OnlineStateChanged && SortFollowsConnectivity)
                    structureChanged = true;
                existing.RefreshFromNode();
            }
            else
            {
                _rowsById[node.Id] = new DeviceRow(node, groupName, Settings.IsFavorite(node.Id));
                structureChanged = true;
            }
        }

        foreach (var goneId in _rowsById.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _rowsById.Remove(goneId);
            structureChanged = true;
        }

        foreach (var copy in _virtualRows) copy.RefreshFromNode();

        if (!structureChanged && _allRows.Count > 0)
        {
            UpdateCounters();
            return;
        }

        _allRows = _rowsById.Values
            .OrderBy(r => r.RealGroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        RebuildFolderPanel();
        ApplyFilter();
    }

    /// <summary>Met à jour les compteurs sans toucher à la liste.</summary>
    private void UpdateCounters()
    {
        var displayed = _shownRowCount;
        var totalOnline = _allRows.Count(r => r.IsOnline);
        CountText.Text = displayed >= _allRows.Count
            ? $"{_allRows.Count} appareils · {totalOnline} en ligne"
            : $"{displayed} affichés sur {_allRows.Count} · {totalOnline} en ligne";
        UpdateFilterChips();
        RefreshFolderCounts();
        UpdateSelectionBar();
    }

    // MARK: - Volet des dossiers

    private List<FolderRow> _folderRows = new();
    private string? _selectedFolderKey;
    private bool _restoringFolder;

    private void ApplyFolderView()
    {
        bool on = Settings.FolderView;
        FolderPane.IsVisible = on;
        FolderPane.Width = Math.Clamp(Settings.FolderPaneWidth, MinFolderPaneWidth, MaxFolderPaneWidth);
        ToolTip.SetTip(ToggleViewButton, on ? "Masquer la barre latérale (liste en colonnes)" : "Afficher la barre latérale");
        ToggleViewGlyph.Foreground = Palette.Resource(on ? "AccentBrush" : "TextSecondaryBrush");

        SidebarTitleRow.IsVisible = on;
        ColumnHeader.IsVisible = !on;
        if (CountText.Parent is Panel current) current.Children.Remove(CountText);
        if (on) TitleCountHost.Children.Add(CountText);
        else ToolbarRight.Children.Insert(0, CountText);

        DeviceList.ItemTemplate = new DeviceTemplateSelector
        {
            Header = (IDataTemplate)DeviceList.Resources["GroupHeaderTemplate"]!,
            Card = (IDataTemplate)DeviceList.Resources["DeviceCardTemplate"]!,
            Columns = (IDataTemplate)DeviceList.Resources["DeviceColumnsTemplate"]!,
            UseColumns = !on,
        };
        ApplyColumnsPadding();

        if (on && _activeFilters.Remove(FilterGroup.Set)) SyncFilterChips();
        UpdateFilterChips();
    }

    /// <summary>Lignes en colonnes : plus basses que les cartes, d'après la densité.</summary>
    private void ApplyColumnsPadding()
    {
        if (Settings.FolderView)
        {
            DeviceList.Resources.Remove("DeviceRowPadding");
            return;
        }
        var vertical = Settings.ListDensity switch { 0 => 2.0, 2 => 7.0, _ => 4.0 };
        DeviceList.Resources["DeviceRowPadding"] = new Thickness(10, vertical, 10, vertical);
    }

    private void UpdateViewTitle(string query)
    {
        if (!Settings.FolderView) return;
        ViewTitleText.Text = !string.IsNullOrEmpty(query)
            ? $"Recherche « {query} »"
            : _folderRows.FirstOrDefault(f => f.Key == (_selectedFolderKey ?? FolderRow.AllKey)) is { } folder
                ? folder.Name
                : "Toutes les machines";
    }

    private void RebuildFolderPanel()
    {
        if (!Settings.FolderView) return;

        var rows = new List<FolderRow>
        {
            FolderRow.Section("Vues"),
            new(FolderRow.AllKey, "Toutes les machines", IconKind.List, "#8A8A8E", 0, 0),
        };
        if (_allRows.Any(r => r.IsFavorite))
            rows.Add(new FolderRow(FolderRow.FavoritesKey, "Favoris", IconKind.Star, "#FFB300", 0, 0));
        if (Settings.RecentNodeIds.Count > 0)
            rows.Add(new FolderRow(FolderRow.RecentKey, "Récentes", IconKind.History, "#8A8A8E", 0, 0));
        rows.Add(new FolderRow(FolderRow.SessionKey, "En session", IconKind.Person, "#0A84FF", 0, 0));
        rows.Add(FolderRow.Section("Dossiers"));

        var folders = _allRows
            .Select(r => r.RealGroupName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => !IsHiddenFolder(name))
            .OrderBy(name => IsPinnedFolder(name) ? 0 : 1)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase);
        foreach (var name in folders)
        {
            rows.Add(IsPinnedFolder(name)
                ? new FolderRow(name, name, IconKind.Pin, "#0A84FF", 0, 0, isPinned: true)
                : new FolderRow(name, name, IconKind.Folder, "#8A8A8E", 0, 0));
        }

        var hidden = HiddenFolderNames().Count;
        if (hidden > 0)
            rows.Add(new FolderRow(FolderRow.HiddenKey,
                hidden == 1 ? "1 dossier masqué" : $"{hidden} dossiers masqués", IconKind.Hidden, "#8A8A8E", 0, 0));

        _folderRows = rows;
        RefreshFolderCounts();
        var wanted = _selectedFolderKey ?? Settings.SelectedFolder ?? FolderRow.AllKey;
        _selectedFolderKey = rows.Any(f => f.Key == wanted) ? wanted : FolderRow.AllKey;
        ShowFolderRows();
    }

    private void ShowFolderRows()
    {
        var query = (FolderFilterBox.Text ?? "").Trim();
        var visible = query.Length == 0
            ? _folderRows
            : _folderRows.Where(f => !f.IsSection && (!f.IsRealFolder || TextMatch.Contains(f.Name, query))).ToList();

        _restoringFolder = true;
        FolderList.ItemsSource = visible;
        FolderList.SelectedItem = visible.FirstOrDefault(f => f.Key == _selectedFolderKey);
        _restoringFolder = false;

        // Intitulés de section : ni survol, ni clic.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in FolderList.GetRealizedContainers())
                item.Classes.Set("section", item.DataContext is FolderRow { IsSection: true });
        }, DispatcherPriority.Loaded);
    }

    private bool IsPinnedFolder(string name) => Settings.PinnedFolders.Contains(name, StringComparer.OrdinalIgnoreCase);
    private bool IsHiddenFolder(string name) => Settings.HiddenFolders.Contains(name, StringComparer.OrdinalIgnoreCase);
    private bool IsRecent(string nodeId) => Settings.RecentNodeIds.Contains(nodeId);

    private List<string> HiddenFolderNames() => _allRows
        .Select(r => r.RealGroupName)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(IsHiddenFolder)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private IEnumerable<DeviceRow> RowsOfFolder(string key) => key switch
    {
        FolderRow.AllKey => _allRows.Where(r => !IsHiddenFolder(r.RealGroupName)),
        FolderRow.HiddenKey => _allRows.Where(r => IsHiddenFolder(r.RealGroupName)),
        FolderRow.FavoritesKey => _allRows.Where(r => r.IsFavorite),
        FolderRow.RecentKey => _allRows.Where(r => IsRecent(r.Node.Id)),
        FolderRow.SessionKey => _allRows.Where(r => r.IsOnline && r.Node.Sessions.Count > 0),
        _ => _allRows.Where(r => string.Equals(r.RealGroupName, key, StringComparison.OrdinalIgnoreCase)),
    };

    /// <summary>
    /// Compteurs du volet : machines en ligne sans filtre ; avec des pastilles
    /// actives, celles qui y répondent.
    /// </summary>
    private void RefreshFolderCounts()
    {
        if (!Settings.FolderView) return;
        var filtered = AnyFilterActive;
        Func<DeviceRow, bool> matches = filtered ? r => MatchesActiveFilters(r) : r => r.IsOnline;

        var byFolder = _allRows
            .GroupBy(r => r.RealGroupName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (Total: g.Count(), Matching: g.Count(matches)),
                          StringComparer.OrdinalIgnoreCase);

        foreach (var folder in _folderRows)
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

    private void FolderList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_restoringFolder || FolderList.SelectedItem is not FolderRow folder) return;
        if (folder.IsSection)
        {
            ShowFolderRows();
            return;
        }
        _selectedFolderKey = folder.Key;
        Settings.SelectedFolder = folder.Key;
        Settings.Save();
        ApplyFilter();
    }

    private void FolderFilterBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var empty = string.IsNullOrEmpty(FolderFilterBox.Text);
        FolderFilterPlaceholder.IsVisible = empty;
        ClearFolderFilterButton.IsVisible = !empty;
        ShowFolderRows();
    }

    private void FolderFilterBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(FolderFilterBox.Text))
        {
            FolderFilterBox.Text = "";
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Down)
        {
            var first = FolderList.Items.OfType<FolderRow>().FirstOrDefault(f => f.IsRealFolder);
            if (first is not null) FolderList.SelectedItem = first;
            e.Handled = true;
        }
    }

    private void ClearFolderFilter_Click(object? sender, RoutedEventArgs e)
    {
        FolderFilterBox.Text = "";
        FolderFilterBox.Focus();
    }

    private const double MinFolderPaneWidth = 180;
    private const double MaxFolderPaneWidth = 520;

    private void FolderPaneResizer_DragDelta(object? sender, VectorEventArgs e)
        => FolderPane.Width = Math.Clamp(FolderPane.Width + e.Vector.X, MinFolderPaneWidth, MaxFolderPaneWidth);

    private void FolderPaneResizer_DragCompleted(object? sender, VectorEventArgs e)
    {
        Settings.FolderPaneWidth = FolderPane.Width;
        Settings.Save();
    }

    private void ToggleView_Click(object? sender, RoutedEventArgs e)
    {
        Settings.FolderView = !Settings.FolderView;
        Settings.Save();
        ApplyFolderView();
        RebuildFolderPanel();
        ApplyFilter(force: true);
    }

    // MARK: - Pastilles de filtre

    private enum DeviceFilter { All, Online, Offline, InSession, Favorites, Recent, Windows, Linux, MacOS }
    private enum FilterGroup { None, State, Set, System }

    private readonly Dictionary<FilterGroup, DeviceFilter> _activeFilters = new();
    private bool AnyFilterActive => _activeFilters.Count > 0;
    private List<DeviceRow> _chipScope = new();
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

    private static FilterGroup GroupOf(DeviceFilter filter) => FilterDefinitions.First(f => f.Filter == filter).Group;

    private bool IsFilterActive(DeviceFilter filter) => filter == DeviceFilter.All
        ? !AnyFilterActive
        : _activeFilters.TryGetValue(GroupOf(filter), out var active) && active == filter;

    private void BuildFilterChips()
    {
        var previousGroup = FilterGroup.None;
        foreach (var (filter, group, label, tooltip) in FilterDefinitions)
        {
            var count = new TextBlock { Margin = new Thickness(6, 0, 0, 0) };
            count.Classes.Add("chipCount");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = label });
            content.Children.Add(count);

            var chip = new ToggleButton { Content = content };
            chip.Classes.Add("chip");
            ToolTip.SetTip(chip, tooltip);
            // Écart plus large entre deux catégories.
            if (group != previousGroup && previousGroup != FilterGroup.None)
                chip.Margin = new Thickness(10, 0, 6, 6);
            else
                chip.Margin = new Thickness(0, 0, 6, 6);
            previousGroup = group;

            chip.Click += (_, _) => ToggleFilter(filter);
            FilterChipsPanel.Children.Add(chip);
            _filterChips[filter] = (chip, count);
        }
        SyncFilterChips();
        UpdateFilterChips();
    }

    private void ToggleFilter(DeviceFilter filter)
    {
        if (filter == DeviceFilter.All) _activeFilters.Clear();
        else if (IsFilterActive(filter)) _activeFilters.Remove(GroupOf(filter));
        else _activeFilters[GroupOf(filter)] = filter;
        SyncFilterChips();
        ApplyFilter();
    }

    private void SyncFilterChips()
    {
        foreach (var (filter, (chip, _)) in _filterChips)
            chip.IsChecked = IsFilterActive(filter);
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

    private bool MatchesActiveFilters(DeviceRow row, FilterGroup except = FilterGroup.None)
    {
        foreach (var (group, filter) in _activeFilters)
            if (group != except && !MatchesFilter(row, filter)) return false;
        return true;
    }

    /// <summary>
    /// Compteur de chaque pastille : ce qu'elle donnerait, compte tenu des
    /// autres catégories. Une pastille sans aucune machine disparaît.
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
            if (Settings.FolderView && filter != DeviceFilter.All && GroupOf(filter) == FilterGroup.Set)
                exists = active = false;
            chip.IsVisible = exists || active;
            // Pastille réduite à zéro par les autres filtres : en retrait, par
            // la couleur (une opacité rendrait le texte flou).
            chip.Classes.Set("empty", n == 0 && !active && filter != DeviceFilter.All);
        }
        FilterChipsPanel.IsVisible = _allRows.Count > 0;
    }

    private void ClearFilters_Click(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        _searchTimer.Stop();
        _activeFilters.Clear();
        SyncFilterChips();
        ApplyFilter();
        SearchBox.Focus();
    }

    // MARK: - Tri

    private static readonly string[] SortLabels = ["Nom", "En ligne d'abord", "Dernière présence", "Système"];

    private bool SortFollowsConnectivity => Settings.DeviceSort is 1 or 2;

    private IEnumerable<DeviceRow> SortRows(IEnumerable<DeviceRow> rows, bool byGroup)
    {
        var ordered = byGroup
            ? rows.OrderBy(r => r.RealGroupName, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(_ => 0);

        ordered = Settings.DeviceSort switch
        {
            1 => ordered.ThenByDescending(r => r.IsOnline),
            2 => ordered.ThenByDescending(r => r.IsOnline)
                        .ThenByDescending(r => r.Node.LastConnect ?? DateTime.MinValue),
            3 => ordered.ThenBy(r => r.Node.OsFamily)
                        .ThenBy(r => r.Node.OsDescription is null)
                        .ThenBy(r => r.Node.OsDescription ?? "", StringComparer.OrdinalIgnoreCase),
            _ => ordered,
        };
        return ordered.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }

    private void SortChoice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out var mode)) return;
        SetSort(mode);
    }

    private void ColumnHeader_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control { Tag: string tag } && int.TryParse(tag, out var mode)) SetSort(mode);
    }

    private void SetSort(int mode)
    {
        Settings.DeviceSort = mode;
        Settings.Save();
        UpdateSortButton();
        ApplyFilter();
    }

    private void UpdateSortButton()
    {
        var mode = Math.Clamp(Settings.DeviceSort, 0, SortLabels.Length - 1);
        SortLabel.Text = SortLabels[mode];
        SortArrowName.IsVisible = mode == 0;
        SortArrowSystem.IsVisible = mode == 3;
        SortArrowSeen.IsVisible = mode is 1 or 2;
        foreach (var item in ((MenuFlyout)SortButton.Flyout!).Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is string tag && tag == mode.ToString();
    }

    // MARK: - Recherche

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    private void SearchFrame_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (SearchBox.IsFocused) return;
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var empty = string.IsNullOrEmpty(SearchBox.Text);
        SearchPlaceholder.IsVisible = empty;
        ClearSearchButton.IsVisible = !empty;
        SearchShortcutHint.IsVisible = empty;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
        {
            e.Handled = true;
            ClearSearch_Click(sender, e);
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            FocusFirstRow();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _searchTimer.Stop();
            ApplyFilter();
            if (DeviceList.ItemsSource is IEnumerable items && items.OfType<DeviceRow>().FirstOrDefault() is { } first)
                OpenDesktop(first);
        }
    }

    private void ClearSearch_Click(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
        _searchTimer.Stop();
        ApplyFilter();
    }

    private void FocusFirstRow()
    {
        if (DeviceList.ItemsSource is not IEnumerable items || items.OfType<DeviceRow>().FirstOrDefault() is not { } first)
            return;
        DeviceList.SelectedItem = first;
        DeviceList.ScrollIntoView(first);
        DeviceList.ContainerFromItem(first)?.Focus();
    }

    // MARK: - Filtrage et composition de la liste

    private const string RecentGroup = "🕘  Récentes";

    private readonly List<DeviceRow> _virtualRows = new();
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);
    private bool _allGroupsCollapsed;
    private readonly Dictionary<string, GroupHeaderRow> _headers = new(StringComparer.OrdinalIgnoreCase);
    private List<(DeviceRow? Source, string Group)> _lastPlan = new();
    private List<object> _lastDisplay = new();
    private string _lastQuery = "";
    private int _shownRowCount;

    private DeviceRow Clone(DeviceRow row, string groupName)
    {
        var copy = new DeviceRow(row.Node, row.RealGroupName, row.IsFavorite) { GroupName = groupName };
        _virtualRows.Add(copy);
        return copy;
    }

    private GroupHeaderRow HeaderFor(string name, int count)
    {
        if (!_headers.TryGetValue(name, out var header)) _headers[name] = header = new GroupHeaderRow(name);
        header.Count = count;
        header.IsCollapsed = _collapsedGroups.Contains(name);
        return header;
    }

    private void ApplyFilter(bool force = false)
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var selectedIds = DeviceList.SelectedItems?.OfType<DeviceRow>().Select(r => r.Node.Id).ToHashSet() ?? new();

        var folderKey = Settings.FolderView && string.IsNullOrEmpty(query)
                        && _selectedFolderKey is { } key && key != FolderRow.AllKey
            ? key : null;
        bool folderFiltered = folderKey is not null;

        IEnumerable<DeviceRow> source = _allRows;
        if (!string.IsNullOrEmpty(query))
            source = source.Where(r =>
                TextMatch.Contains(r.Name, query)
                || TextMatch.Contains(r.GroupName, query)
                || TextMatch.Contains(r.Node.OsDescription, query)
                || TextMatch.Contains(r.Node.Ip, query)
                || (r.IsOnline && r.Node.Sessions.Any(s => TextMatch.Contains(s.UserName, query))));
        else if (folderKey != FolderRow.HiddenKey)
            source = source.Where(r => !IsHiddenFolder(r.RealGroupName));

        if (folderKey is not null)
            source = source.Where(r => folderKey switch
            {
                FolderRow.HiddenKey => IsHiddenFolder(r.RealGroupName),
                FolderRow.FavoritesKey => r.IsFavorite,
                FolderRow.RecentKey => IsRecent(r.Node.Id),
                FolderRow.SessionKey => r.IsOnline && r.Node.Sessions.Count > 0,
                _ => string.Equals(r.RealGroupName, folderKey, StringComparison.OrdinalIgnoreCase),
            });

        _chipScope = source.ToList();
        if (AnyFilterActive) source = _chipScope.Where(r => MatchesActiveFilters(r));
        var set = _activeFilters.GetValueOrDefault(FilterGroup.Set, DeviceFilter.All);

        var rows = SortRows(source, byGroup: true).ToList();

        bool flat = (folderFiltered && folderKey != FolderRow.HiddenKey)
                    || set is DeviceFilter.Favorites or DeviceFilter.Recent;
        if (set == DeviceFilter.Recent || folderKey == FolderRow.RecentKey)
        {
            var recent = Settings.RecentNodeIds.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
            rows = rows.OrderBy(r => recent.GetValueOrDefault(r.Node.Id, int.MaxValue)).ToList();
        }
        else if (set == DeviceFilter.Favorites || folderKey == FolderRow.FavoritesKey)
        {
            rows = SortRows(rows, byGroup: false).ToList();
        }

        // Sections virtuelles (favoris, récentes) en tête, en liste groupée
        // sans barre latérale ni filtre.
        var entries = new List<(DeviceRow Source, string Group)>(rows.Count + 16);
        if (string.IsNullOrEmpty(query) && !folderFiltered && !AnyFilterActive && !Settings.FolderView)
        {
            foreach (var favorite in SortRows(rows.Where(r => r.IsFavorite), byGroup: false))
                entries.Add((favorite, FolderRow.FavoritesKey));
            var byId = rows.ToDictionary(r => r.Node.Id, r => r);
            foreach (var id in Settings.RecentNodeIds)
                if (byId.TryGetValue(id, out var row) && !row.IsFavorite)
                    entries.Add((row, RecentGroup));
        }
        foreach (var row in rows) entries.Add((row, row.GroupName));

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
            if (_allGroupsCollapsed)
                foreach (var name in groupCounts.Keys.Where(n => !_headers.ContainsKey(n)))
                    _collapsedGroups.Add(name);

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

        // Même résultat qu'à l'écran : on garde la liste, défilement compris.
        var unchanged = !force && DeviceList.ItemsSource is not null && plan.Count == _lastPlan.Count
                        && query == _lastQuery;
        for (var i = 0; unchanged && i < plan.Count; i++)
            unchanged = ReferenceEquals(plan[i].Source, _lastPlan[i].Source) && plan[i].Group == _lastPlan[i].Group;

        foreach (var (name, count) in groupCounts) HeaderFor(name, count);

        if (!unchanged)
        {
            _virtualRows.Clear();
            var display = new List<object>(plan.Count);
            foreach (var (row, group) in plan)
            {
                display.Add(row is null ? HeaderFor(group, groupCounts[group])
                          : group == row.GroupName ? row
                          : Clone(row, group));
            }
            Highlight.SetQuery(DeviceList, query);
            DeviceList.ItemsSource = display;
            _lastDisplay = display;
            _lastPlan = plan;
            _lastQuery = query;

            // Sélection rétablie par identifiant.
            foreach (var item in display.OfType<DeviceRow>().Where(r => selectedIds.Contains(r.Node.Id)))
                DeviceList.SelectedItems?.Add(item);
        }

        _shownRowCount = plan.Count(p => p.Source is not null) + (flat ? 0 : groupCounts
            .Where(g => _collapsedGroups.Contains(g.Key)).Sum(g => g.Value));
        UpdateViewTitle(query);
        UpdateCounters();
        UpdateEmptyState(rows.Count, query, folderFiltered);
        UpdateToggleGroupsButton(groupCounts.Keys);
    }

    private void UpdateEmptyState(int displayed, string query, bool folderFiltered)
    {
        if (displayed > 0 || _allRows.Count == 0)
        {
            EmptyState.IsVisible = false;
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
            EmptyStateClearButton.IsVisible = false;
        }
        else
        {
            EmptyStateTitle.Text = "Aucune machine ne correspond";
            var where = folderFiltered ? " dans ce dossier" : "";
            EmptyStateDetail.Text = $"Rien ne répond à {string.Join(" et ", causes)}{where}.";
            EmptyStateClearButton.IsVisible = true;
        }
        EmptyState.IsVisible = true;
    }

    // MARK: - Dossiers repliés

    private void GroupHeader_Tapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not GroupHeaderRow header) return;
        if (!_collapsedGroups.Remove(header.Name)) _collapsedGroups.Add(header.Name);
        _allGroupsCollapsed = false;
        e.Handled = true;
        ApplyFilter();
    }

    private void ToggleGroups_Click(object? sender, RoutedEventArgs e)
    {
        var names = _lastPlan.Where(p => p.Source is null).Select(p => p.Group).ToList();
        bool anyExpanded = names.Any(n => !_collapsedGroups.Contains(n));
        if (anyExpanded)
        {
            foreach (var name in names) _collapsedGroups.Add(name);
            _allGroupsCollapsed = true;
        }
        else
        {
            _collapsedGroups.Clear();
            _allGroupsCollapsed = false;
        }
        ApplyFilter();
    }

    private void UpdateToggleGroupsButton(IEnumerable<string> groups)
    {
        var names = groups.ToList();
        bool anyExpanded = names.Count == 0 || names.Any(n => !_collapsedGroups.Contains(n));
        ToggleGroupsGlyph.Kind = anyExpanded ? IconKind.ChevronUp : IconKind.ChevronDown;
        ToolTip.SetTip(ToggleGroupsButton, anyExpanded ? "Replier tous les dossiers" : "Déplier tous les dossiers");
        ToggleGroupsButton.IsVisible = names.Count > 0;
    }

    // MARK: - Lignes : conteneurs, sélection, ouverture

    private void DeviceList_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        bool header = e.Container.DataContext is GroupHeaderRow;
        e.Container.Classes.Set("header", header);
        e.Container.Focusable = !header;
    }

    private IReadOnlyList<DeviceRow> SelectedRows =>
        DeviceList.SelectedItems?.OfType<DeviceRow>().ToList() ?? new List<DeviceRow>();

    private void DeviceList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Un en-tête ne se sélectionne pas.
        foreach (var header in e.AddedItems.OfType<GroupHeaderRow>().ToList())
            DeviceList.SelectedItems?.Remove(header);
        UpdateSelectionBar();
    }

    private void UpdateSelectionBar()
    {
        var count = SelectedRows.Count;
        SelectionBar.IsVisible = count >= 2;
        if (count < 2) return;
        var online = SelectedRows.Count(r => r.IsOnline);
        SelectionText.Text = $"{count} machines sélectionnées · {online} en ligne";
        BulkSendFilesButton.IsEnabled = SelectedRows.Any(r => r.RemoteActionsVisible);
    }

    private void ClearSelection_Click(object? sender, RoutedEventArgs e) => DeviceList.SelectedItems?.Clear();

    private void DeviceList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsInsideButton(e.Source)) return;
        if ((e.Source as Control)?.DataContext is DeviceRow row) OpenDesktop(row);
    }

    private static bool IsInsideButton(object? source)
    {
        for (var element = source as StyledElement; element is not null; element = element.Parent)
        {
            if (element is Button) return true;
            if (element is ListBoxItem) return false;
        }
        return false;
    }

    private void DeviceList_KeyDown(object? sender, KeyEventArgs e)
    {
        var row = SelectedRows.FirstOrDefault();
        if (e.Key == Key.Enter && row is not null)
        {
            e.Handled = true;
            OpenDesktop(row);
        }
        else if (e.Key == Key.T && e.KeyModifiers == CommandKey && row is not null)
        {
            e.Handled = true;
            OpenTerminal(row.Node, TerminalWindow.Mode.Agent);
        }
        else if (e.Key == Key.E && e.KeyModifiers == CommandKey && row is not null)
        {
            e.Handled = true;
            OpenFiles(row.Node);
        }
        else if (e.Key == Key.F2 && row is not null)
        {
            e.Handled = true;
            _ = RenameAsync(row.Node);
        }
        else if (e.Key == Key.Escape && SelectedRows.Count > 0)
        {
            e.Handled = true;
            DeviceList.SelectedItems?.Clear();
        }
    }

    private void RowAction_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: DeviceRow row, Tag: string action }) return;
        switch (action)
        {
            case "Desktop": OpenDesktop(row); break;
            case "Terminal": OpenTerminal(row.Node, TerminalWindow.Mode.Agent); break;
            case "Watch": SetWatched([row.Node], !row.IsWatched); break;
            case "Files": OpenFiles(row.Node); break;
            case "Wake": _ = RunPowerAsync(() => _connection!.WakeAsync(row.Node.Id),
                                           $"Signal de réveil envoyé à « {row.Name} »."); break;
        }
    }

    private void Favorite_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Button)?.DataContext is DeviceRow row) ToggleFavorite(row.Node);
    }

    private void ToggleFavorite(MeshNode node)
    {
        Settings.ToggleFavorite(node.Id);
        Settings.Save();
        var isFavorite = Settings.IsFavorite(node.Id);
        if (_rowsById.TryGetValue(node.Id, out var row)) row.IsFavorite = isFavorite;
        foreach (var copy in _virtualRows.Where(r => r.Node.Id == node.Id)) copy.IsFavorite = isFavorite;
        RebuildFolderPanel();
        ApplyFilter(force: true);
    }

    private void OpenDesktop(DeviceRow row)
    {
        if (_connection is null) return;
        var node = row.Node;
        if (!node.IsOnline)
        {
            ShowToast($"« {node.Name} » est hors ligne. Clic droit pour tenter un réveil (Wake-on-LAN).");
            return;
        }
        if (!node.HasAgent)
        {
            ShowToast($"« {node.Name} » n'a pas d'agent : le bureau à distance n'est pas disponible.");
            return;
        }

        Settings.RememberRecent(node.Id);
        Settings.Save();

        var existing = _desktops.FirstOrDefault(w => w.NodeId == node.Id);
        if (existing is not null)
        {
            SessionsWindow.Activate(existing);
            return;
        }
        var window = new DesktopWindow(_connection, node)
        {
            OpenTerminal = () => OpenTerminal(node, TerminalWindow.Mode.Agent),
            OpenFiles = () => OpenFiles(node),
            OpenProcesses = () => OpenProcesses(node),
            OpenSystemInfo = () => OpenSystemInfo(node),
        };
        window.Closed += (_, _) => _desktops.Remove(window);
        _desktops.Add(window);
        OpenSession(window, node.Name, "🖥");

        if (Settings.FolderView) RebuildFolderPanel();
    }

    /// <summary>
    /// Terminal de la machine, par l'agent ou par SSH. Une fenêtre déjà
    /// ouverte pour la même machine et le même mode est ramenée au premier plan.
    /// </summary>
    private void OpenTerminal(MeshNode node, TerminalWindow.Mode mode)
    {
        if (_connection is null) return;
        if (!node.IsOnline || (mode == TerminalWindow.Mode.Agent && !node.HasAgent))
        {
            ShowToast($"« {node.Name} » est hors ligne : terminal indisponible.");
            return;
        }

        Settings.RememberRecent(node.Id);
        Settings.Save();

        var existing = _terminals.FirstOrDefault(w => w.NodeId == node.Id && w.SessionMode == mode);
        if (existing is not null)
        {
            SessionsWindow.Activate(existing);
            return;
        }
        var row = _rowsById.GetValueOrDefault(node.Id);
        var window = new TerminalWindow(_connection, node, mode)
        {
            OpenDesktop = () => { if (row is not null) OpenDesktop(row); },
        };
        window.Closed += (_, _) => _terminals.Remove(window);
        _terminals.Add(window);
        OpenSession(window, node.Name, "▶");
    }

    /// <summary>Fichiers de la machine : une seule fenêtre par machine.</summary>
    private void OpenFiles(MeshNode node)
    {
        if (_connection is null) return;
        if (!node.IsOnline || !node.HasAgent)
        {
            ShowToast($"« {node.Name} » est hors ligne : fichiers indisponibles.");
            return;
        }

        Settings.RememberRecent(node.Id);
        Settings.Save();

        var existing = _filesWindows.FirstOrDefault(w => w.NodeId == node.Id);
        if (existing is not null)
        {
            SessionsWindow.Activate(existing);
            return;
        }
        var window = new FilesWindow(_connection, node);
        window.Closed += (_, _) => _filesWindows.Remove(window);
        _filesWindows.Add(window);
        OpenSession(window, node.Name, "📁");
    }

    /// <summary>
    /// Présente une session selon la préférence : dans un onglet de la fenêtre
    /// commune, ou dans sa propre fenêtre.
    /// </summary>
    private void OpenSession(ChromeWindow session, string title, string glyph)
    {
        if (Settings.UseTabbedSessions) SessionsWindow.AddSession(session, title, glyph);
        else
        {
            session.Show();
            session.Activate();
        }
    }

    // MARK: - Menu contextuel

    private void DeviceMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var rows = SelectedRows;
        bool any = rows.Count > 0;
        bool single = rows.Count == 1;
        var first = rows.FirstOrDefault();

        MenuDesktop.IsEnabled = single && first!.RemoteActionsVisible;
        MenuTerminal.IsEnabled = single && first!.RemoteActionsVisible;
        MenuFiles.IsEnabled = single && first!.RemoteActionsVisible;
        MenuSendFiles.IsEnabled = any && rows.Any(r => r.RemoteActionsVisible);
        // Le partage se crée côté serveur : la machine n'a pas besoin d'être en ligne.
        MenuShare.IsEnabled = single;
        // Processus et ouverture d'URL passent par l'agent : machine joignable requise.
        MenuProcesses.IsEnabled = single && first!.RemoteActionsVisible;
        MenuOpenUrl.IsEnabled = single && first!.RemoteActionsVisible;
        MenuInfo.IsEnabled = single;
        MenuSsh.IsEnabled = single && first!.IsOnline;
        MenuWake.IsEnabled = any && rows.Any(r => !r.IsOnline);
        var offlineRows = rows.Where(r => !r.IsOnline).ToList();
        MenuWatch.IsVisible = offlineRows.Count > 0;
        MenuWatch.Header = offlineRows.Count > 0 && offlineRows.All(r => r.IsWatched)
            ? (offlineRows.Count == 1 ? "Ne plus me prévenir de son retour" : "Ne plus me prévenir de leur retour")
            : (offlineRows.Count > 1 ? "Me prévenir de leur retour" : "Me prévenir à son retour");
        MenuReboot.IsEnabled = MenuSleep.IsEnabled = MenuPowerOff.IsEnabled = any && rows.Any(r => r.IsOnline);
        MenuRename.IsEnabled = single;
        MenuWebPage.IsEnabled = single;
        MenuCopyName.IsEnabled = any;
        MenuFavorite.IsEnabled = any;
        MenuFavorite.Header = any && rows.All(r => r.IsFavorite) ? "Retirer des favoris" : "Ajouter aux favoris";
    }

    private void MenuDesktop_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is { } row) OpenDesktop(row);
    }

    private void MenuTerminal_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is { } row) OpenTerminal(row.Node, TerminalWindow.Mode.Agent);
    }

    private void MenuFiles_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is { } row) OpenFiles(row.Node);
    }

    private void MenuSsh_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is { } row) OpenTerminal(row.Node, TerminalWindow.Mode.Ssh);
    }

    // MARK: - Alerte de retour en ligne

    /// <summary>Machines dont on attend le retour en ligne (pour cette session de l'application).</summary>
    private readonly HashSet<string> _watchOnline = new();

    private void MenuWatch_Click(object? sender, RoutedEventArgs e)
    {
        var offline = SelectedRows.Where(r => !r.IsOnline).Select(r => r.Node).ToList();
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
    /// vient de se connecter déclenche une notification, une seule fois.
    /// </summary>
    private void CheckWatchedNodes()
    {
        if (_watchOnline.Count == 0 || _connection is not { } connection) return;

        var back = _watchOnline
            .Select(id => connection.Nodes.TryGetValue(id, out var node) ? node : null)
            .OfType<MeshNode>()
            .Where(n => n.IsOnline)
            .ToList();
        if (back.Count == 0)
        {
            RefreshWatchMarks();
            return;
        }

        foreach (var node in back)
        {
            _watchOnline.Remove(node.Id);
            Log.Info($"Retour en ligne signalé : {node.Name}", "Appareil");
        }
        RefreshWatchMarks();

        var first = back[0];
        var title = back.Count == 1 ? $"« {first.Name} » est en ligne" : $"{back.Count} machines sont en ligne";
        var message = back.Count == 1 ? "La machine est de nouveau joignable." : string.Join(", ", back.Select(n => n.Name));
        SystemNotification.Show(title, message);
        ShowToast(back.Count == 1 ? $"« {first.Name} » est de retour en ligne." : $"{title}.");
    }

    private void MenuWake_Click(object? sender, RoutedEventArgs e) => BulkWake_Click(sender, e);

    private async void MenuReboot_Click(object? sender, RoutedEventArgs e)
        => await PowerAsync(SelectedRows, PowerActionType.Reboot);

    private async void MenuSleep_Click(object? sender, RoutedEventArgs e)
        => await PowerAsync(SelectedRows, PowerActionType.Sleep);

    private async void MenuPowerOff_Click(object? sender, RoutedEventArgs e)
        => await PowerAsync(SelectedRows, PowerActionType.PowerOff);

    private void MenuFavorite_Click(object? sender, RoutedEventArgs e) => BulkFavorite_Click(sender, e);

    private async void MenuRename_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is { } row) await RenameAsync(row.Node);
    }

    private async void MenuCopyName_Click(object? sender, RoutedEventArgs e)
    {
        var names = SelectedRows.Select(r => r.Name).ToList();
        if (names.Count == 0 || Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, names));
        ShowToast(names.Count == 1 ? $"« {names[0]} » copié." : $"{names.Count} noms copiés.");
    }

    private async void MenuWebPage_Click(object? sender, RoutedEventArgs e)
    {
        if (_connection is null || SelectedRows.FirstOrDefault() is not { } row) return;
        var url = _connection.Profile.DeviceWebUrl(row.Node.Id);
        if (url is null)
        {
            ShowToast("Adresse du serveur indisponible.");
            return;
        }
        await Launcher.LaunchUriAsync(new Uri(url));
    }

    private void MenuSendFiles_Click(object? sender, RoutedEventArgs e) => BulkSendFiles_Click(sender, e);

    private void BulkSendFiles_Click(object? sender, RoutedEventArgs e)
        => OpenSendFiles(SelectedRows.Where(r => r.RemoteActionsVisible).Select(r => r.Node).ToList());

    /// <summary>
    /// Ouvre la fenêtre d'envoi, ces machines déjà cochées et, le cas échéant,
    /// ces fichiers déjà choisis.
    /// </summary>
    private void OpenSendFiles(IReadOnlyList<MeshNode> nodes, IEnumerable<string>? files = null)
    {
        if (_connection is null) return;
        new SendFilesWindow(_connection, rememberDestinations: true, preselected: nodes, files: files).Show();
    }

    private async void MenuShare_Click(object? sender, RoutedEventArgs e)
    {
        if (_connection is null || SelectedRows.FirstOrDefault() is not { } row) return;
        await new ShareWindow(_connection, row.Node).ShowDialog(this);
    }

    private void MenuProcesses_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is { } row) OpenProcesses(row.Node);
    }

    private void MenuInfo_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is { } row) OpenSystemInfo(row.Node);
    }

    private async void MenuOpenUrl_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedRows.FirstOrDefault() is { } row) await OpenRemoteUrlAsync(row.Node);
    }

    /// <summary>Processus de la machine : une seule fenêtre par machine.</summary>
    private void OpenProcesses(MeshNode node)
    {
        if (_connection is null) return;
        if (OwnedOrOpen<ProcessWindow>(w => w.NodeId == node.Id)) return;
        new ProcessWindow(_connection, node).Show();
    }

    /// <summary>Informations système : une seule fiche par machine.</summary>
    private void OpenSystemInfo(MeshNode node)
    {
        if (_connection is null) return;
        if (OwnedOrOpen<SystemInfoWindow>(w => w.NodeId == node.Id)) return;
        new SystemInfoWindow(_connection, node).Show();
    }

    /// <summary>Ramène au premier plan une fenêtre déjà ouverte qui répond au critère.</summary>
    private static bool OwnedOrOpen<T>(Func<T, bool> match) where T : Window
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life) return false;
        if (life.Windows.OfType<T>().FirstOrDefault(match) is not { } existing) return false;
        existing.Activate();
        return true;
    }

    /// <summary>Ouvre une adresse dans le navigateur de la machine distante.</summary>
    private async Task OpenRemoteUrlAsync(MeshNode node)
    {
        if (_connection is null) return;
        var url = await Dialogs.PromptAsync(this, "Ouvrir une URL",
            $"Adresse à ouvrir dans le navigateur de « {node.Name} » :", "https://", "Ouvrir", selectAll: false);
        if (string.IsNullOrWhiteSpace(url) || url == "https://") return;
        try
        {
            await _connection.OpenUrlAsync(node.Id, url);
            Log.Info($"URL ouverte sur {node.Name} : {url}", "Appareil");
            ShowToast($"Adresse envoyée à « {node.Name} ».");
        }
        catch (Exception ex) { await Dialogs.InformAsync(this, ex.Message); }
    }

    private async Task RenameAsync(MeshNode node)
    {
        if (_connection is null) return;
        var previous = node.Name;
        var name = await Dialogs.PromptAsync(this, $"Renommer « {previous} »", "Nouveau nom", previous);
        if (string.IsNullOrWhiteSpace(name) || name == previous) return;
        try
        {
            await _connection.RenameDeviceAsync(node.Id, name);
            ShowToast($"« {previous} » s'appelle désormais « {name} ».");
        }
        catch (Exception ex) { ShowToast($"Renommage impossible : {ex.Message}"); }
    }

    // MARK: - Alimentation

    private static string Machines(int n) => n == 1 ? "1 machine" : $"{n} machines";

    private async Task PowerAsync(IReadOnlyList<DeviceRow> rows, PowerActionType action)
    {
        if (_connection is null) return;
        var targets = rows.Where(r => r.IsOnline).ToList();
        if (targets.Count == 0)
        {
            ShowToast("Aucune machine de la sélection n'est joignable.");
            return;
        }

        var who = targets.Count == 1 ? $"« {targets[0].Name} »" : Machines(targets.Count);
        var (title, detail, verb) = action switch
        {
            PowerActionType.Reboot => ($"Redémarrer {who} ?",
                "La machine va redémarrer. Les travaux non enregistrés seront perdus.", "redémarrage"),
            PowerActionType.Sleep => ($"Mettre {who} en veille ?",
                "La machine sera suspendue et ne répondra plus jusqu'à son réveil.", "mise en veille"),
            _ => ($"Éteindre {who} ?",
                "La machine sera arrêtée. Il faudra un réveil Wake-on-LAN ou une action physique pour la rallumer.",
                "extinction"),
        };
        if (!await Dialogs.ConfirmAsync(this, title, detail)) return;
        await RunPowerAsync(() => _connection.PowerActionAsync(targets.Select(r => r.Node.Id), action),
            $"Ordre de {verb} envoyé à {who}.");
    }

    private async Task RunPowerAsync(Func<Task> action, string successMessage)
    {
        try
        {
            await action();
            ShowToast(successMessage);
        }
        catch (Exception ex) { ShowToast($"Action refusée : {ex.Message}"); }
    }

    private void BulkWake_Click(object? sender, RoutedEventArgs e)
    {
        if (_connection is null) return;
        var offline = SelectedRows.Where(r => !r.IsOnline).ToList();
        if (offline.Count == 0) return;
        var who = offline.Count == 1 ? $"« {offline[0].Name} »" : Machines(offline.Count);
        _ = RunPowerAsync(() => _connection.WakeAsync(offline.Select(r => r.Node.Id)), $"Signal de réveil envoyé à {who}.");
    }

    private async void BulkReboot_Click(object? sender, RoutedEventArgs e) => await PowerAsync(SelectedRows, PowerActionType.Reboot);
    private async void BulkPowerOff_Click(object? sender, RoutedEventArgs e) => await PowerAsync(SelectedRows, PowerActionType.PowerOff);

    private void BulkFavorite_Click(object? sender, RoutedEventArgs e)
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;
        bool add = !rows.All(r => r.IsFavorite);
        foreach (var row in rows)
        {
            if (Settings.IsFavorite(row.Node.Id) != add) Settings.ToggleFavorite(row.Node.Id);
            if (_rowsById.TryGetValue(row.Node.Id, out var original)) original.IsFavorite = add;
            row.IsFavorite = add;
        }
        Settings.Save();
        RebuildFolderPanel();
        ApplyFilter(force: true);
        ShowToast(add ? $"{Machines(rows.Count)} dans les favoris." : $"{Machines(rows.Count)} retirée(s) des favoris.");
    }

    // MARK: - Clavier

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (SettingsPanel.IsVisible && e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSettings();
            return;
        }
        if (!DevicesPanel.IsVisible) return;

        if (e.Key == Key.F && e.KeyModifiers == CommandKey)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }

    // MARK: - Bandeau de confirmation

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.2) };

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBanner.IsVisible = true;
        ToastBanner.Opacity = 1;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private async void HideToast()
    {
        _toastTimer.Stop();
        ToastBanner.Opacity = 0;
        await Task.Delay(260);
        if (ToastBanner.Opacity == 0) ToastBanner.IsVisible = false;
    }

    // MARK: - Volet des réglages

    private static readonly int[] QualityTags = [3, 0, 1, 2, 4];
    private static readonly int[] FrameRates = [5, 10, 15, 20, 25, 30, 40];

    private void SettingsButton_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        SettingsSubtitle.Text = Settings.Host;
        DefaultQualityCombo.SelectedIndex = Math.Max(0, Array.IndexOf(QualityTags, Settings.DesktopQuality));
        DefaultFrameRateCombo.SelectedIndex = Math.Max(0, Array.IndexOf(FrameRates,
            Desktop.DesktopSession.NearestFrameRate(Settings.DesktopFrameRate)));
        _densityLoading = true;
        DensityCombo.SelectedIndex = Math.Clamp(Settings.ListDensity, 0, 2);
        _densityLoading = false;
        DarkModeCheck.IsChecked = Settings.DarkMode;
        TabbedSessionsCheck.IsChecked = Settings.UseTabbedSessions;
        TrayCheck.IsChecked = Settings.KeepInMenuBar;
        AutoStartCheck.IsVisible = StartupManager.IsSupported;
        AutoStartCheck.IsChecked = StartupManager.IsEnabled();
        StartMinimizedCheck.IsChecked = Settings.StartMinimized;
        CheckUpdatesCheck.IsChecked = Settings.CheckForUpdates;
        StartupWarning.IsVisible = false;
        HostBox.Text = Settings.Host;
        SelfSignedCheck.IsChecked = Settings.AllowSelfSigned;
        RememberSettingCheck.IsChecked = Settings.RememberPassword;
        RememberSettingHint.Text = OperatingSystem.IsMacOS()
            ? "Le mot de passe est conservé dans le Trousseau macOS."
            : "Le mot de passe est chiffré pour votre session Windows.";
        AccountText.Text = _connection?.UserInfo is { } info
            ? $"Connecté en tant que {info.Name}" + (string.IsNullOrEmpty(info.Email) ? "." : $" ({info.Email}).")
            : string.IsNullOrEmpty(Settings.Username) ? "Non connecté." : $"Compte : {Settings.Username}.";
        LogoutButton.IsVisible = _connection is not null;
        SettingsFeedback.Text = "";
        VersionText.Text = $"Mesh Control pour macOS {typeof(App).Assembly.GetName().Version?.ToString(3)} — prototype";

        SettingsScrim.IsVisible = true;
        SettingsPanel.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            SettingsScrim.Opacity = 1;
            SettingsPanel.RenderTransform = TransformOperations.Parse("translateX(0px)");
        }, DispatcherPriority.Render);
    }

    private async void CloseSettings()
    {
        SettingsScrim.Opacity = 0;
        SettingsPanel.RenderTransform = TransformOperations.Parse("translateX(420px)");
        await Task.Delay(230);
        if (SettingsScrim.Opacity == 0)
        {
            SettingsScrim.IsVisible = false;
            SettingsPanel.IsVisible = false;
        }
    }

    private void CloseSettings_Click(object? sender, RoutedEventArgs e)
    {
        // Annuler : la densité, appliquée en direct, revient à sa valeur.
        Palette.ApplyDensity(Application.Current!, Settings.ListDensity);
        ApplyColumnsPadding();
        CloseSettings();
    }

    private void SettingsScrim_PointerPressed(object? sender, PointerPressedEventArgs e) => CloseSettings_Click(sender, e);

    private bool _densityLoading;

    /// <summary>Densité appliquée en direct, pour juger sur pièce.</summary>
    private void DensityCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_densityLoading || DensityCombo.SelectedIndex < 0) return;
        Palette.ApplyDensity(Application.Current!, DensityCombo.SelectedIndex);
    }

    private void DarkMode_Changed(object? sender, RoutedEventArgs e)
        => DarkModeHint.Text = DarkModeCheck.IsChecked == Settings.DarkMode
            ? "Pour toutes les fenêtres de l'application. S'applique au prochain démarrage."
            : "Le thème changera au prochain démarrage de l'application.";

    private void SaveSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DefaultQualityCombo.SelectedIndex >= 0) Settings.DesktopQuality = QualityTags[DefaultQualityCombo.SelectedIndex];
        if (DefaultFrameRateCombo.SelectedIndex >= 0) Settings.DesktopFrameRate = FrameRates[DefaultFrameRateCombo.SelectedIndex];
        if (DensityCombo.SelectedIndex >= 0) Settings.ListDensity = DensityCombo.SelectedIndex;
        Settings.DarkMode = DarkModeCheck.IsChecked == true;
        Settings.UseTabbedSessions = TabbedSessionsCheck.IsChecked == true;
        Settings.KeepInMenuBar = TrayCheck.IsChecked == true;
        Settings.StartMinimized = StartMinimizedCheck.IsChecked == true;
        Settings.CheckForUpdates = CheckUpdatesCheck.IsChecked == true;
        var wantAutoStart = AutoStartCheck.IsChecked == true;
        if (StartupManager.IsSupported && wantAutoStart != StartupManager.IsEnabled()
            && !StartupManager.SetEnabled(wantAutoStart))
        {
            StartupWarning.Text = "Impossible de modifier l'ouverture à la connexion (voir le journal).";
            StartupWarning.IsVisible = true;
        }
        Settings.RememberPassword = RememberSettingCheck.IsChecked == true;
        if (!Settings.RememberPassword) Keychain.Delete(PasswordAccount(Settings.Host, Settings.Username));

        var host = (HostBox.Text ?? "").Trim().TrimEnd('/');
        bool serverChanged = !string.Equals(host, Settings.Host, StringComparison.OrdinalIgnoreCase)
                             || (SelfSignedCheck.IsChecked == true) != Settings.AllowSelfSigned;
        if (serverChanged && host.Length > 0)
        {
            CertificateTrust.Forget(Settings.Host);
            Settings.Host = host;
            Settings.AllowSelfSigned = SelfSignedCheck.IsChecked == true;
        }
        Settings.Save();
        Palette.ApplyDensity(Application.Current!, Settings.ListDensity);
        ApplyColumnsPadding();
        CloseSettings();

        if (serverChanged)
        {
            DisposeConnection();
            ShowLogin();
        }
        else
        {
            ShowToast("Réglages enregistrés.");
        }
    }

    private void Logout_Click(object? sender, RoutedEventArgs e)
    {
        Keychain.Delete(PasswordAccount(Settings.Host, Settings.Username));
        _password = null;
        Settings.RememberPassword = false;
        Settings.Save();
        CloseSettings();
        DisposeConnection();
        ShowLogin();
    }

    private async void OpenLogs_Click(object? sender, RoutedEventArgs e)
        => await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Log.Folder));
}

/// <summary>
/// Choix du modèle de ligne : en-tête de dossier, carte (barre latérale) ou
/// colonnes — l'équivalent de DeviceListTemplateSelector sous Windows.
/// </summary>
public sealed class DeviceTemplateSelector : IDataTemplate
{
    public required IDataTemplate Header { get; init; }
    public required IDataTemplate Card { get; init; }
    public required IDataTemplate Columns { get; init; }
    public bool UseColumns { get; init; }

    public bool Match(object? data) => data is DeviceRow or GroupHeaderRow;

    public Control? Build(object? data) => data is GroupHeaderRow
        ? Header.Build(data)
        : (UseColumns ? Columns : Card).Build(data);
}
