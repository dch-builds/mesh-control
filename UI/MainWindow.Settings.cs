using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>
/// Volet des réglages, coulissant depuis la droite. Séparé du reste de
/// MainWindow pour garder chaque fichier lisible.
/// </summary>
public partial class MainWindow
{
    private const double PanelWidth = 420;

    /// <summary>Valeurs au moment de l'ouverture, pour pouvoir annuler.</summary>
    private string _settingsOriginalHost = "";
    private bool _settingsOriginalSelfSigned;
    private string _settingsOriginalHotkey = "";
    private bool _settingsOriginalQuickSearch;

    // MARK: - Ouverture / fermeture

    private void OpenSettings()
    {
        LoadSettingsIntoPanel();

        SettingsScrim.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Visible;
        SettingsFeedback.Text = "";

        // Glissement depuis la droite + fondu du voile : le volet vient
        // « par-dessus » le contenu sans le remplacer.
        var slide = new DoubleAnimation(PanelWidth, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        SettingsTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
        SettingsScrim.BeginAnimation(OpacityProperty, fade);
    }

    private void CloseSettings()
    {
        var slide = new DoubleAnimation(0, PanelWidth, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        // Masquer seulement à la fin, sinon le volet disparaîtrait d'un coup.
        slide.Completed += (_, _) =>
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            SettingsScrim.Visibility = Visibility.Collapsed;
        };
        SettingsTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        SettingsScrim.BeginAnimation(OpacityProperty, fade);
    }

    private bool IsSettingsOpen => SettingsPanel.Visibility == Visibility.Visible;

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();
    private void SettingsScrim_Click(object sender, MouseButtonEventArgs e) => CloseSettings();

    // MARK: - Chargement

    private void LoadSettingsIntoPanel()
    {
        _settingsOriginalHost = _settings.Host;
        _settingsOriginalSelfSigned = _settings.AllowSelfSigned;
        _settingsOriginalHotkey = _settings.QuickSearchHotkey;
        _settingsOriginalQuickSearch = _settings.QuickSearchEnabled;

        // Bureau à distance
        DefaultQualityCombo.SelectedIndex = Math.Clamp(_settings.DefaultDesktopQuality, 0, 2);
        SelectFrameRate(_settings.DefaultDesktopFrameRate);
        SelectScale(_settings.DefaultDesktopScale);
        DefaultFitCheck.IsChecked = _settings.DefaultDesktopFit;
        DesktopOpenModeCombo.SelectedIndex = Math.Clamp(_settings.DesktopOpenMode, 0, 2);
        TabbedSessionsCheck.IsChecked = _settings.UseTabbedSessions;

        // Recherche rapide
        QuickSearchCheck.IsChecked = _settings.QuickSearchEnabled;
        HotkeyBox.Text = _settings.QuickSearchHotkey;
        HotkeyBox.IsEnabled = _settings.QuickSearchEnabled;
        HotkeyWarning.Visibility = Visibility.Collapsed;

        // Comportement
        TrayCheck.IsChecked = _settings.MinimizeToTray;
        StartMinimizedCheck.IsChecked = _settings.StartMinimized;
        AutoStartCheck.IsChecked = StartupManager.IsEnabled();
        StartupWarning.Visibility = Visibility.Collapsed;

        // Serveur & compte
        HostBox.Text = _settings.Host;
        SelfSignedCheck.IsChecked = _settings.AllowSelfSigned;
        RememberSettingCheck.IsChecked = _settings.RememberCredentials;
        AccountText.Text = string.IsNullOrEmpty(_settings.Username)
            ? "Aucun compte mémorisé."
            : $"Connecté en tant que « {_settings.Username} »";
        UpdateTrustStateUi();

        // Diagnostic
        VerboseLogCheck.IsChecked = _settings.VerboseLogging;
        CheckUpdatesCheck.IsChecked = _settings.CheckForUpdates;
        DensityCombo.SelectedIndex = Math.Clamp(_settings.ListDensity, 0, 2);
        DarkModeCheck.IsChecked = _settings.DarkMode;
        UpdateThemeRestartHint();

        SettingsSubtitle.Text = _settings.Host;
        VersionText.Text = $"Mesh Control {App.Version}";
    }

    private void SelectFrameRate(int fps)
    {
        // Un réglage d'une version antérieure (45 ou 60) n'est plus proposé :
        // on retient la cadence la plus proche plutôt que de retomber à 10.
        fps = Desktop.DesktopSession.NearestFrameRate(fps);
        foreach (ComboBoxItem item in DefaultFrameRateCombo.Items)
        {
            if (item.Tag is string tag && int.TryParse(tag, out var value) && value == fps)
            {
                DefaultFrameRateCombo.SelectedItem = item;
                return;
            }
        }
        DefaultFrameRateCombo.SelectedIndex = 1;   // 10 img/s par défaut
    }

    private void SelectScale(int percent)
    {
        foreach (ComboBoxItem item in DefaultScaleCombo.Items)
        {
            if (item.Tag is string tag && int.TryParse(tag, out var value) && value == percent)
            {
                DefaultScaleCombo.SelectedItem = item;
                return;
            }
        }
        DefaultScaleCombo.SelectedIndex = 0;   // 100 % par défaut
    }

    // MARK: - Enregistrement

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (string.IsNullOrEmpty(host))
        {
            ShowSettingsFeedback("Renseignez l'adresse du serveur.", isError: true);
            HostBox.Focus();
            return;
        }

        // Le raccourci doit rester valide, sinon la recherche rapide serait
        // silencieusement désactivée.
        var hotkeyText = HotkeyBox.Text.Trim();
        if (QuickSearchCheck.IsChecked == true && AppSettings.ParseHotkey(hotkeyText) is null)
        {
            ShowHotkeyWarning("Raccourci invalide. Pressez une combinaison avec au moins un modificateur.");
            return;
        }

        // Bureau à distance
        _settings.DefaultDesktopQuality = DefaultQualityCombo.SelectedIndex;
        _settings.DefaultDesktopFrameRate = SelectedFrameRate();
        _settings.DefaultDesktopScale = SelectedScale();
        _settings.DefaultDesktopFit = DefaultFitCheck.IsChecked == true;
        _settings.DesktopOpenMode = Math.Max(0, DesktopOpenModeCombo.SelectedIndex);
        _settings.UseTabbedSessions = TabbedSessionsCheck.IsChecked == true;

        // Recherche rapide
        _settings.QuickSearchEnabled = QuickSearchCheck.IsChecked == true;
        if (_settings.QuickSearchEnabled) _settings.QuickSearchHotkey = hotkeyText;

        // Comportement
        _settings.MinimizeToTray = TrayCheck.IsChecked == true;
        _settings.StartMinimized = StartMinimizedCheck.IsChecked == true;

        // Serveur & compte
        var allowSelfSigned = SelfSignedCheck.IsChecked == true;

        // Un autre serveur, c'est un autre compte : mot de passe, jeton de
        // connexion et cookie de second facteur de l'ancien ne doivent pas lui
        // être présentés (même règle que l'écran de choix du serveur).
        if (!string.Equals(host, _settings.Host, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(_settings.Host))
        {
            _settings.ProtectedPassword = null;
            _settings.ClearDeviceTrust();
        }

        // Option décochée : le certificat retenu est oublié, pour qu'un
        // certificat renouvelé puisse être accepté en la recochant.
        if (!allowSelfSigned && _settings.AllowSelfSigned)
            CertificateTrust.Forget(new ServerProfile { Host = _settings.Host }.BaseUri?.Authority);

        _settings.Host = host;
        _settings.AllowSelfSigned = allowSelfSigned;
        _settings.RememberCredentials = RememberSettingCheck.IsChecked == true;
        if (!_settings.RememberCredentials)
        {
            // Ne rien laisser qui rouvrirait une session sans mot de passe.
            _settings.ProtectedPassword = null;
            _settings.ClearDeviceTrust();
        }

        // Diagnostic
        _settings.VerboseLogging = VerboseLogCheck.IsChecked == true;
        _settings.CheckForUpdates = CheckUpdatesCheck.IsChecked == true;
        _settings.ListDensity = Math.Clamp(DensityCombo.SelectedIndex, 0, 2);
        ApplyListDensity();
        Log.MinimumLevel = _settings.VerboseLogging ? LogLevel.Debug : LogLevel.Info;

        // Démarrage automatique : n'écrire dans le registre que si l'état change.
        bool wantAutoStart = AutoStartCheck.IsChecked == true;
        if (wantAutoStart != StartupManager.IsEnabled()
            && !StartupManager.SetEnabled(wantAutoStart))
        {
            StartupWarning.Text = "Impossible de modifier le démarrage automatique "
                                + "(accès au registre refusé). Les autres réglages sont enregistrés.";
            StartupWarning.Visibility = Visibility.Visible;
        }

        // Enregistrement explicite demandé par l'utilisateur : écriture immédiate,
        // les modifications par effet de bord (favoris, dossiers) restent différées.
        _settings.SaveNow();
        ApplySettingsToUi();

        bool hotkeyChanged = _settings.QuickSearchHotkey != _settingsOriginalHotkey
                             || _settings.QuickSearchEnabled != _settingsOriginalQuickSearch;
        if (hotkeyChanged) SetupHotkey();

        bool shouldReconnect = host != _settingsOriginalHost
                               || _settings.AllowSelfSigned != _settingsOriginalSelfSigned;

        CloseSettings();

        if (shouldReconnect)
        {
            Log.Info($"Reconnexion suite au changement de serveur : {host}", "Réglages");
            _connection?.Disconnect();
            ShowLogin();
            SetStatus("Le serveur a changé : reconnectez-vous.");
        }
    }

    private int SelectedScale()
    {
        if (DefaultScaleCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && int.TryParse(tag, out var value))
            return value;
        return 100;
    }

    private int SelectedFrameRate()
    {
        if (DefaultFrameRateCombo.SelectedItem is ComboBoxItem { Tag: string tag }
            && int.TryParse(tag, out var value))
            return value;
        return 10;
    }

    // MARK: - Autorisation de ce poste (second facteur)

    /// <summary>
    /// Reflète l'état de l'autorisation : jeton de connexion, mémorisation
    /// temporaire, ou rien. Les boutons ne s'affichent que lorsqu'ils peuvent
    /// aboutir — créer un jeton suppose une session ouverte avec le compte.
    /// </summary>
    private void UpdateTrustStateUi()
    {
        var canCreate = _connection is { State: ConnectionState.Connected }
                        && _connection.CanCreateLoginToken;

        if (_settings.HasLoginToken)
        {
            TrustStateText.Text = string.IsNullOrEmpty(_settings.LoginTokenName)
                ? "Ce poste est autorisé sans limite de durée."
                : $"Ce poste est autorisé sans limite de durée (jeton « {_settings.LoginTokenName} »).";
            TrustDeviceButton.Visibility = Visibility.Collapsed;
            ForgetDeviceButton.Visibility = Visibility.Visible;
        }
        else if (_settings.HasTwoFactorCookie)
        {
            TrustStateText.Text = _settings.TwoFactorCookieExpiry is { } until
                ? $"Ce poste est mémorisé jusqu'au {until:dd/MM/yyyy}."
                : "Ce poste est mémorisé : le code n'est pas redemandé.";
            TrustDeviceButton.Visibility = canCreate ? Visibility.Visible : Visibility.Collapsed;
            ForgetDeviceButton.Visibility = Visibility.Visible;
        }
        else
        {
            TrustStateText.Text = "Le code à deux facteurs est demandé à chaque connexion.";
            TrustDeviceButton.Visibility = canCreate ? Visibility.Visible : Visibility.Collapsed;
            ForgetDeviceButton.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Crée un jeton de connexion pour ce poste : le serveur acceptera ensuite
    /// l'application sans second facteur, indéfiniment.
    /// </summary>
    private async void TrustDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_connection is not { State: ConnectionState.Connected })
        {
            ShowSettingsFeedback("Connectez-vous d'abord au serveur.", isError: true);
            return;
        }
        if (!_connection.CanCreateLoginToken)
        {
            ShowSettingsFeedback("Cette session est déjà ouverte avec un jeton.", isError: true);
            return;
        }

        TrustDeviceButton.IsEnabled = false;
        try
        {
            // Le nom identifie le poste dans « Mon compte » sur le serveur :
            // c'est là que l'administrateur révoquera le jeton si nécessaire.
            var name = $"Mesh Control — {Environment.MachineName}";
            var token = await _connection.CreateLoginTokenAsync(name);
            if (token is null)
            {
                ShowSettingsFeedback(
                    "Le serveur refuse les jetons de connexion pour ce compte.", isError: true);
                return;
            }

            _settings.LoginTokenUser = token.User;
            _settings.LoginTokenPass = token.Password;
            _settings.LoginTokenName = token.Name;
            _settings.SaveNow();

            Log.Info($"Jeton de connexion créé pour ce poste : {token.Name}", "Serveur");
            UpdateTrustStateUi();
            ShowSettingsFeedback("Ce poste est autorisé : plus aucun code à saisir.");
        }
        catch (Exception ex)
        {
            ShowSettingsFeedback($"Échec : {ex.Message}", isError: true);
        }
        finally
        {
            TrustDeviceButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Retire l'autorisation de ce poste : le jeton est révoqué côté serveur
    /// quand c'est possible, et les secrets locaux sont effacés dans tous les
    /// cas. Le code sera redemandé à la prochaine connexion.
    /// </summary>
    private async void ForgetDevice_Click(object sender, RoutedEventArgs e)
    {
        var tokenUser = _settings.LoginTokenUser;

        _settings.ClearDeviceTrust();
        UpdateTrustStateUi();
        Log.Info("Autorisation de ce poste retirée (second facteur).", "Serveur");

        if (string.IsNullOrEmpty(tokenUser))
        {
            ShowSettingsFeedback("Ce poste est oublié.");
            return;
        }

        // La révocation peut être refusée en silence : c'est la réponse du
        // serveur qui dit si le jeton est bien mort.
        if (_connection is { State: ConnectionState.Connected }
            && await _connection.RevokeLoginTokenAsync(tokenUser))
        {
            ShowSettingsFeedback("Ce poste est oublié, le jeton est révoqué.");
            return;
        }

        ShowSettingsFeedback("Ce poste est oublié. Le jeton reste à révoquer depuis "
                           + "« Mon compte » sur le serveur.");
    }

    private void ShowSettingsFeedback(string message, bool isError = false)
    {
        SettingsFeedback.Text = message;
        SettingsFeedback.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("DangerBrush")
            : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
    }

    // MARK: - Raccourci clavier

    private void QuickSearch_Changed(object sender, RoutedEventArgs e)
    {
        // Ce handler se déclenche aussi pendant le chargement du XAML.
        if (HotkeyBox is null) return;
        HotkeyBox.IsEnabled = QuickSearchCheck.IsChecked == true;
    }

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
        => HotkeyBox.Text = "Pressez la combinaison…";

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (AppSettings.ParseHotkey(HotkeyBox.Text) is null)
            HotkeyBox.Text = _settings.QuickSearchHotkey;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;   // capturer la frappe sans la laisser agir

        var key = e.Key;
        if (key == Key.System) key = e.SystemKey;

        // Ignorer l'appui d'un modificateur seul : on attend la touche finale.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
                or Key.System or Key.None)
            return;

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            ShowHotkeyWarning("Ajoutez au moins un modificateur (Ctrl, Alt, Maj ou Win) : "
                            + "sans cela, la touche serait capturée dans toutes les applications.");
            return;
        }

        HotkeyBox.Text = AppSettings.FormatHotkey(mods, key);
        HotkeyWarning.Visibility = Visibility.Collapsed;
    }

