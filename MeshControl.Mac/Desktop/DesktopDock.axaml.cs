using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeshControl.Core;
using MeshControl.Desktop;

namespace MeshControl.Mac;

/// <summary>
/// Bureau à distance ancré en bas à droite d'un terminal (portage de
/// Desktop/DesktopDock.xaml.cs) : on garde l'écran de la machine sous les yeux
/// pendant qu'on y tape des commandes.
///
/// Le bureau reste une DesktopWindow à part entière, jamais affichée, dont le
/// contenu est logé ici — exactement comme en mode onglets. Il peut donc à
/// tout moment quitter la vignette pour un onglet ou sa propre fenêtre sans
/// rouvrir la connexion.
///
/// Le clavier va là où l'on a cliqué en dernier : au terminal par défaut, au
/// bureau après un clic sur l'écran distant (le cadre passe alors en couleur
/// d'accent). Le terminal hôte laisse passer ses frappes tant que
/// <see cref="HasKeyboard"/> est vrai.
/// </summary>
public partial class DesktopDock : UserControl
{
    private const double MinDockWidth = 300;
    private const double MinDockHeight = 190;

    /// <summary>Espace laissé libre autour de la vignette, côtés haut et gauche.</summary>
    private const double ParentClearance = 24;

    private MeshNode? _node;
    private ChromeWindow? _carrier;
    private Func<DesktopWindow>? _createDesktop;
    private Action<DesktopWindow>? _showSeparately;

    /// <summary>Bureau ancré ici, ou null.</summary>
    private DesktopWindow? _desktop;

    /// <summary>Contenu repris de sa fenêtre porteuse, à lui rendre au départ.</summary>
    private Control? _content;

    /// <summary>Vignette réduite à son en-tête.</summary>
    private bool _collapsed;

    public DesktopDock()
    {
        InitializeComponent();

        Header.PointerPressed += Header_PointerPressed;
        ResizeGrip.DragDelta += ResizeGrip_DragDelta;
        ResizeGrip.DragCompleted += (_, _) =>
        {
            App.Settings.DesktopDockWidth = Math.Round(Width);
            App.Settings.DesktopDockHeight = Math.Round(Height);
            App.Settings.Save();
        };

        // Une vignette plus grande que le terminal masquerait tout : elle suit
        // la taille de la zone qui l'accueille.
        AttachedToVisualTree += (_, _) =>
        {
            if (Parent is Control parent)
            {
                parent.SizeChanged -= Parent_SizeChanged;
                parent.SizeChanged += Parent_SizeChanged;
                UpdateMaxSize(parent);
            }
        };
    }

    /// <summary>
    /// Le bureau rend le clavier (vignette réduite ou fermée) : le terminal
    /// reprend le focus, faute de quoi les frappes n'iraient nulle part.
    /// </summary>
    public event Action? KeyboardReturned;

    /// <param name="carrier">Fenêtre du terminal : le bureau est fermé avec elle.</param>
    /// <param name="createDesktop">Crée un bureau pour la machine (fourni par la fenêtre principale).</param>
    /// <param name="showSeparately">Ouvre un bureau en onglet ou en fenêtre, selon la préférence.</param>
    public void Attach(MeshNode node, ChromeWindow carrier, Func<DesktopWindow> createDesktop,
                       Action<DesktopWindow> showSeparately)
    {
        _node = node;
        _carrier = carrier;
        _createDesktop = createDesktop;
        _showSeparately = showSeparately;
        carrier.Closed += (_, _) => CloseDesktop();
    }

    /// <summary>Vrai quand le clavier appartient au bureau ancré.</summary>
    public bool HasKeyboard => _desktop is not null && IsKeyboardFocusWithin;

    /// <summary>Relâche les touches maintenues côté distant.</summary>
    public void ReleaseKeyboard() => _desktop?.ReleaseKeyboard();

    // MARK: - Menu et raccourci du terminal

