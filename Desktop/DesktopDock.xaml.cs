using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MeshControl.Core;

namespace MeshControl.Desktop;

/// <summary>
/// Bureau à distance ancré en bas à droite d'un terminal : on garde l'écran
/// de la machine sous les yeux pendant qu'on y tape des commandes.
///
/// Le bureau reste une DesktopWindow à part entière, jamais affichée, dont le
/// contenu est logé ici — exactement comme en mode onglets. Il peut donc à
/// tout moment quitter la vignette pour un onglet ou sa propre fenêtre sans
/// rouvrir la connexion.
///
/// Le clavier va là où l'on a cliqué en dernier : au terminal par défaut,
/// au bureau après un clic sur l'écran distant (le cadre passe alors en
/// couleur d'accent). Le terminal hôte doit laisser passer ses frappes tant
/// que <see cref="HasKeyboard"/> est vrai.
/// </summary>
public partial class DesktopDock : UserControl
{
    private const double MinDockWidth = 300;
    private const double MinDockHeight = 190;

    /// <summary>Espace laissé libre autour de la vignette, côtés haut et gauche.</summary>
    private const double ParentClearance = 24;

    private MeshServerConnection? _connection;
    private MeshNode? _node;

    /// <summary>Bureau ancré ici, ou null.</summary>
    private DesktopWindow? _desktop;

    /// <summary>Contenu repris de sa fenêtre porteuse, à lui rendre au départ.</summary>
    private UIElement? _content;

    /// <summary>Vignette réduite à son en-tête : l'image n'est plus reçue.</summary>
    private bool _collapsed;

    public DesktopDock()
    {
        InitializeComponent();

        // Une vignette plus grande que le terminal masquerait tout : elle suit
        // la taille de la zone qui l'accueille.
        Loaded += (_, _) =>
        {
            if (Parent is FrameworkElement parent)
            {
                parent.SizeChanged -= Parent_SizeChanged;
                parent.SizeChanged += Parent_SizeChanged;
                UpdateMaxSize(parent);
            }
        };
    }

    /// <summary>
    /// Le bureau a demandé à rendre le clavier : vignette réduite ou fermée.
    /// Le terminal hôte reprend alors le focus, faute de quoi les frappes
    /// n'iraient nulle part.
    /// </summary>
    public event Action? KeyboardReturned;

    /// <summary>
    /// Relie la vignette à sa machine. Le bureau est fermé avec la fenêtre
    /// porteuse du terminal : il n'aurait plus d'interface pour être piloté.
    /// </summary>
    public void Attach(MeshServerConnection connection, MeshNode node, Window carrier)
    {
        _connection = connection;
        _node = node;
        carrier.Closed += (_, _) => CloseDesktop();
    }

    /// <summary>Vrai quand le clavier appartient au bureau ancré.</summary>
    public bool HasKeyboard => IsKeyboardFocusWithin;

    /// <summary>Relâche les touches maintenues côté distant.</summary>
    public void ReleaseKeyboard() => _desktop?.ReleaseKeyboard();

    // MARK: - Menu et raccourci du terminal

    /// <summary>
    /// Menu du bouton « Bureau » du terminal : ancrer, réduire ou rétablir la
    /// vignette, ou ouvrir le bureau en grand.
    /// </summary>
    public void ShowMenu(FrameworkElement target, Action? closed = null)
    {
        var underTarget = target.IsVisible;
        var menu = new ContextMenu
        {
            // Barre d'outils masquée (plein écran) : au centre du terminal.
            PlacementTarget = underTarget ? target : this.Parent as UIElement ?? target,
            Placement = underTarget ? PlacementMode.Bottom : PlacementMode.Center,
        };

        if (_desktop is null)
        {
            menu.Items.Add(CreateMenuItem("Ancrer en bas à droite du terminal", "\uE90C",
                                    "Ctrl+Maj+D", OpenDocked));
        }
        else if (_collapsed)
        {
            menu.Items.Add(CreateMenuItem("Afficher la vignette", "\uE923",
                                    "Ctrl+Maj+D", () => SetCollapsed(false)));
        }
        else
        {
            menu.Items.Add(CreateMenuItem("Réduire la vignette", "\uE921",
                                    "Ctrl+Maj+D", () => SetCollapsed(true)));
        }

        menu.Items.Add(CreateMenuItem(SeparateLabel, "\uE8A7", null, OpenSeparately));

        if (_desktop is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Fermer le bureau à distance", "\uE711", null, CloseDesktop));
        }

        if (closed is not null) menu.Closed += (_, _) => closed();
        menu.IsOpen = true;
    }

