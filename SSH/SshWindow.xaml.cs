using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MeshControl.Core;

namespace MeshControl.Ssh;

public partial class SshWindow : MeshControl.UI.ChromeWindow, MeshControl.UI.ISessionWindow
{
    /// <summary>Empêche un double démarrage (Loaded et hôte d'onglet).</summary>
    private bool _sessionStarted;

    /// <summary>Démarre la session ; sans effet si elle l'est déjà.</summary>
    public async Task StartSessionAsync()
    {
        if (_sessionStarted) return;
        _sessionStarted = true;
        await _session.StartAsync();
    }

    private readonly SshSession _session;
    private readonly TerminalControl _terminal;
    private readonly string _deviceName;
    private bool _uiReady;

    public SshWindow(MeshServerConnection connection, MeshNode node)
    {
        _deviceName = node.Name;
        _session = new SshSession(connection, node, Dispatcher);

        InitializeComponent();
        _uiReady = true;

        TitleText.Text = $"{node.Name} — SSH";
        SetStatus("Connexion…", "#EFEFF4", "#8A8A8E");

        // Le terminal est un contrôle dessiné à la main : on l'ajoute par code.
        _terminal = new TerminalControl();
        _terminal.SetFontSize(AppSettings.Load().TerminalFontSize);
        _terminal.GridSizeChanged += (cols, rows) => _session.Resize(cols, rows);
        _terminal.PasteRequested += () => Paste_Click(this, new RoutedEventArgs());
        // Abonnement après la taille initiale : avant, on réenregistrerait la
        // valeur qu'on vient tout juste de lire.
        _terminal.FontSizeChanged += SaveFontSize;
        TerminalHost.Child = _terminal;
        SearchBar.Attach(_terminal);

        _fullscreen = new UI.SessionFullscreen(RootPanel, FullscreenHint, Toolbar);
        _fullscreen.Changed += OnFullscreenChanged;
        // En mode onglets, une autre session a pu changer l'état de la fenêtre
        // hôte pendant que celle-ci était en arrière-plan.
        RootPanel.IsVisibleChanged += (_, _) => _fullscreen.Sync();
        // Fermer en plein écran laisserait la fenêtre hôte sans bordure ni
        // bandeau d'onglets, y compris pour les autres sessions.
        Closed += (_, _) => _fullscreen.Exit();

        // Frappes interceptées sur le CONTENU (et non la fenêtre) : en mode
        // onglets, la fenêtre porteuse n'est jamais affichée et ne recevrait
        // aucun événement clavier. Les champs d'authentification restent
        // prioritaires (voir InputFieldHasFocus).
        RootPanel.PreviewKeyDown += Terminal_PreviewKeyDown;
        RootPanel.PreviewTextInput += Terminal_PreviewTextInput;

        _session.Output += OnOutput;
        _session.StateChanged += OnStateChanged;

        // En mode onglets, la fenêtre n'est jamais affichée : Loaded ne se
        // déclenche pas, l'hôte appelle StartSessionAsync() explicitement.
        Loaded += async (_, _) =>
        {
            _terminal.Focus();
            await StartSessionAsync();
        };
        Closed += (_, _) => _session.Stop();
    }

    // MARK: - Session

    private void OnOutput(string text)
    {
        _terminal.Write(text);
    }

    private void OnStateChanged()
    {
        if (!_uiReady) return;

        switch (_session.State)
        {
            case SshSessionState.Connecting:
                SetStatus("Connexion…", "#EFEFF4", "#8A8A8E");
                AuthPanel.Visibility = Visibility.Collapsed;
                break;

            case SshSessionState.NeedsCredentials:
                SetStatus("Authentification requise", "#FFF4E5", "#B25E09");
                ShowAuthPanel();
                break;

            case SshSessionState.Authenticating:
                SetStatus("Authentification…", "#EFEFF4", "#8A8A8E");
                AuthPanel.Visibility = Visibility.Collapsed;
                break;

            case SshSessionState.Connected:
                SetStatus("Connecté", "#E4F8E8", "#248A3D");
                AuthPanel.Visibility = Visibility.Collapsed;
                // Rejouer la sortie arrivée avant l'abonnement.
                var pending = _session.DrainPendingOutput();
                if (!string.IsNullOrEmpty(pending)) _terminal.Write(pending);
                _terminal.Focus();
                break;

            case SshSessionState.Closed:
                SetStatus("Session fermée", "#FDE8E7", "#C0392B");
                AuthPanel.Visibility = Visibility.Collapsed;
                if (!string.IsNullOrEmpty(_session.CloseReason))
                    _terminal.Write($"\r\n\u001B[31m*** {_session.CloseReason}\u001B[0m\r\n");
                break;
        }
    }