    /// <summary>
    /// Menu du bouton « Bureau » du terminal : ancrer, réduire ou rétablir la
    /// vignette, ou ouvrir le bureau en grand.
    /// </summary>
    public void ShowMenu(Control target, Action? closed = null)
    {
        var flyout = new MenuFlyout
        {
            Placement = target.IsEffectivelyVisible ? PlacementMode.BottomEdgeAlignedLeft : PlacementMode.Center,
        };

        if (_desktop is null)
            flyout.Items.Add(CreateMenuItem("Ancrer en bas à droite du terminal", IconKind.Desktop, "Ctrl+Maj+D", OpenDocked));
        else if (_collapsed)
            flyout.Items.Add(CreateMenuItem("Afficher la vignette", IconKind.ChevronUp, "Ctrl+Maj+D", () => SetCollapsed(false)));
        else
            flyout.Items.Add(CreateMenuItem("Réduire la vignette", IconKind.ChevronDown, "Ctrl+Maj+D", () => SetCollapsed(true)));

        flyout.Items.Add(CreateMenuItem(SeparateLabel, IconKind.FullScreen, null, OpenSeparately));

        if (_desktop is not null)
        {
            flyout.Items.Add(new Separator());
            flyout.Items.Add(CreateMenuItem("Fermer le bureau à distance", IconKind.Close, null, CloseDesktop));
        }

        if (closed is not null) flyout.Closed += (_, _) => closed();
        flyout.ShowAt(target.IsEffectivelyVisible ? target : (Control?)Parent ?? target);
    }

    private static MenuItem CreateMenuItem(string header, IconKind glyph, string? gesture, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new Icon { Kind = glyph, Size = 12 },
        };
        if (gesture is not null) item.InputGesture = KeyGesture.Parse("Ctrl+Shift+D");
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Raccourci du terminal (Ctrl+Maj+D) : ouvre la vignette, puis la réduit et la rétablit.</summary>
    public void Toggle()
    {
        if (_desktop is null) OpenDocked();
        else SetCollapsed(!_collapsed);
    }

    /// <summary>« Onglet » ou « fenêtre », selon la préférence de l'application.</summary>
    private static string SeparateLabel => App.Settings.UseTabbedSessions ? "Ouvrir dans un onglet" : "Ouvrir dans une fenêtre";

    // MARK: - Ouverture

    /// <summary>Ouvre le bureau de la machine dans la vignette.</summary>
    public void OpenDocked()
    {
        if (_desktop is not null)
        {
            SetCollapsed(false);
            return;
        }
        if (!CanOpenDesktop() || _createDesktop is null) return;

        var desktop = _createDesktop();
        // Avant de reprendre le contenu : la vignette n'a ni barre d'outils ni
        // barre d'état, et le plein écran ne concerne pas la fenêtre du terminal.
        desktop.SetDocked(true, _carrier?.Host);

        _content = desktop.Content as Control;
        desktop.Content = null;
        _desktop = desktop;

        desktop.SessionStateChanged += UpdateState;
        desktop.UndockRequested += Desktop_UndockRequested;
        desktop.Closed += Desktop_Closed;

        TitleText.Text = desktop.DeviceName;
        ToolTip.SetTip(UndockButton, $"{SeparateLabel} (Ctrl+Maj+F dans le bureau)");
        ContentHost.Child = _content;
        SetCollapsed(false);
        IsVisible = true;
        UpdateState();

        // La fenêtre porteuse n'étant jamais affichée, Opened ne démarrera pas
        // la session : c'est à l'hôte de le faire, comme pour un onglet.
        Dispatcher.UIThread.Post(async () =>
        {
            try { await desktop.StartSessionAsync(); }
            catch (Exception ex) { Log.Error($"Démarrage du bureau ancré « {desktop.DeviceName} » impossible", ex, "Desktop"); }
        });
    }

    /// <summary>
    /// Ouvre le bureau en grand, dans un onglet ou sa propre fenêtre. Le bureau
    /// ancré, s'il y en a un, y est déplacé tel quel : même connexion.
    /// </summary>
    public void OpenSeparately()
    {
        if (_showSeparately is null || _createDesktop is null) return;

        DesktopWindow window;
        if (_desktop is { } docked)
        {
            ReleaseDesktop();
            window = docked;
        }
        else
        {
            if (!CanOpenDesktop()) return;
            window = _createDesktop();
        }
        _showSeparately(window);
    }

    /// <summary>Signale les cas où le bureau à distance ne peut pas s'ouvrir.</summary>
    private bool CanOpenDesktop()
    {
        if (_node is null) return false;
        if (_node.HasAgent) return true;
        if (_carrier is not null)
            _ = Dialogs.InformAsync(_carrier.Host, $"« {_node.Name} » n'a pas d'agent installé : le bureau à distance n'est pas disponible.");
        return false;
    }

    // MARK: - Fermeture

    /// <summary>Ferme le bureau ancré et sa connexion.</summary>
    public void CloseDesktop()
    {
        if (_desktop is not { } desktop) return;
        ReleaseDesktop();
        try { desktop.Close(); } catch { /* déjà fermée */ }
    }