    private static MenuItem CreateMenuItem(string header, string glyph, string? gesture, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture ?? "",
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>
    /// Raccourci du terminal (Ctrl+Maj+D) : ouvre la vignette, puis la
    /// réduit et la rétablit à chaque appui.
    /// </summary>
    public void Toggle()
    {
        if (_desktop is null) OpenDocked();
        else SetCollapsed(!_collapsed);
    }

    /// <summary>« Onglet » ou « fenêtre », selon la préférence de l'application.</summary>
    private static string SeparateLabel => AppSettings.Load().UseTabbedSessions
        ? "Ouvrir dans un onglet"
        : "Ouvrir dans une fenêtre";

    // MARK: - Ouverture

    /// <summary>Ouvre le bureau de la machine dans la vignette.</summary>
    public void OpenDocked()
    {
        if (_desktop is not null)
        {
            SetCollapsed(false);
            return;
        }
        if (!CanOpenDesktop() || _connection is null || _node is null) return;

        var desktop = new DesktopWindow(_connection, _node);
        // Avant de reprendre le contenu : son premier affichage ne doit pas
        // agrandir ni passer en plein écran la fenêtre du terminal.
        desktop.SetDocked(true);

        _content = desktop.Content as UIElement;
        desktop.Content = null;
        _desktop = desktop;

        desktop.SessionStateChanged += UpdateState;
        desktop.UndockRequested += Desktop_UndockRequested;
        desktop.Closed += Desktop_Closed;

        TitleText.Text = desktop.DeviceName;
        UndockButton.ToolTip = $"{SeparateLabel} (Ctrl+Maj+F dans le bureau)";
        ContentHost.Child = _content;
        SetCollapsed(false);
        Visibility = Visibility.Visible;
        UpdateState();

        // La fenêtre porteuse n'étant jamais affichée, Loaded ne démarrera pas
        // la session : c'est à l'hôte de le faire, comme pour un onglet.
        _ = Dispatcher.BeginInvoke(async () =>
        {
            try { await desktop.StartSessionAsync(); }
            catch (Exception ex)
            {
                Log.Error($"Démarrage du bureau ancré « {desktop.DeviceName} » impossible",
                          ex, "Desktop");
            }
        });
    }

    /// <summary>
    /// Ouvre le bureau en grand, dans un onglet ou sa propre fenêtre selon la
    /// préférence. Le bureau ancré, s'il y en a un, y est déplacé tel quel :
    /// même connexion, sans nouvelle négociation avec l'agent.
    /// </summary>
    public void OpenSeparately()
    {
        if (_connection is null || _node is null) return;

        DesktopWindow window;
        if (_desktop is { } docked)
        {
            ReleaseDesktop();
            window = docked;
        }
        else
        {
            if (!CanOpenDesktop()) return;
            window = new DesktopWindow(_connection, _node);
        }

        if (AppSettings.Load().UseTabbedSessions)
        {
            UI.SessionsWindow.AddSession(window, _node.Name, "🖥");
        }
        else
        {
            // Owner volontairement non défini, comme depuis la fenêtre
            // principale : le bureau survit à la fermeture du terminal.
            window.Show();
            window.Activate();
        }
    }

    /// <summary>Signale les cas où le bureau à distance ne peut pas s'ouvrir.</summary>
    private bool CanOpenDesktop()
    {
        if (_node is null) return false;
        if (_node.HasAgent) return true;

        MessageBox.Show(Window.GetWindow(this) ?? Application.Current.MainWindow!,
            $"« {_node.Name} » n'a pas d'agent installé : le bureau à distance n'est pas disponible.",
            "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    // MARK: - Fermeture

    /// <summary>Ferme le bureau ancré et sa connexion.</summary>
    public void CloseDesktop()
    {
        if (_desktop is not { } desktop) return;
        ReleaseDesktop();
        // Fermer la fenêtre porteuse arrête la session (voir DesktopWindow).
        try { desktop.Close(); } catch { /* déjà fermée */ }
    }

    /// <summary>
    /// Rend son contenu au bureau et vide la vignette, sans fermer la session.
    /// </summary>
    private void ReleaseDesktop()
    {
        if (_desktop is not { } desktop) return;

        var hadKeyboard = HasKeyboard;
        desktop.ReleaseKeyboard();

        desktop.SessionStateChanged -= UpdateState;
        desktop.UndockRequested -= Desktop_UndockRequested;
        desktop.Closed -= Desktop_Closed;

        ContentHost.Child = null;
        desktop.SetDocked(false);
        desktop.Content = _content;

        _desktop = null;
        _content = null;
        Visibility = Visibility.Collapsed;

        if (hadKeyboard) KeyboardReturned?.Invoke();
    }

    /// <summary>
    /// Ctrl+Maj+F dans la vignette. Différé : la demande arrive au milieu du
    /// traitement d'une frappe du bureau, dont on ne déplace pas le contenu
    /// en cours de route.
    /// </summary>
    private void Desktop_UndockRequested() =>
        Dispatcher.BeginInvoke(new Action(OpenSeparately));

    /// <summary>Le bureau s'est fermé sans passer par la vignette.</summary>
    private void Desktop_Closed(object? sender, EventArgs e)
    {
        if (_desktop is null) return;
        _desktop.SessionStateChanged -= UpdateState;
        _desktop.UndockRequested -= Desktop_UndockRequested;
        _desktop = null;
        ContentHost.Child = null;
        _content = null;
        Visibility = Visibility.Collapsed;
    }

    // MARK: - En-tête

    private void UpdateState()
    {
        if (_desktop is not { } desktop) return;

        var (text, online) = desktop.SessionState switch
        {
            DesktopSessionState.Connected => ("", true),
            DesktopSessionState.Connecting => ("Connexion…", false),
            DesktopSessionState.Reconnecting => ("Reconnexion…", false),
            _ => (desktop.SessionCloseReason ?? "Session fermée", false),
        };

        StateText.Text = text;
        StateText.ToolTip = string.IsNullOrEmpty(text) ? null : text;
        StatusDot.Fill = (System.Windows.Media.Brush)FindResource(online ? "OnlineBrush" : "OfflineBrush");
        ReconnectButton.Visibility = desktop.SessionState == DesktopSessionState.Closed
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Réduite, la vignette se rétablit d'un clic ; ouverte, un double-clic
        // la réduit, un simple clic donne le clavier au bureau.
        if (_collapsed)
            SetCollapsed(false);
        else if (e.ClickCount == 2)
            SetCollapsed(true);
        else
            _desktop?.FocusRemote();
        e.Handled = true;
    }

    private void Reconnect_Click(object sender, RoutedEventArgs e) => _desktop?.Reconnect();

    private void Collapse_Click(object sender, RoutedEventArgs e) => SetCollapsed(!_collapsed);

    private void Undock_Click(object sender, RoutedEventArgs e) => OpenSeparately();

    private void Close_Click(object sender, RoutedEventArgs e) => CloseDesktop();

    /// <summary>
    /// Réduit la vignette à son en-tête, ou la rétablit. Réduite, le contenu
    /// n'est plus visible : le bureau suspend de lui-même le flux d'images.
    /// </summary>
    private void SetCollapsed(bool collapsed)
    {
        var hadKeyboard = collapsed && HasKeyboard;
        if (hadKeyboard) _desktop?.ReleaseKeyboard();

        _collapsed = collapsed;
        var visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ContentHost.Visibility = visibility;
        ResizeGrip.Visibility = visibility;

        CollapseButton.Content = collapsed ? "\uE923" : "\uE921";
        CollapseButton.ToolTip = collapsed
            ? "Afficher la vignette (Ctrl+Maj+D depuis le terminal)"
            : "Réduire la vignette (Ctrl+Maj+D depuis le terminal)";

        if (collapsed)
        {
            // En-tête seul : la largeur suit son contenu.
            Width = double.NaN;
            Height = double.NaN;
        }
        else
        {
            var settings = AppSettings.Load();
            Width = Math.Max(MinDockWidth, settings.DesktopDockWidth);
            Height = Math.Max(MinDockHeight, settings.DesktopDockHeight);
        }

        if (hadKeyboard) KeyboardReturned?.Invoke();
    }

    // MARK: - Taille

    private void Parent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement parent) UpdateMaxSize(parent);
    }

    private void UpdateMaxSize(FrameworkElement parent)
    {
        if (parent.ActualWidth <= 0 || parent.ActualHeight <= 0) return;
        MaxWidth = Math.Max(MinDockWidth, parent.ActualWidth - Margin.Right - ParentClearance);
        MaxHeight = Math.Max(MinDockHeight, parent.ActualHeight - Margin.Bottom - ParentClearance);
    }

    /// <summary>
    /// La vignette est ancrée en bas à droite : tirer le coin supérieur
    /// gauche vers le haut et la gauche l'agrandit.
    /// </summary>
    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_collapsed) return;
        Width = Math.Clamp(ActualWidth - e.HorizontalChange, MinDockWidth, MaxWidth);
        Height = Math.Clamp(ActualHeight - e.VerticalChange, MinDockHeight, MaxHeight);
    }

    /// <summary>
    /// Retient la taille choisie. Les réglages sont relus juste avant : une
    /// autre fenêtre a pu en modifier d'autres depuis l'ouverture.
    /// </summary>
    private void ResizeGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var settings = AppSettings.Load();
        settings.DesktopDockWidth = Math.Round(Width);
        settings.DesktopDockHeight = Math.Round(Height);
        settings.Save();
    }
}