    private void ShowAuthPanel()
    {
        AuthPanel.Visibility = Visibility.Visible;

        // Cas « clé stockée » : seule la phrase de passe est demandée.
        if (_session.KeyPassOnly)
        {
            AuthTitle.Text = "Phrase de passe de la clé SSH";
            UsernameGroup.Visibility = Visibility.Collapsed;
            PasswordLabel.Text = "Phrase de passe";
            RememberCheck.Visibility = Visibility.Collapsed;
        }
        else
        {
            AuthTitle.Text = "Identifiants SSH";
            UsernameGroup.Visibility = Visibility.Visible;
            PasswordLabel.Text = "Mot de passe";
            RememberCheck.Visibility = Visibility.Visible;
        }

        if (_session.AuthError is { } error)
        {
            AuthError.Text = error;
            AuthError.Visibility = Visibility.Visible;
        }
        else
        {
            AuthError.Visibility = Visibility.Collapsed;
        }

        PassBox.Clear();
        if (_session.KeyPassOnly) PassBox.Focus();
        else if (string.IsNullOrEmpty(UserBox.Text)) UserBox.Focus();
        else PassBox.Focus();
    }

    private void SetStatus(string text, string background, string foreground)
    {
        StatusLabel.Text = text;
        StatusBadge.Background = MakeBrush(background);
        StatusLabel.Foreground = MakeBrush(foreground);
    }