    private void ShowHotkeyWarning(string message)
    {
        HotkeyWarning.Text = message;
        HotkeyWarning.Visibility = Visibility.Visible;
    }

    // MARK: - Déconnexion

    /// <summary>
    /// Ferme la session : coupe la connexion, efface le mot de passe mémorisé et
    /// revient à l'écran de connexion. Les fenêtres de session ouvertes sont
    /// fermées, car elles deviendraient inutilisables sans connexion.
    /// </summary>
    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        // Compter les sessions actives pour prévenir avant de les interrompre.
        var openSessions = Application.Current.Windows.OfType<Window>()
            .Count(w => w is Desktop.DesktopWindow or Ssh.TerminalWindow
                          or Ssh.SshWindow or Files.FilesWindow);

        // Se déconnecter efface aussi l'autorisation de ce poste : laisser un
        // jeton de connexion derrière soi reviendrait à garder un accès ouvert.
        var trustNote = (_settings.HasLoginToken || _settings.HasTwoFactorCookie)
            ? " L'autorisation de ce poste sera retirée : un code à deux facteurs "
              + "sera redemandé."
            : "";

        var detail = openSessions > 0
            ? $"{openSessions} session(s) en cours seront fermées.\n\n"
              + "Le mot de passe mémorisé sera effacé : il faudra le ressaisir à la "
              + "prochaine connexion." + trustNote
            : "Le mot de passe mémorisé sera effacé : il faudra le ressaisir à la "
              + "prochaine connexion." + trustNote;