    /// <summary>Rend son contenu au bureau et vide la vignette, sans fermer la session.</summary>
    private void ReleaseDesktop()
    {
        if (_desktop is not { } desktop) return;

        var hadKeyboard = HasKeyboard;
        desktop.ReleaseKeyboard();

        desktop.SessionStateChanged -= UpdateState;
        desktop.UndockRequested -= Desktop_UndockRequested;
        desktop.Closed -= Desktop_Closed;

        ContentHost.Child = null;
        desktop.SetDocked(false, null);
        desktop.Content = _content;

        _desktop = null;
        _content = null;
        IsVisible = false;

        if (hadKeyboard) KeyboardReturned?.Invoke();
    }

    /// <summary>Ctrl+Maj+F dans la vignette, différé : la demande arrive au milieu d'une frappe.</summary>
    private void Desktop_UndockRequested() => Dispatcher.UIThread.Post(OpenSeparately);

    /// <summary>Le bureau s'est fermé sans passer par la vignette.</summary>
    private void Desktop_Closed(object? sender, EventArgs e)
    {
        if (_desktop is null) return;
        _desktop.SessionStateChanged -= UpdateState;
        _desktop.UndockRequested -= Desktop_UndockRequested;
        _desktop = null;
        ContentHost.Child = null;
        _content = null;
        IsVisible = false;
        KeyboardReturned?.Invoke();
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
        ToolTip.SetTip(StateText, string.IsNullOrEmpty(text) ? null : text);
        StatusDot.Fill = Palette.Resource(online ? "OnlineBrush" : "OfflineBrush");
        ReconnectButton.IsVisible = desktop.SessionState == DesktopSessionState.Closed;
    }

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Header).Properties.IsLeftButtonPressed) return;
        // Clic sur un bouton de l'en-tête : il fait son office.
        for (var element = e.Source as StyledElement; element is not null && element != Header; element = element.Parent)
            if (element is Button) return;

        // Réduite, la vignette se rétablit d'un clic ; ouverte, un double-clic
        // la réduit, un simple clic donne le clavier au bureau.
        if (_collapsed) SetCollapsed(false);
        else if (e.ClickCount == 2) SetCollapsed(true);
        else _desktop?.FocusRemote();
        e.Handled = true;
    }

    private void Reconnect_Click(object? sender, RoutedEventArgs e) => _desktop?.Reconnect();
    private void Collapse_Click(object? sender, RoutedEventArgs e) => SetCollapsed(!_collapsed);
    private void Undock_Click(object? sender, RoutedEventArgs e) => OpenSeparately();
    private void Close_Click(object? sender, RoutedEventArgs e) => CloseDesktop();

    /// <summary>Réduit la vignette à son en-tête, ou la rétablit.</summary>
    private void SetCollapsed(bool collapsed)
    {
        var hadKeyboard = collapsed && HasKeyboard;
        if (hadKeyboard) _desktop?.ReleaseKeyboard();

        _collapsed = collapsed;
        ContentHost.IsVisible = !collapsed;
        ResizeGrip.IsVisible = !collapsed;

        CollapseGlyph.Kind = collapsed ? IconKind.ChevronUp : IconKind.ChevronDown;
        ToolTip.SetTip(CollapseButton, collapsed
            ? "Afficher la vignette (Ctrl+Maj+D depuis le terminal)"
            : "Réduire la vignette (Ctrl+Maj+D depuis le terminal)");

        if (collapsed)
        {
            // En-tête seul : la largeur suit son contenu.
            Width = double.NaN;
            Height = double.NaN;
        }
        else
        {
            Width = Math.Max(MinDockWidth, App.Settings.DesktopDockWidth);
            Height = Math.Max(MinDockHeight, App.Settings.DesktopDockHeight);
        }

        _desktop?.UpdateStreamPause();
        if (hadKeyboard) KeyboardReturned?.Invoke();
    }

    // MARK: - Taille

    private void Parent_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control parent) UpdateMaxSize(parent);
    }

    private void UpdateMaxSize(Control parent)
    {
        if (parent.Bounds.Width <= 0 || parent.Bounds.Height <= 0) return;
        MaxWidth = Math.Max(MinDockWidth, parent.Bounds.Width - Margin.Right - ParentClearance);
        MaxHeight = Math.Max(MinDockHeight, parent.Bounds.Height - Margin.Bottom - ParentClearance);
    }

    /// <summary>Tirer le coin supérieur gauche vers le haut et la gauche agrandit la vignette.</summary>
    private void ResizeGrip_DragDelta(object? sender, VectorEventArgs e)
    {
        if (_collapsed) return;
        Width = Math.Clamp(Bounds.Width - e.Vector.X, MinDockWidth, MaxWidth);
        Height = Math.Clamp(Bounds.Height - e.Vector.Y, MinDockHeight, MaxHeight);
    }
}