    private static SolidColorBrush MakeBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(MeshControl.UI.Palette.Adapt(hex)));

    // MARK: - Authentification

    private void AuthField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Authenticate_Click(sender, e);
    }

    private void Authenticate_Click(object sender, RoutedEventArgs e)
    {
        if (_session.KeyPassOnly)
        {
            _session.SubmitKeyPassphrase(PassBox.Password);
            return;
        }

        var user = UserBox.Text.Trim();
        if (string.IsNullOrEmpty(user))
        {
            AuthError.Text = "Renseignez un nom d'utilisateur.";
            AuthError.Visibility = Visibility.Visible;
            UserBox.Focus();
            return;
        }

        _session.SubmitCredentials(user, PassBox.Password,
            RememberCheck.IsChecked == true);
    }

    // MARK: - Saisie clavier -> séquences ANSI

    /// <summary>
    /// Les caractères imprimables passent par PreviewTextInput : WPF a déjà
    /// appliqué la disposition clavier, donc les accents arrivent correctement.
    /// </summary>
    /// <summary>
    /// Vrai quand un champ de saisie (authentification, barre d'outils) a le
    /// focus : ses frappes lui appartiennent et ne partent pas vers le shell.
    /// Indispensable ici, le bandeau contenant un champ mot de passe.
    /// </summary>
    private bool InputFieldHasFocus =>
        Keyboard.FocusedElement is TextBox or PasswordBox or Button or CheckBox;

    private void Terminal_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_session.State != SshSessionState.Connected) return;
        if (InputFieldHasFocus) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        _session.SendInput(e.Text);
        e.Handled = true;
    }

    /// <summary>
    /// Les touches non imprimables (flèches, Ctrl+lettre, Tab, F1-F12…) sont
    /// converties en séquences d'échappement ANSI attendues par le shell.
    /// </summary>
    private void Terminal_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (InputFieldHasFocus) return;

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // Ces trois-là passent avant le test d'état : relire, copier et
        // rechercher dans la sortie d'une session fermée est précisément ce
        // qu'on vient y faire.
        if (ctrl && shift && e.Key == Key.C)
        {
            CopyAll_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (ctrl && shift && e.Key == Key.F)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (ctrl && shift && e.Key == Key.R)
        {
            SearchBar.Toggle();
            e.Handled = true;
            return;
        }

        // Défilement de l'historique, lui aussi indépendant de l'état de la
        // session. Toute autre frappe ramène la vue en bas.
        if (_terminal.HandleViewKey(e.Key, Keyboard.Modifiers))
        {
            e.Handled = true;
            return;
        }

        if (_session.State != SshSessionState.Connected) return;

        // Ctrl+Maj+V : coller localement, ne pas transmettre.
        if (ctrl && shift && e.Key == Key.V)
        {
            Paste_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+lettre : caractères de contrôle 0x01-0x1A (Ctrl+C = 0x03, etc.).
        if (ctrl && !shift && TerminalKeys.MapControlChar(e.Key) is { } control)
        {
            _session.SendInput(control);
            e.Handled = true;
            return;
        }

        if (TerminalKeys.MapSpecialKey(e.Key) is not { } sequence) return;

        _session.SendInput(sequence);
        e.Handled = true;
    }

    // Les séquences ANSI sont factorisées dans TerminalKeys, partagé avec le
    // terminal via agent : les deux parlent le même langage côté clavier.

    // MARK: - Presse-papiers

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (_session.State != SshSessionState.Connected) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text)) return;
            // Le terminal reçoit le texte tel quel : contrairement au bureau à
            // distance, il n'y a pas de simulation de frappe à cadencer.
            _session.SendInput(text.Replace("\r\n", "\r").Replace("\n", "\r"));
        }
        catch { /* presse-papiers verrouillé par une autre application */ }

        // Le terminal reprend la main : après une action de la barre d'outils,
        // on veut taper, pas rester accroché au contrôle qu'on vient d'utiliser.
        _terminal.Focus();
    }

    /// <summary>
    /// Copie la sélection si elle existe, tout le terminal — historique compris
    /// — sinon. La sélection est levée dans la foulée : elle a servi, et la
    /// laisser en vidéo inverse ferait croire qu'il reste quelque chose à faire.
    /// </summary>
    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        var text = _terminal.TextForCopy();
        if (string.IsNullOrEmpty(text)) return;

        try { Clipboard.SetText(text); }
        catch { /* idem */ }
        _terminal.ClearSelection();
        _terminal.Focus();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _terminal.Clear();
        _terminal.Focus();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => SearchBar.Toggle();

    // MARK: - Plein écran

    /// <summary>
    /// Plein écran de cette session, mécanisme partagé avec le bureau à
    /// distance : il porte sur la fenêtre hôte, qui n'est pas celle-ci en mode
    /// onglets. Le bandeau d'authentification, lui, n'est jamais masqué — on ne
    /// peut pas demander des identifiants sans les montrer.
    /// </summary>
    private readonly UI.SessionFullscreen _fullscreen;

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        _fullscreen.Toggle();
        // Le clavier doit revenir au shell après la bascule : le changement de
        // style de fenêtre fait perdre le focus.
        _terminal.Focus();
    }

    private void OnFullscreenChanged(bool fullscreen) =>
        FullscreenButton.ToolTip = fullscreen
            ? "Quitter le plein écran (Ctrl+Maj+F)"
            : "Plein écran (Ctrl+Maj+F)";

    /// <summary>
    /// Enregistre la taille de police choisie au zoom. Les réglages sont relus
    /// juste avant : une autre fenêtre a pu en modifier d'autres depuis
    /// l'ouverture de ce terminal, et réécrire notre copie les perdrait.
    /// </summary>
    private static void SaveFontSize(double size)
    {
        var settings = AppSettings.Load();
        settings.TerminalFontSize = (int)size;
        settings.Save();
    }
}