        if (MessageBox.Show(detail, "Se déconnecter ?",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        // Révoquer le jeton tant que la connexion est encore ouverte : après la
        // déconnexion, plus rien ne permet de joindre le serveur.
        var tokenUser = _settings.LoginTokenUser;
        if (!string.IsNullOrEmpty(tokenUser) && _connection is { State: ConnectionState.Connected })
        {
            try { await _connection.RevokeLoginTokenAsync(tokenUser); }
            catch (Exception ex) { Log.Warning($"Révocation du jeton impossible : {ex.Message}", "Serveur"); }
        }

        Logout();
    }

    private void Logout()
    {
        Log.Info($"Déconnexion de {_settings.Username}", "Serveur");

        // Fermer les fenêtres de session : sans connexion, elles ne peuvent plus
        // rien faire et afficheraient des erreurs.
        foreach (var window in Application.Current.Windows.OfType<Window>()
                     .Where(w => w is Desktop.DesktopWindow or Ssh.TerminalWindow
                                   or Ssh.SshWindow or Files.FilesWindow)
                     .ToList())
        {
            try { window.Close(); } catch { /* déjà en cours de fermeture */ }
        }

        CloseSettings();

        // Interrompre une éventuelle boucle de reconnexion automatique, sinon
        // elle rétablirait la session que l'on vient de fermer.
        StopReconnecting();

        if (_connection is not null)
        {
            _connection.StateChanged -= OnStateChanged;
            _connection.DevicesChanged -= OnDevicesChanged;
            _connection.Disconnect();
            _connection.Dispose();
            _connection = null;
        }

        // Oublier les identifiants, l'autorisation de ce poste et l'inventaire
        // du serveur.
        _settings.ProtectedPassword = null;
        _settings.RememberCredentials = false;
        _settings.ClearDeviceTrust();   // écrit immédiatement

        ClearDeviceInventory();

        PassBox.Clear();
        TokenBox.Clear();
        RememberCheck.IsChecked = false;
        SubtitleText.Text = _settings.Host;
        UpdateTrayTooltip();

        ShowLogin();
        SetStatus("Vous êtes déconnecté.");
    }

    // MARK: - Diagnostic

    /// <summary>
    /// Aperçu immédiat : on applique sans attendre l'enregistrement, pour que le
    /// choix se juge à l'œil. La valeur est persistée par « Enregistrer ».
    /// </summary>
    private void DensityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Déclenché pendant InitializeComponent (IsSelected="True" en XAML) :
        // _settings existe déjà, mais l'aperçu n'a de sens qu'une fois chargé.
        if (!IsLoaded) return;

        var previous = _settings.ListDensity;
        _settings.ListDensity = Math.Clamp(DensityCombo.SelectedIndex, 0, 2);
        if (_settings.ListDensity != previous) ApplyListDensity();
    }

