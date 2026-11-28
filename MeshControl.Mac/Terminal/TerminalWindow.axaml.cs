using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeshControl.Core;
using MeshControl.Ssh;

namespace MeshControl.Mac;

/// <summary>
/// Terminal d'une machine — portage de SSH/TerminalWindow.xaml.cs (par
/// l'agent, avec le choix du shell) et SSH/SshWindow.xaml.cs (par le relais
/// SSH du serveur, avec ses identifiants). Une seule fenêtre pour les deux :
/// leur barre d'outils et leur clavier sont les mêmes.
///
/// Clavier : Ctrl+lettre envoie le caractère de contrôle (Ctrl+C interrompt),
/// comme partout. Sur Mac, Cmd garde son sens habituel : Cmd+C copie,
/// Cmd+V colle, Cmd+F cherche, Cmd+K efface, Cmd+± zoome. Les raccourcis
/// Ctrl+Maj+… de la version Windows restent disponibles.
/// </summary>
public partial class TerminalWindow : ChromeWindow, ISessionWindow
{
    public enum Mode { Agent, Ssh }

    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;
    private readonly Mode _mode;
    private readonly TerminalControl _terminal;
    private readonly bool _uiReady;

    private AgentTerminalSession? _agent;
    private SshSession? _ssh;

    /// <summary>Ouvre le bureau à distance de la machine (fourni par la fenêtre principale).</summary>
    public Action? OpenDesktop { get; init; }

    /// <summary>Crée un bureau pour la machine (fourni par la fenêtre principale), pour la vignette.</summary>
    public Func<DesktopWindow>? CreateDesktop { get; init; }

    /// <summary>Ouvre un bureau en onglet ou en fenêtre, selon la préférence (fourni par la fenêtre principale).</summary>
    public Action<DesktopWindow>? ShowDesktop { get; init; }

    public string NodeId => _node.Id;
    public Mode SessionMode => _mode;

    public TerminalWindow(MeshServerConnection connection, MeshNode node, Mode mode)
    {
        _connection = connection;
        _node = node;
        _mode = mode;

        InitializeComponent();

        var kind = mode == Mode.Ssh ? "Terminal SSH" : "Terminal";
        Title = $"{node.Name} — {kind} — Mesh Control";
        WindowTitleBar.Title = $"{kind} — Mesh Control";
        TitleText.Text = mode == Mode.Ssh ? $"{node.Name} — SSH" : $"{node.Name} — Terminal";
        ToolTip.SetTip(TitleText, TitleText.Text);
        SetStatus("Connexion…", "#EFEFF4", "#8A8A8E");

        // Terminal par l'agent : choix du shell, commandes, reconnexion.
        // SSH : bouton Effacer, bandeau d'identifiants.
        bool agent = mode == Mode.Agent;
        ShellLabel.IsVisible = ShellCombo.IsVisible = agent;
        CommandsButton.IsVisible = agent;
        ReconnectButton.IsVisible = agent;
        ClearButton.IsVisible = !agent;

        _terminal = new TerminalControl();
        _terminal.SetFontSize(App.Settings.TerminalFontSize);
        _terminal.GridSizeChanged += (cols, rows) => Resize(cols, rows);
        _terminal.Emulator.Reply += reply => Send(reply);
        _terminal.PasteRequested += () => Paste_Click(this, new RoutedEventArgs());
        _terminal.SearchRequested += () => SearchBar.Toggle();
        if (agent)
        {
            _terminal.CommandsRequested += OpenQuickCommands;
            _terminal.ReconnectRequested += () => Reconnect_Click(this, new RoutedEventArgs());
        }
        _terminal.FontSizeChanged += size =>
        {
            App.Settings.TerminalFontSize = (int)size;
            App.Settings.Save();
        };
        _terminal.TextInput += Terminal_TextInput;
        TerminalHost.Child = _terminal;
        SearchBar.Attach(_terminal);

        // Vignette du bureau : les fonctions de la fenêtre principale sont lues
        // à l'usage, elles ne sont pas encore posées à ce stade.
        DockedDesktop.Attach(node, this, () => CreateDesktop!(), window => ShowDesktop?.Invoke(window));
        DockedDesktop.KeyboardReturned += () => _terminal.Focus();

        // Sur le contenu : en onglet, la fenêtre porteuse n'est jamais affichée.
        RootPanel.AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        Toolbar.SizeChanged += (_, _) => Dispatcher.UIThread.Post(FitToolbar, DispatcherPriority.Loaded);

        if (agent) PopulateShellChoices();
        _uiReady = true;

        Opened += async (_, _) => await ((ISessionWindow)this).StartSessionAsync();
        Closed += (_, _) => StopSession();
    }

