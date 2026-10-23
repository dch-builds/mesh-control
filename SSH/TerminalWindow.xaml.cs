using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeshControl.Core;

namespace MeshControl.Ssh;

/// <summary>
/// Terminal passant par l'agent MeshCentral. Contrairement à SSH, aucune
/// configuration serveur n'est nécessaire — c'est l'équivalent de l'onglet
/// « Terminal » de l'interface web, avec le choix du shell.
/// </summary>
public partial class TerminalWindow : MeshControl.UI.ChromeWindow, MeshControl.UI.ISessionWindow
{
    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;
    private readonly TerminalControl _terminal;
    private AgentTerminalSession? _session;
    private bool _uiReady;

    public TerminalWindow(MeshServerConnection connection, MeshNode node)
    {
        _connection = connection;
        _node = node;

        InitializeComponent();

        TitleText.Text = $"{node.Name} — Terminal";
        SetStatus("Connexion…", "#EFEFF4", "#8A8A8E");

        _terminal = new TerminalControl();
        _terminal.SetFontSize(AppSettings.Load().TerminalFontSize);
        _terminal.GridSizeChanged += (cols, rows) => _session?.Resize(cols, rows);
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

        // Les frappes sont interceptées sur le CONTENU (et non la fenêtre) :
        // en mode onglets, la fenêtre porteuse n'est jamais affichée et ne
        // recevrait aucun événement clavier. Le contenu, lui, migre dans
        // l'onglet. La barre d'outils reste utilisable car on ignore les frappes
        // quand un de ses champs a le focus.
        RootPanel.PreviewKeyDown += Terminal_PreviewKeyDown;
        RootPanel.PreviewTextInput += Terminal_PreviewTextInput;

        // Les shells disponibles dépendent du système de la machine.
        PopulateShellChoices();
        _uiReady = true;

        // En mode onglets, la fenêtre n'est jamais affichée : Loaded ne se
        // déclenche pas, l'hôte appelle l'interface explicitement.
        Loaded += async (_, _) =>
        {
            _terminal.Focus();
            await ((MeshControl.UI.ISessionWindow)this).StartSessionAsync();
        };
        Closed += (_, _) => _session?.Stop();
    }

    /// <summary>
    /// Sous Windows, l'agent expose cmd, PowerShell et un shell utilisateur.
    /// Sous Linux, seul le shell par défaut a du sens.
    /// </summary>
    private void PopulateShellChoices()
    {
        ShellCombo.Items.Clear();

        // Les choix reprennent exactement ceux du menu terminal de l'interface
        // web MeshCentral (termShellContextMenu / termShellContextMenuLinux).
        if (_node.OsFamily == OSFamily.Windows)
        {
            ShellCombo.Items.Add(new ComboBoxItem
            {
                Content = "Admin Shell", Tag = ShellKind.Default,
                ToolTip = "Invite de commandes avec les droits de l'agent (NT AUTHORITY\\SYSTEM)",
            });
            ShellCombo.Items.Add(new ComboBoxItem
            {
                Content = "Admin PowerShell", Tag = ShellKind.PowerShell,
                ToolTip = "PowerShell avec les droits de l'agent (NT AUTHORITY\\SYSTEM)",
            });
            ShellCombo.Items.Add(new ComboBoxItem
            {
                Content = "User Shell", Tag = ShellKind.UserShell,
                ToolTip = "Invite de commandes dans le contexte de l'utilisateur connecté",
            });
            ShellCombo.Items.Add(new ComboBoxItem
            {
                Content = "User PowerShell", Tag = ShellKind.UserPowerShell,
                ToolTip = "PowerShell dans le contexte de l'utilisateur connecté",
            });
        }
        else
        {
            ShellCombo.Items.Add(new ComboBoxItem
            {
                Content = "Root Shell", Tag = ShellKind.Default,
                ToolTip = "Shell avec les droits de l'agent (root)",
            });
            ShellCombo.Items.Add(new ComboBoxItem
            {
                Content = "User Shell", Tag = ShellKind.UserShell,
                ToolTip = "Shell dans le contexte de l'utilisateur connecté",
            });
            ShellCombo.Items.Add(new ComboBoxItem
            {
                Content = "Login Shell", Tag = ShellKind.LoginShell,
                ToolTip = "Invite de connexion (login/mot de passe) dans la console, "
                        + "comme sur une console physique",
            });
        }

        ShellCombo.SelectedIndex = 0;
    }

    private ShellKind SelectedShell =>
        (ShellCombo.SelectedItem as ComboBoxItem)?.Tag as ShellKind? ?? ShellKind.Default;

    // MARK: - Session

    /// <summary>Empêche un double démarrage (Loaded et hôte d'onglet).</summary>
    private bool _sessionStarted;

    /// <summary>
    /// Démarrage initial demandé par l'hôte d'onglet. Distinct de
    /// StartSessionAsync, qui relance délibérément la session lors d'un
    /// changement de shell ou d'une reconnexion.
    /// </summary>
    async Task MeshControl.UI.ISessionWindow.StartSessionAsync()
    {
        if (_sessionStarted) return;
        _sessionStarted = true;
        await StartSessionAsync();
    }