    // MARK: - Thème

    /// <summary>
    /// Enregistré tout de suite, sans attendre « Enregistrer » : le choix ne
    /// prend effet qu'au redémarrage, que l'on peut lancer d'ici même.
    /// </summary>
    private void DarkMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        _settings.DarkMode = DarkModeCheck.IsChecked == true;
        _settings.Save();
        UpdateThemeRestartHint();
    }

    /// <summary>Propose le redémarrage tant que le thème affiché n'est pas celui choisi.</summary>
    private void UpdateThemeRestartHint()
    {
        var pending = _settings.DarkMode != Palette.IsDark;
        RestartForThemeButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        DarkModeHint.Text = pending
            ? "Le nouveau thème s'appliquera au prochain démarrage de l'application."
            : "Pour toutes les fenêtres de l'application. S'applique au prochain démarrage.";
    }

    /// <summary>
    /// Relance l'application pour appliquer le thème. Les couleurs sont figées
    /// au chargement de chaque fenêtre : seul un nouveau démarrage les change
    /// toutes d'un coup.
    /// </summary>
    private void RestartForTheme_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current.Windows.OfType<Window>().Any(w => w != this && w.IsVisible)
            && MessageBox.Show("Les sessions et fenêtres ouvertes seront fermées. Redémarrer maintenant ?",
                   "Mesh Control", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        if (Environment.ProcessPath is not { } exe) return;

        _settings.SaveNow();
        // Le raccourci global est libéré avant le départ de la nouvelle
        // instance : elle le trouverait sinon encore pris.
        _hotkey?.Dispose();
        _hotkey = null;

        try
        {
            // --show : la fenêtre doit réapparaître, même si le démarrage réduit est activé.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--show")
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Log.Warning($"Redémarrage impossible : {ex.Message}", "Réglages");
            MessageBox.Show($"Le redémarrage a échoué : {ex.Message}\n\nRelancez l'application à la main.",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _exiting = true;
        Close();
    }

    // MARK: - Densité de la liste

    /// <summary>
    /// Applique la densité choisie en remplaçant les ressources de gabarit. Les
    /// lignes s'y réfèrent en DynamicResource : le changement est immédiat, sans
    /// reconstruction de la liste ni perte du défilement.
    /// </summary>
    private void ApplyListDensity()
    {
        // (padding de ligne, taille de pastille, taille du nom)
        var (padding, icon, nameSize) = _settings.ListDensity switch
        {
            0 => (new Thickness(8, 4, 8, 4), 26.0, 12.0),   // compact
            2 => (new Thickness(12, 13, 12, 13), 42.0, 14.0), // confortable
            _ => (new Thickness(10, 9, 10, 9), 36.0, 13.0),   // normal
        };

        var resources = Application.Current.Resources;
        resources["DeviceRowPadding"] = padding;
        resources["DeviceIconSize"] = icon;
        resources["DeviceNameSize"] = nameSize;
    }

    /// <summary>
    /// Recherche manuelle : on repropose même une version précédemment refusée,
    /// et on confirme explicitement quand tout est à jour.
    /// </summary>
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.IsEnabled = false;
            try { await CheckForUpdatesAsync(manual: true); }
            finally { button.IsEnabled = true; }
            return;
        }
        await CheckForUpdatesAsync(manual: true);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Log.Folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Ouverture du dossier des journaux impossible", ex, "Réglages");
            ShowSettingsFeedback($"Dossier : {Log.Folder}");
        }
    }
}