    // MARK: - Session (agent ou SSH)

    private SshSessionState? State => _mode == Mode.Agent ? _agent?.State : _ssh?.State;
    private string? CloseReason => _mode == Mode.Agent ? _agent?.CloseReason : _ssh?.CloseReason;
    private bool IsConnected => State == SshSessionState.Connected;

    private void Send(string text)
    {
        if (_mode == Mode.Agent) _agent?.SendInput(text);
        else _ssh?.SendInput(text);
    }

    private void Resize(int cols, int rows)
    {
        if (_mode == Mode.Agent) _agent?.Resize(cols, rows);
        else _ssh?.Resize(cols, rows);
    }

    private void StopSession()
    {
        _agent?.Stop();
        _ssh?.Stop();
    }

    private bool _sessionStarted;

    /// <summary>Premier démarrage, par l'ouverture de la fenêtre ou par la fenêtre à onglets.</summary>
    async Task ISessionWindow.StartSessionAsync()
    {
        if (_sessionStarted) return;
        _terminal.Focus();
        await StartSessionAsync();
    }

    void ISessionWindow.ReleaseKeyboard() => DockedDesktop.ReleaseKeyboard();
    public TitleBar SessionTitleBar => WindowTitleBar;
    public Control FocusTarget => _terminal;

    private async Task StartSessionAsync()
    {
        _sessionStarted = true;
        if (_mode == Mode.Ssh)
        {
            if (_ssh is not null) return;
            _ssh = new SshSession(_connection, _node, AvaloniaUiDispatcher.Instance);
            _ssh.Output += OnOutput;
            _ssh.StateChanged += OnStateChanged;
            _ssh.Resize(_terminal.Emulator.Cols, _terminal.Emulator.Rows);
            await _ssh.StartAsync();
            return;
        }

        // Session précédente (changement de shell, reconnexion) : désabonnée
        // d'abord, ses dernières trames ne doivent pas s'afficher ici.
        if (_agent is { } previous)
        {
            previous.Output -= OnOutput;
            previous.StateChanged -= OnStateChanged;
            previous.Stop();
        }

        var shellName = (ShellCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "shell";
        _terminal.Write($"\u001B[90m» Connexion à {_node.Name} — {shellName}…\u001B[0m\r\n");
        SetReconnectHighlighted(false);

        _agent = new AgentTerminalSession(_connection, _node, SelectedShell, AvaloniaUiDispatcher.Instance);
        _agent.Output += OnOutput;
        _agent.StateChanged += OnStateChanged;
        _agent.Resize(_terminal.Emulator.Cols, _terminal.Emulator.Rows);
        await _agent.StartAsync();
    }

    private void OnOutput(string text) => _terminal.Write(text);

    private void OnStateChanged()
    {
        if (!_uiReady) return;
        switch (State)
        {
            case SshSessionState.Connecting:
                SetStatus("Connexion…", "#EFEFF4", "#8A8A8E");
                AuthPanel.IsVisible = false;
                break;

            case SshSessionState.NeedsCredentials:
                SetStatus("Authentification requise", "#FFF4E5", "#B25E09");
                ShowAuthPanel();
                break;

            case SshSessionState.Authenticating:
                SetStatus("Authentification…", "#EFEFF4", "#8A8A8E");
                AuthPanel.IsVisible = false;
                break;

            case SshSessionState.Connected:
                SetStatus("Connecté", "#E4F8E8", "#248A3D");
                AuthPanel.IsVisible = false;
                var pending = _mode == Mode.Agent ? _agent!.DrainPendingOutput() : _ssh!.DrainPendingOutput();
                if (!string.IsNullOrEmpty(pending)) _terminal.Write(pending);
                _terminal.Focus();
                if (_mode == Mode.Agent) ShowExpectedIdentity();
                break;

            case SshSessionState.Closed:
                SetStatus("Session fermée", "#FDE8E7", "#C0392B");
                AuthPanel.IsVisible = false;
                if (!string.IsNullOrEmpty(CloseReason))
                    _terminal.Write($"\r\n\u001B[31m*** {CloseReason}\u001B[0m\r\n");
                if (_mode == Mode.Agent)
                {
                    _terminal.Write("\u001B[90m» Appuyez sur Entrée pour vous reconnecter.\u001B[0m\r\n");
                    SetReconnectHighlighted(true);
                }
                break;
        }
    }

    private void SetStatus(string text, string background, string foreground)
    {
        StatusLabel.Text = text;
        StatusBadge.Background = Palette.Brush(background);
        StatusLabel.Foreground = Palette.Brush(foreground);
    }

    /// <summary>Session fermée : « Reconnecter » passe en couleur d'accent.</summary>
    private void SetReconnectHighlighted(bool highlighted)
    {
        ReconnectButton.Classes.Set("tool", !highlighted);
        ReconnectButton.Classes.Set("accentTool", highlighted);
    }

    // MARK: - Shell (terminal par l'agent)

    /// <summary>Mêmes choix que le menu terminal de l'interface web MeshCentral.</summary>
    private void PopulateShellChoices()
    {
        ComboBoxItem Choice(string label, ShellKind kind, string tip)
        {
            var item = new ComboBoxItem { Content = label, Tag = kind };
            ToolTip.SetTip(item, tip);
            return item;
        }

        ShellCombo.Items.Clear();
        if (_node.OsFamily == OSFamily.Windows)
        {
            ShellCombo.Items.Add(Choice("Admin Shell", ShellKind.Default,
                "Invite de commandes avec les droits de l'agent (NT AUTHORITY\\SYSTEM)"));
            ShellCombo.Items.Add(Choice("Admin PowerShell", ShellKind.PowerShell,
                "PowerShell avec les droits de l'agent (NT AUTHORITY\\SYSTEM)"));
            ShellCombo.Items.Add(Choice("User Shell", ShellKind.UserShell,
                "Invite de commandes dans le contexte de l'utilisateur connecté"));
            ShellCombo.Items.Add(Choice("User PowerShell", ShellKind.UserPowerShell,
                "PowerShell dans le contexte de l'utilisateur connecté"));
        }
        else
        {
            ShellCombo.Items.Add(Choice("Root Shell", ShellKind.Default, "Shell avec les droits de l'agent (root)"));
            ShellCombo.Items.Add(Choice("User Shell", ShellKind.UserShell, "Shell dans le contexte de l'utilisateur connecté"));
            ShellCombo.Items.Add(Choice("Login Shell", ShellKind.LoginShell,
                "Invite de connexion (login/mot de passe) dans la console, comme sur une console physique"));
        }
        ShellCombo.SelectedIndex = 0;
    }

    private ShellKind SelectedShell => (ShellCombo.SelectedItem as ComboBoxItem)?.Tag as ShellKind? ?? ShellKind.Default;

    private async void ShellCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;
        IdentityBadge.IsVisible = false;
        _terminal.Clear();
        await StartSessionAsync();
    }

