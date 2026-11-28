using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Réglages repris de la version Windows (UI/MainWindow.Settings.cs) : bureau
/// par défaut, raccourci de recherche rapide, autorisation de ce poste (second
/// facteur), journal détaillé, redémarrage pour le thème.
/// </summary>
public partial class MainWindow
{
    private static readonly int[] ScaleTags = [100, 75, 50, 33];

    // MARK: - Chargement et enregistrement

    private void LoadMoreSettings()
    {
        DefaultScaleCombo.SelectedIndex = Math.Max(0, Array.IndexOf(ScaleTags, Settings.DefaultDesktopScale));
        DefaultFitCheck.IsChecked = Settings.DefaultDesktopFit;
        DesktopOpenModeCombo.SelectedIndex = Math.Clamp(Settings.DesktopOpenMode, 0, 2);

        QuickSearchCheck.IsChecked = Settings.QuickSearchEnabled;
        HotkeyBox.Text = CurrentHotkeyCombo.Display;
        HotkeyBox.IsEnabled = Settings.QuickSearchEnabled;
        HotkeyWarning.IsVisible = false;
        HotkeyHint.Text = OperatingSystem.IsMacOS()
            ? "Cliquez puis pressez la combinaison voulue (avec ⌃, ⌥, ⇧ ou ⌘)."
            : "Cliquez puis pressez la combinaison voulue (avec Ctrl, Alt, Maj ou Win).";
        _pendingHotkey = null;

        VerboseLogCheck.IsChecked = Settings.VerboseLogging;
        UpdateTrustStateUi();
        UpdateThemeRestartHint();
    }

    /// <summary>Vrai si le raccourci a changé et doit être réenregistré.</summary>
    private void SaveMoreSettings()
    {
        if (DefaultScaleCombo.SelectedIndex >= 0) Settings.DefaultDesktopScale = ScaleTags[DefaultScaleCombo.SelectedIndex];
        Settings.DefaultDesktopFit = DefaultFitCheck.IsChecked == true;
        Settings.DesktopOpenMode = Math.Max(0, DesktopOpenModeCombo.SelectedIndex);

        var enabled = QuickSearchCheck.IsChecked == true;
        var combo = _pendingHotkey ?? CurrentHotkeyCombo;
        var hotkeyChanged = enabled != Settings.QuickSearchEnabled || combo.ToString() != Settings.QuickSearchHotkey;
        Settings.QuickSearchEnabled = enabled;
        Settings.QuickSearchHotkey = combo.ToString();
        if (hotkeyChanged) ApplyHotkey();

        Settings.VerboseLogging = VerboseLogCheck.IsChecked == true;
        Log.MinimumLevel = Settings.VerboseLogging ? LogLevel.Debug : LogLevel.Info;
    }

    // MARK: - Raccourci de recherche rapide

    /// <summary>Combinaison choisie dans les réglages, ou celle par défaut.</summary>
    private static HotkeyCombo CurrentHotkeyCombo => HotkeyCombo.Parse(App.Settings.QuickSearchHotkey) ?? HotkeyCombo.Default;

    /// <summary>Libellé du raccourci actif, pour les rappels (barre des menus, notifications).</summary>
    private string HotkeyLabel => _hotkey?.Current?.Display ?? CurrentHotkeyCombo.Display;

    /// <summary>Combinaison saisie dans le champ, pas encore enregistrée.</summary>
    private HotkeyCombo? _pendingHotkey;

    /// <summary>(Ré)enregistre le raccourci global selon les réglages.</summary>
    private void ApplyHotkey()
    {
        if (_hotkey is null) return;
        _hotkey.Unregister();
        if (Settings.QuickSearchEnabled && !_hotkey.Register(CurrentHotkeyCombo))
        {
            // Presque toujours un conflit avec une autre application : on le
            // signale sans bloquer le reste de l'application.
            ShowToast($"Raccourci {CurrentHotkeyCombo.Display} indisponible : déjà utilisé par une autre application.");
        }
        HotkeyHintBadge.IsVisible = _hotkey.IsRegistered;
        HotkeyHintText.Text = HotkeyLabel;
        UpdateTrayMenu();
    }

    private void QuickSearch_Changed(object? sender, RoutedEventArgs e)
    {
        if (HotkeyBox is null) return;   // pendant InitializeComponent
        HotkeyBox.IsEnabled = QuickSearchCheck.IsChecked == true;
    }

    private void HotkeyBox_GotFocus(object? sender, FocusChangedEventArgs e) => HotkeyBox.Text = "Pressez la combinaison…";

    private void HotkeyBox_LostFocus(object? sender, RoutedEventArgs e)
        => HotkeyBox.Text = (_pendingHotkey ?? CurrentHotkeyCombo).Display;

    private void HotkeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;   // capturer la frappe sans la laisser agir

        // Appui d'un modificateur seul : on attend la touche finale.
        if (KeyMapping.IsModifier(e.Key) || e.Key == Key.None) return;