    private async Task StartSessionAsync()
    {
        // Fermer une session précédente (changement de shell, reconnexion).
        _session?.Stop();
        _session = new AgentTerminalSession(_connection, _node, SelectedShell, Dispatcher);
        _session.Output += OnOutput;
        _session.StateChanged += OnStateChanged;

        // La taille courante du terminal doit être transmise dès l'ouverture.
        _session.Resize(_terminal.Emulator.Cols, _terminal.Emulator.Rows);
        await _session.StartAsync();
    }

    private void OnOutput(string text) => _terminal.Write(text);

    private void OnStateChanged()
    {
        if (!_uiReady || _session is null) return;

        switch (_session.State)
        {
            case SshSessionState.Connecting:
                SetStatus("Connexion…", "#EFEFF4", "#8A8A8E");
                break;

            case SshSessionState.Connected:
                SetStatus("Connecté", "#E4F8E8", "#248A3D");
                var pending = _session.DrainPendingOutput();
                if (!string.IsNullOrEmpty(pending)) _terminal.Write(pending);
                _terminal.Focus();
                ShowExpectedIdentity();
                break;

            case SshSessionState.Closed:
                SetStatus("Session fermée", "#FDE8E7", "#C0392B");
                if (!string.IsNullOrEmpty(_session.CloseReason))
                    _terminal.Write($"\r\n\u001B[31m*** {_session.CloseReason}\u001B[0m\r\n");
                break;
        }
    }

    private void SetStatus(string text, string background, string foreground)
    {
        StatusLabel.Text = text;
        StatusBadge.Background = MakeBrush(background);
        StatusLabel.Foreground = MakeBrush(foreground);
    }

    /// <summary>
    /// Indique le contexte de droits attendu selon le protocole utilisé. Tapez
    /// « whoami » dans le terminal pour le confirmer côté machine.
    /// </summary>
    private void ShowExpectedIdentity()
    {
        var (label, isPrivileged) = SelectedShell switch
        {
            ShellKind.UserShell or ShellKind.UserPowerShell => ("utilisateur connecté", false),
            ShellKind.LoginShell => ("connexion manuelle", false),
            _ => (_node.OsFamily == OSFamily.Windows ? "SYSTEM" : "root", true),
        };

        IdentityText.Text = $"⚡ {label}";
        IdentityBadge.Background = MakeBrush(isPrivileged ? "#FFF4E5" : "#EFEFF4");
        IdentityText.Foreground = MakeBrush(isPrivileged ? "#B25E09" : "#8A8A8E");
        IdentityBadge.Visibility = Visibility.Visible;
    }

    private static SolidColorBrush MakeBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    // MARK: - Barre d'outils

    private async void ShellCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Ce handler se déclenche aussi pendant l'initialisation du XAML.
        if (!_uiReady) return;
        IdentityBadge.Visibility = Visibility.Collapsed;
        _terminal.Clear();
        await StartSessionAsync();
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        IdentityBadge.Visibility = Visibility.Collapsed;
        _terminal.Clear();
        await StartSessionAsync();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => SearchBar.Toggle();

    // MARK: - Plein écran

    /// <summary>
    /// Plein écran de cette session, mécanisme partagé avec le bureau à
    /// distance : il porte sur la fenêtre hôte, qui n'est pas celle-ci en mode
    /// onglets.
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

    // MARK: - Clavier

    /// <summary>
    /// Vrai quand un champ de l'interface — barre d'outils, recherche — a le
    /// focus : les frappes lui appartiennent alors et ne doivent pas partir
    /// vers le shell.
    /// </summary>
    private bool ToolbarHasFocus =>
        Keyboard.FocusedElement is DependencyObject focused
        && focused is not TerminalControl
        && (focused is ComboBox or ComboBoxItem or Button or TextBox);

    private void Terminal_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_session?.State != SshSessionState.Connected) return;
        if (ToolbarHasFocus) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        _session.SendInput(e.Text);
        e.Handled = true;
    }

    private void Terminal_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ToolbarHasFocus) return;

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

        if (_session?.State != SshSessionState.Connected) return;

        // Raccourci local du terminal : ne pas transmettre au shell.
        if (ctrl && shift && e.Key == Key.V)
        {
            Paste_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+lettre : caractère de contrôle (Ctrl+C interrompt, etc.).
        if (ctrl && !shift && TerminalKeys.MapControlChar(e.Key) is { } control)
        {
            _session.SendInput(control);
            e.Handled = true;
            return;
        }

        if (TerminalKeys.MapSpecialKey(e.Key) is { } sequence)
        {
            _session.SendInput(sequence);
            e.Handled = true;
        }
        // Sinon : caractère imprimable, laissé à PreviewTextInput.
    }

    // MARK: - Presse-papiers

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.State != SshSessionState.Connected) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text)) return;
            // Les fins de ligne Windows deviennent un simple retour chariot,
            // sinon le shell verrait une ligne vide après chaque commande.
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
}