    /// <summary>Contexte de droits attendu selon le shell (« whoami » le confirme).</summary>
    private void ShowExpectedIdentity()
    {
        var (label, isPrivileged) = SelectedShell switch
        {
            ShellKind.UserShell or ShellKind.UserPowerShell => ("utilisateur connecté", false),
            ShellKind.LoginShell => ("connexion manuelle", false),
            _ => (_node.OsFamily == OSFamily.Windows ? "SYSTEM" : "root", true),
        };
        IdentityText.Text = $"⚡ {label}";
        IdentityBadge.Background = Palette.Brush(isPrivileged ? "#FFF4E5" : "#EFEFF4");
        IdentityText.Foreground = Palette.Brush(isPrivileged ? "#B25E09" : "#8A8A8E");
        IdentityBadge.IsVisible = true;
    }

    private async void Reconnect_Click(object? sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Agent) return;
        IdentityBadge.IsVisible = false;
        _terminal.Clear();
        await StartSessionAsync();
    }

    // MARK: - Identifiants SSH

    private void ShowAuthPanel()
    {
        if (_ssh is null) return;
        AuthPanel.IsVisible = true;
        if (_ssh.KeyPassOnly)
        {
            AuthTitle.Text = "Phrase de passe de la clé SSH";
            UsernameGroup.IsVisible = false;
            PasswordLabel.Text = "Phrase de passe";
            SshRememberCheck.IsVisible = false;
        }
        else
        {
            AuthTitle.Text = "Identifiants SSH";
            UsernameGroup.IsVisible = true;
            PasswordLabel.Text = "Mot de passe";
            SshRememberCheck.IsVisible = true;
        }

        AuthError.Text = _ssh.AuthError ?? "";
        AuthError.IsVisible = _ssh.AuthError is not null;
        SshPassBox.Text = "";
        if (_ssh.KeyPassOnly || !string.IsNullOrEmpty(SshUserBox.Text)) SshPassBox.Focus();
        else SshUserBox.Focus();
    }

    private void AuthField_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Authenticate_Click(sender, e);
    }

    private void Authenticate_Click(object? sender, RoutedEventArgs e)
    {
        if (_ssh is null) return;
        if (_ssh.KeyPassOnly)
        {
            _ssh.SubmitKeyPassphrase(SshPassBox.Text ?? "");
            return;
        }
        var user = (SshUserBox.Text ?? "").Trim();
        if (user.Length == 0)
        {
            AuthError.Text = "Renseignez un nom d'utilisateur.";
            AuthError.IsVisible = true;
            SshUserBox.Focus();
            return;
        }
        _ssh.SubmitCredentials(user, SshPassBox.Text ?? "", SshRememberCheck.IsChecked == true);
    }

    // MARK: - Commandes rapides (terminal par l'agent)

    private void Commands_Click(object? sender, RoutedEventArgs e) => OpenQuickCommands();

    /// <summary>Menu des commandes courantes, reconstruit à chaque ouverture (il dépend du shell).</summary>
    private void OpenQuickCommands()
    {
        if (_mode != Mode.Agent) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

        // Commandes de l'utilisateur en tête : ce sont ses plus fréquentes.
        var mine = CustomCommandsForShell();
        if (mine.Count > 0)
        {
            flyout.Items.Add(new MenuItem { Header = "Mes commandes", IsEnabled = false });
            foreach (var command in mine)
            {
                var item = new MenuItem { Header = command.Run ? command.Label : command.Label + "…" };
                ToolTip.SetTip(item, QuickCommandToolTip(command));
                item.Click += (_, _) => SendQuickCommand(command);
                flyout.Items.Add(item);
            }
            flyout.Items.Add(new Separator());
        }

        foreach (var group in QuickCommands.For(_node.OsFamily, SelectedShell))
        {
            var submenu = new MenuItem { Header = group.Title };
            foreach (var command in group.Items)
            {
                if (command is null)
                {
                    submenu.Items.Add(new Separator());
                    continue;
                }
                var item = new MenuItem { Header = command.Run ? command.Label : command.Label + "…" };
                ToolTip.SetTip(item, QuickCommandToolTip(command));
                item.Click += (_, _) => SendQuickCommand(command);
                submenu.Items.Add(item);
            }
            flyout.Items.Add(submenu);
        }

        flyout.Items.Add(new Separator());

        // Ajout : la sélection du terminal, si elle existe, sert de point de
        // départ — on retient ainsi une commande qu'on vient de taper.
        var selection = _terminal.GetSelectedText()?.Trim();
        var add = new MenuItem
        {
            Header = string.IsNullOrEmpty(selection) ? "Ajouter une commande…" : "Ajouter la sélection à mes commandes…",
        };
        add.Click += async (_, _) => await OpenCustomCommandsAsync(selection ?? "");
        flyout.Items.Add(add);

        var manage = new MenuItem { Header = "Gérer mes commandes…" };
        manage.Click += async (_, _) => await OpenCustomCommandsAsync(null);
        flyout.Items.Add(manage);

        flyout.Items.Add(new Separator());
        flyout.Items.Add(new MenuItem { Header = "« … » : commande tapée, à valider par Entrée", IsEnabled = false });
        flyout.Closed += (_, _) => _terminal.Focus();

        if (CommandsButton.IsEffectivelyVisible) flyout.ShowAt(CommandsButton);
        else
        {
            flyout.Placement = PlacementMode.Center;
            flyout.ShowAt(_terminal);
        }
    }

    // MARK: - Commandes personnalisées

    /// <summary>Famille du shell ouvert, au sens des commandes personnalisées.</summary>
    private CustomCommandShell CurrentCustomShell =>
        _node.OsFamily != OSFamily.Windows
            ? CustomCommandShell.Unix
            : SelectedShell is ShellKind.PowerShell or ShellKind.UserPowerShell
                ? CustomCommandShell.PowerShell
                : CustomCommandShell.Cmd;

    /// <summary>Commandes de l'utilisateur destinées au shell ouvert.</summary>
    private List<QuickCommand> CustomCommandsForShell()
    {
        var shell = CurrentCustomShell;
        return App.Settings.CustomTerminalCommands
            .Where(c => !string.IsNullOrWhiteSpace(c.Command)
                        && (c.Shell == CustomCommandShell.Any || c.Shell == shell))
            .Select(c => new QuickCommand(
                string.IsNullOrWhiteSpace(c.Label) ? c.Command : c.Label,
                // Plusieurs lignes : chacune part avec Entrée, comme tapée.
                c.Command.Replace("\r\n", "\r").Replace('\n', '\r'),
                c.Run))
            .ToList();
    }

    /// <param name="newCommand">Commande à créer (texte de départ, éventuellement vide), ou null pour la liste.</param>
    private async Task OpenCustomCommandsAsync(string? newCommand)
    {
        await new CustomCommandsWindow(CurrentCustomShell, newCommand).ShowDialog<bool>(Host);
        _terminal.Focus();
    }

    private static Control QuickCommandToolTip(QuickCommand command)
    {
        var panel = new StackPanel { MaxWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = command.Command,
            FontFamily = TerminalControl.TerminalFont,
            TextWrapping = TextWrapping.Wrap,
        });
        var hint = command.Run
            ? command.Note
            : string.Join(" ", new[] { command.Note, "Tapée sans être exécutée : vérifiez-la, puis Entrée." }
                                  .Where(part => !string.IsNullOrEmpty(part)));
        if (!string.IsNullOrEmpty(hint))
        {
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = Palette.Resource("TextSecondaryBrush"),
            });
        }
        return panel;
    }

    private void SendQuickCommand(QuickCommand command)
    {
        if (!IsConnected)
        {
            _terminal.Write("\u001B[90m» Session non connectée : la commande n'a pas été envoyée.\u001B[0m\r\n");
            return;
        }
        // Sous Linux, Ctrl+E puis Ctrl+U vident la ligne en cours.
        var clearLine = _node.OsFamily == OSFamily.Windows ? "" : "\u0005\u0015";
        Send(clearLine + command.Command + (command.Run ? "\r" : ""));
        _terminal.Focus();
    }

    // MARK: - Barre d'outils

    private void Desktop_Click(object? sender, RoutedEventArgs e)
    {
        if (CreateDesktop is null)
        {
            OpenDesktop?.Invoke();
            return;
        }
        DockedDesktop.ShowMenu(DesktopButton, () =>
        {
            // Le clavier revient au shell, sauf s'il vient d'être donné au bureau.
            if (!DockedDesktop.HasKeyboard) _terminal.Focus();
        });
    }

    private void Search_Click(object? sender, RoutedEventArgs e) => SearchBar.Toggle();

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        _terminal.Clear();
        _terminal.Focus();
    }

    private void Fullscreen_Click(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        Host.WindowState = Host.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        _terminal.Focus();
    }

    /// <summary>Barres réglées sur l'état réel de la fenêtre (voir DesktopWindow).</summary>
    protected override void OnHostWindowStateChanged()
    {
        if (WindowTitleBar is null) return;

        bool full = Host.WindowState == WindowState.FullScreen;
        // Sous les onglets, la barre du terminal (sans barre d'outils) reste effacée.
        WindowTitleBar.IsVisible = !full && !WindowTitleBar.IsEmbedded;
        Toolbar.IsVisible = !full;
        FullscreenHint.IsVisible = full;
        ToolTip.SetTip(FullscreenButton, full ? "Quitter le plein écran (Ctrl+Maj+F)" : "Plein écran (Ctrl+Maj+F)");
        if (full) DispatcherTimer.RunOnce(() => FullscreenHint.IsVisible = false, TimeSpan.FromSeconds(5));
    }

    // MARK: - Repli de la barre d'outils

    /// <summary>Place gardée au nom de la machine avant de replier les boutons.</summary>
    private const double TitleReserve = 140;

    private bool _fittingToolbar;

    /// <summary>
    /// Fenêtre étroite : les boutons perdent leur libellé et ne gardent que
    /// leur pictogramme (l'info-bulle dit ce qu'ils font), pour laisser au nom
    /// de la machine, à l'état et au choix du shell la place de s'afficher.
    /// </summary>
    private void FitToolbar()
    {
        if (_fittingToolbar || Toolbar.Bounds.Width <= 0) return;
        _fittingToolbar = true;
        try
        {
            SetButtonLabels(true);
            ToolbarControls.Measure(Size.Infinity);
            var controls = ToolbarControls.DesiredSize.Width;

            double left = TitleReserve;
            foreach (var part in new Control[] { StatusBadge, ShellLabel, ShellCombo, IdentityBadge })
            {
                if (!part.IsVisible) continue;
                part.Measure(Size.Infinity);
                left += part.DesiredSize.Width + part.Margin.Left + part.Margin.Right;
            }

            var available = Toolbar.Bounds.Width - Toolbar.Padding.Left - Toolbar.Padding.Right - 12;
            SetButtonLabels(controls + left <= available);
        }
        finally
        {
            _fittingToolbar = false;
        }
    }

    private void SetButtonLabels(bool visible)
    {
        foreach (var button in ToolbarControls.Children.OfType<Button>())
        {
            if (button.Content is not StackPanel { Children: [Icon icon, TextBlock label] }) continue;
            label.IsVisible = visible;
            icon.Margin = visible ? new Thickness(0, 1, 6, 0) : new Thickness(0, 1, 0, 0);
            button.Padding = visible ? new Thickness(10, 6) : new Thickness(9, 5);
        }
    }

    // MARK: - Presse-papiers

    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        if (!IsConnected || Host.Clipboard is not { } clipboard) return;
        try
        {
            var data = await TerminalPaste.PrepareAsync(await clipboard.TryGetTextAsync(),
                                                         _terminal.Emulator.BracketedPaste, Host);
            if (data is not null) Send(data);
        }
        catch { /* presse-papiers indisponible */ }
        _terminal.Focus();
    }

    private async void CopyAll_Click(object? sender, RoutedEventArgs e)
    {
        await _terminal.CopyToClipboardAsync(selectionOnly: false);
        _terminal.Focus();
    }

    // MARK: - Clavier

    /// <summary>Un champ (recherche, identifiants) a le clavier : les frappes lui appartiennent.</summary>
    private bool InputFieldHasFocus => FocusManager?.GetFocusedElement() is TextBox;

    private void Terminal_TextInput(object? sender, TextInputEventArgs e)
    {
        if (!IsConnected || string.IsNullOrEmpty(e.Text)) return;
        Send(e.Text);
        e.Handled = true;
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        // Le bureau ancré a le clavier : ses frappes traversent ce panneau et
        // lui reviennent, pas au shell.
        if (DockedDesktop.HasKeyboard) return;
        if (InputFieldHasFocus) return;
        var mods = e.KeyModifiers;
        var ctrl = mods.HasFlag(KeyModifiers.Control);
        var shift = mods.HasFlag(KeyModifiers.Shift);
        var cmd = OperatingSystem.IsMacOS() && mods.HasFlag(KeyModifiers.Meta);

        // Raccourcis locaux, valables même session fermée : relire, copier,
        // chercher dans la sortie est précisément ce qu'on y vient faire.
        if ((ctrl && shift && e.Key == Key.C) || (cmd && e.Key == Key.C))
        {
            if (cmd) _ = _terminal.CopyToClipboardAsync(selectionOnly: _terminal.HasSelection);
            else CopyAll_Click(this, e);
            e.Handled = true;
            return;
        }
        // Ctrl+C sur une sélection : copier ; sans sélection, il interrompt.
        if (ctrl && !shift && e.Key == Key.C && _terminal.HasSelection && !OperatingSystem.IsMacOS())
        {
            _ = _terminal.CopyToClipboardAsync(selectionOnly: true);
            e.Handled = true;
            return;
        }
        if ((ctrl && shift && e.Key == Key.F) || (cmd && ctrl && e.Key == Key.F))
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if ((ctrl && shift && e.Key == Key.R) || (cmd && !ctrl && e.Key == Key.F))
        {
            SearchBar.Toggle();
            e.Handled = true;
            return;
        }
        if ((ctrl || cmd) && shift && e.Key == Key.K)
        {
            OpenQuickCommands();
            e.Handled = true;
            return;
        }
        if (cmd && !shift && e.Key == Key.K)
        {
            _terminal.Clear();
            e.Handled = true;
            return;
        }
        if (ctrl && shift && e.Key == Key.D)
        {
            if (CreateDesktop is null) OpenDesktop?.Invoke();
            else DockedDesktop.Toggle();
            e.Handled = true;
            return;
        }

        if (_terminal.HandleViewKey(e.Key, mods))
        {
            e.Handled = true;
            return;
        }

        // Session fermée : Entrée la rouvre.
        if (State == SshSessionState.Closed && _mode == Mode.Agent && e.Key == Key.Enter && mods == KeyModifiers.None)
        {
            Reconnect_Click(this, e);
            e.Handled = true;
            return;
        }

        if (!IsConnected) return;

        if ((ctrl && shift && e.Key == Key.V) || (cmd && e.Key == Key.V))
        {
            Paste_Click(this, e);
            e.Handled = true;
            return;
        }
        if (cmd) return;   // autres Cmd+… : raccourcis du système

        if (ctrl && !shift && TerminalKeys.MapControlChar(e.Key) is { } control)
        {
            Send(control);
            e.Handled = true;
            return;
        }
        if (TerminalKeys.MapSpecialKey(e.Key) is { } sequence)
        {
            Send(sequence);
            e.Handled = true;
        }
        // Sinon : caractère imprimable, laissé à la saisie de texte.
    }
}