        if (e.KeyModifiers == KeyModifiers.None)
        {
            ShowHotkeyWarning(OperatingSystem.IsMacOS()
                ? "Ajoutez au moins un modificateur (⌃, ⌥, ⇧ ou ⌘) : sans cela, la touche serait capturée dans toutes les applications."
                : "Ajoutez au moins un modificateur (Ctrl, Alt, Maj ou Win) : sans cela, la touche serait capturée dans toutes les applications.");
            return;
        }
        if (HotkeyCombo.MacKeyCode(e.Key) is null || HotkeyCombo.VirtualKey(e.Key) == 0)
        {
            ShowHotkeyWarning("Choisissez une lettre, un chiffre, une touche de fonction ou Espace.");
            return;
        }

        _pendingHotkey = new HotkeyCombo(e.KeyModifiers, e.Key);
        HotkeyBox.Text = _pendingHotkey.Value.Display;
        HotkeyWarning.IsVisible = false;
    }

    private void ShowHotkeyWarning(string message)
    {
        HotkeyWarning.Text = message;
        HotkeyWarning.IsVisible = true;
    }

    // MARK: - Autorisation de ce poste (second facteur)

    private static string TokenUserAccount(string host, string user) => $"{user}@{host}#token-user";
    private static string TokenPassAccount(string host, string user) => $"{user}@{host}#token-pass";

    /// <summary>Jeton de connexion de ce poste, s'il en a un.</summary>
    private static (string User, string Pass)? StoredLoginToken(string host, string user)
    {
        var tokenUser = Keychain.Read(TokenUserAccount(host, user));
        var tokenPass = Keychain.Read(TokenPassAccount(host, user));
        return string.IsNullOrEmpty(tokenUser) || string.IsNullOrEmpty(tokenPass) ? null : (tokenUser, tokenPass);
    }

    private static void ClearLoginToken(string host, string user)
    {
        Keychain.Delete(TokenUserAccount(host, user));
        Keychain.Delete(TokenPassAccount(host, user));
        App.Settings.LoginTokenName = null;
        App.Settings.Save();
    }

    /// <summary>
    /// Identifiants de connexion. Un mot de passe tapé à l'instant l'emporte sur
    /// le jeton : sans cela, posséder un jeton interdirait toute autre
    /// connexion. La reconnexion automatique, elle, préfère le jeton : elle n'a
    /// personne pour saisir un code si le serveur en réclamait un.
    /// </summary>
    private static MeshLogin BuildLogin(string host, string user, string password, string? token, bool manual)
    {
        bool passwordWins = manual && !string.IsNullOrEmpty(password);
        if (string.IsNullOrEmpty(token) && !passwordWins && StoredLoginToken(host, user) is { } stored)
            return new MeshLogin { Password = stored.Pass, LoginTokenUser = stored.User };

        return new MeshLogin
        {
            Password = password,
            TwoFactorToken = token,
            TwoFactorCookie = string.IsNullOrEmpty(token) ? Keychain.Read(CookieAccount(host, user)) : null,
        };
    }

    /// <summary>Connexion, avec retour aux identifiants du compte si le jeton est refusé.</summary>
    private static async Task ConnectWithLoginAsync(MeshServerConnection connection, string host, string user,
                                                    string password, string? token, bool manual)
    {
        var login = BuildLogin(host, user, password, token, manual);
        try
        {
            await connection.ConnectAsync(login);
        }
        catch (MeshException) when (login.LoginTokenUser is not null && !string.IsNullOrEmpty(password))
        {
            // Jeton révoqué ou expiré côté serveur : l'oublier et repartir des
            // identifiants du compte, quitte à redemander un code.
            Log.Warning("Jeton de connexion refusé par le serveur : retour aux identifiants du compte.", "Serveur");
            ClearLoginToken(host, user);
            await connection.ConnectAsync(BuildLogin(host, user, password, token, manual));
        }
    }

    /// <summary>
    /// État de l'autorisation : jeton de connexion, mémorisation, ou rien. Les
    /// boutons ne s'affichent que lorsqu'ils peuvent aboutir — créer un jeton
    /// suppose une session ouverte avec le compte.
    /// </summary>
    private void UpdateTrustStateUi()
    {
        var host = Settings.Host;
        var user = Settings.Username;
        var canCreate = _connection is { State: ConnectionState.Connected } && _connection.CanCreateLoginToken;

        if (StoredLoginToken(host, user) is not null)
        {
            TrustStateText.Text = string.IsNullOrEmpty(Settings.LoginTokenName)
                ? "Ce poste est autorisé sans limite de durée."
                : $"Ce poste est autorisé sans limite de durée (jeton « {Settings.LoginTokenName} »).";
            TrustDeviceButton.IsVisible = false;
            ForgetDeviceButton.IsVisible = true;
        }
        else if (!string.IsNullOrEmpty(Keychain.Read(CookieAccount(host, user))))
        {
            TrustStateText.Text = "Ce poste est mémorisé : le code n'est pas redemandé.";
            TrustDeviceButton.IsVisible = canCreate;
            ForgetDeviceButton.IsVisible = true;
        }
        else
        {
            TrustStateText.Text = "Le code à deux facteurs est demandé à chaque connexion.";
            TrustDeviceButton.IsVisible = canCreate;
            ForgetDeviceButton.IsVisible = false;
        }
    }

    /// <summary>
    /// Crée un jeton de connexion pour ce poste : le serveur acceptera ensuite
    /// l'application sans second facteur, indéfiniment.
    /// </summary>
    private async void TrustDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (_connection is not { State: ConnectionState.Connected } connection)
        {
            ShowSettingsFeedback("Connectez-vous d'abord au serveur.", isError: true);
            return;
        }
        if (!connection.CanCreateLoginToken)
        {
            ShowSettingsFeedback("Cette session est déjà ouverte avec un jeton.", isError: true);
            return;
        }

        TrustDeviceButton.IsEnabled = false;
        try
        {
            // Le nom identifie le poste dans « Mon compte » sur le serveur :
            // c'est là que l'administrateur révoquera le jeton si nécessaire.
            var token = await connection.CreateLoginTokenAsync($"Mesh Control — {Environment.MachineName}");
            if (token is null)
            {
                ShowSettingsFeedback("Le serveur refuse les jetons de connexion pour ce compte.", isError: true);
                return;
            }

            Keychain.Write(TokenUserAccount(Settings.Host, Settings.Username), token.User);
            Keychain.Write(TokenPassAccount(Settings.Host, Settings.Username), token.Password);
            Settings.LoginTokenName = token.Name;
            Settings.Save();

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
    /// quand c'est possible, les secrets locaux sont effacés dans tous les cas.
    /// </summary>
    private async void ForgetDevice_Click(object? sender, RoutedEventArgs e)
    {
        var host = Settings.Host;
        var user = Settings.Username;
        var tokenUser = StoredLoginToken(host, user)?.User;

        ClearLoginToken(host, user);
        Keychain.Delete(CookieAccount(host, user));
        UpdateTrustStateUi();
        Log.Info("Autorisation de ce poste retirée (second facteur).", "Serveur");

        if (string.IsNullOrEmpty(tokenUser))
        {
            ShowSettingsFeedback("Ce poste est oublié.");
            return;
        }

        // La révocation peut être refusée en silence : c'est la réponse du
        // serveur qui dit si le jeton est bien mort.
        if (_connection is { State: ConnectionState.Connected } connection && await connection.RevokeLoginTokenAsync(tokenUser))
        {
            ShowSettingsFeedback("Ce poste est oublié, le jeton est révoqué.");
            return;
        }
        ShowSettingsFeedback("Ce poste est oublié. Le jeton reste à révoquer depuis « Mon compte » sur le serveur.");
    }

    private void ShowSettingsFeedback(string message, bool isError = false)
    {
        SettingsFeedback.Text = message;
        SettingsFeedback.Foreground = Palette.Resource(isError ? "DangerBrush" : "TextSecondaryBrush");
    }

    // MARK: - Thème

    /// <summary>Propose le redémarrage tant que le thème affiché n'est pas celui choisi.</summary>
    private void UpdateThemeRestartHint()
    {
        var pending = (DarkModeCheck.IsChecked == true) != Palette.IsDark;
        RestartForThemeButton.IsVisible = pending;
        DarkModeHint.Text = pending
            ? "Le nouveau thème s'appliquera au prochain démarrage de l'application."
            : "Pour toutes les fenêtres de l'application. S'applique au prochain démarrage.";
    }

    /// <summary>
    /// Relance l'application pour appliquer le thème : les couleurs sont figées
    /// au chargement de chaque fenêtre, seul un nouveau démarrage les change toutes.
    /// </summary>
    private async void RestartForTheme_Click(object? sender, RoutedEventArgs e)
    {
        var others = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life
                     && life.Windows.Any(w => w != this && w.IsVisible);
        if (others && !await Dialogs.ConfirmAsync(this, "Redémarrer maintenant ?",
                "Les sessions et fenêtres ouvertes seront fermées.", "Redémarrer", danger: false))
            return;

        Settings.DarkMode = DarkModeCheck.IsChecked == true;
        Settings.Save();

        // Le raccourci global est libéré avant le départ de la nouvelle
        // instance : elle le trouverait sinon encore pris.
        _hotkey?.Dispose();

        try
        {
            if (OperatingSystem.IsMacOS() && AppBundlePath() is { } bundle)
                // -n : une nouvelle instance, même si celle-ci tourne encore.
                Process.Start("open", ["-n", bundle, "--args", "--show"]);
            else if (Environment.ProcessPath is { } exe)
                Process.Start(new ProcessStartInfo(exe, "--show") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warning($"Redémarrage impossible : {ex.Message}", "Réglages");
            await Dialogs.InformAsync(this, $"Le redémarrage a échoué : {ex.Message}\n\nRelancez l'application à la main.");
            return;
        }

        _exiting = true;
        Close();
    }

    /// <summary>Dossier « Mesh Control.app » de l'application, sous macOS.</summary>
    private static string? AppBundlePath()
    {
        var path = Environment.ProcessPath;
        var index = path?.IndexOf(".app/", StringComparison.Ordinal) ?? -1;
        return index > 0 ? path![..(index + 4)] : null;
    }
}
