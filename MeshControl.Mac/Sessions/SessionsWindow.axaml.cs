using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Fenêtre unique regroupant les sessions en onglets (portage de
/// UI/SessionsWindow.xaml.cs).
///
/// Principe : chaque session reste une fenêtre à part entière (avec toute sa
/// logique), mais son contenu visuel est détaché et placé dans un onglet. La
/// fenêtre d'origine n'est jamais affichée ; elle porte la session et ses
/// gestionnaires. Cela évite de dupliquer le code des bureaux, terminaux et
/// navigateurs de fichiers.
/// </summary>
public partial class SessionsWindow : ChromeWindow
{
    /// <summary>Instance unique : toutes les sessions se regroupent ici.</summary>
    private static SessionsWindow? _instance;

    private sealed class SessionTab
    {
        public required ChromeWindow Window { get; init; }
        public required Control Root { get; init; }
        public required string Title { get; init; }
        public required string Glyph { get; init; }
        public required Button Button { get; init; }
    }

    private readonly List<SessionTab> _tabs = new();
    private SessionTab? _current;

    /// <summary>Pas de défilement d'un clic sur une flèche, en pixels.</summary>
    private const double TabScrollStep = 180;

    private SessionsWindow()
    {
        InitializeComponent();
        TitleBar.MarkInteractive(TabStrip);

        // Navigation entre sessions, prioritaire sur la session affichée.
        AddHandler(KeyDownEvent, SessionsWindow_KeyDown, RoutingStrategies.Tunnel);

        TabScroller.PointerWheelChanged += (_, e) =>
        {
            if (TabScroller.Extent.Width <= TabScroller.Viewport.Width) return;
            TabScroller.Offset = TabScroller.Offset.WithX(TabScroller.Offset.X - e.Delta.Y * 60);
            e.Handled = true;
        };
        TabScroller.ScrollChanged += (_, _) => UpdateTabScrollButtons();
        TabScroller.SizeChanged += (_, _) => UpdateTabScrollButtons();

        // Changement d'application : rien ne doit rester enfoncé côté distant.
        Deactivated += (_, _) => ReleaseRemoteKeyboard();

        Closed += (_, _) =>
        {
            // Fermer les sessions sous-jacentes, sinon elles resteraient actives
            // sans interface pour les piloter.
            foreach (var tab in _tabs.ToList())
            {
                tab.Window.SetHost(null);
                try { tab.Window.Close(); } catch { /* déjà fermée */ }
            }
            _tabs.Clear();
            _instance = null;
        };
    }

    /// <summary>Vrai si la fenêtre est affichée dans un onglet.</summary>
    public static bool Hosts(ChromeWindow window) => _instance?._tabs.Any(t => t.Window == window) == true;

    /// <summary>Ramène au premier plan l'onglet d'une session déjà ouverte.</summary>
    public static void Activate(ChromeWindow window)
    {
        if (_instance is not { } sessions || sessions._tabs.FirstOrDefault(t => t.Window == window) is not { } tab)
        {
            window.Activate();
            return;
        }
        if (sessions.WindowState == WindowState.Minimized) sessions.WindowState = WindowState.Normal;
        sessions.Select(tab);
        sessions.Activate();
    }

    /// <summary>
    /// Ajoute une session dans un onglet. La fenêtre passée n'est jamais
    /// affichée : seul son contenu est repris.
    /// </summary>
    public static void AddSession(ChromeWindow sessionWindow, string title, string glyph)
    {
        _instance ??= new SessionsWindow();
        var sessions = _instance;

        // Détacher le contenu visuel de sa fenêtre d'origine : un contrôle ne
        // peut avoir qu'un seul parent.
        if (sessionWindow.Content is not Control root) return;
        sessionWindow.Content = null;

        var tab = new SessionTab
        {
            Window = sessionWindow,
            Root = root,
            Title = title,
            Glyph = glyph,
            Button = sessions.CreateTabButton(title, glyph),
        };
        tab.Button.Click += (_, _) => sessions.Select(tab);
        tab.Button.PointerPressed += (_, e) =>
        {
            // Clic du milieu : l'onglet se ferme, comme dans un navigateur.
            if (e.GetCurrentPoint(tab.Button).Properties.IsMiddleButtonPressed)
            {
                e.Handled = true;
                sessions.CloseTab(tab);
            }
        };

        sessions._tabs.Add(tab);
        sessions.TabPanel.Children.Add(tab.Button);
        sessionWindow.SetHost(sessions);

        // Quand la session se termine d'elle-même (fermeture demandée par le
        // code), l'onglet doit disparaître aussi.
        sessionWindow.Closed += (_, _) => sessions.RemoveTab(tab);

        sessions.Select(tab);
        sessions.Show();
        sessions.Activate();

        // La fenêtre porteuse n'étant jamais affichée, Opened ne se déclenche
        // pas : c'est à nous de lancer la session.
        if (sessionWindow is ISessionWindow session)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                try { await session.StartSessionAsync(); }
                catch (Exception ex) { Log.Error($"Démarrage de la session « {title} » impossible", ex, "Sessions"); }
            });
        }
    }

    private Button CreateTabButton(string title, string glyph)
    {
        var close = new Button { Content = new TextBlock { Text = "✕", FontSize = 9 } };
        close.Classes.Add("tabClose");
        ToolTip.SetTip(close, "Fermer cet onglet (Ctrl+F4, ou clic du milieu)");

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, FontSize = 12, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        // Titre tronqué : un nom de machine à rallonge occupait sinon la moitié du bandeau.
        content.Children.Add(new TextBlock { Text = title, MaxWidth = 220, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(close);

        var button = new Button { Content = content };
        button.Classes.Add("sessionTab");
        ToolTip.SetTip(button, title);
        WindowDecorationProperties.SetElementRole(button, WindowDecorationsElementRole.User);
        WindowDecorationProperties.SetElementRole(close, WindowDecorationsElementRole.User);

        close.Click += (_, e) =>
        {
            e.Handled = true;   // ne pas sélectionner l'onglet qu'on ferme
            if (_tabs.FirstOrDefault(t => t.Button == button) is { } tab) CloseTab(tab);
        };
        return button;
    }

    // MARK: - Sélection

    private void Select(SessionTab tab)
    {
        if (_current == tab)
        {
            FocusCurrentContent();
            return;
        }

        // La session que l'on quitte a pu recevoir « Ctrl enfoncé » avant que
        // l'on intercepte la frappe : son clavier distant resterait bloqué.
        ReleaseRemoteKeyboard();

        SessionHost.Children.Clear();
        _current = tab;
        SessionHost.Children.Add(tab.Root);
        foreach (var other in _tabs) other.Button.Classes.Set("selected", other == tab);

        UpdateLayoutForCount();
        FocusCurrentContent();
        Dispatcher.UIThread.Post(() => tab.Button.BringIntoView(), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Une session : elle garde sa propre barre (titre et barre d'outils),
    /// comme en fenêtre autonome. Plusieurs : le bandeau d'onglets occupe la
    /// barre de titre et chaque session ne garde que sa barre d'outils.
    /// </summary>
    private void UpdateLayoutForCount()
    {
        var multiple = _tabs.Count > 1;
        var full = WindowState == WindowState.FullScreen;
        WindowTitleBar.IsVisible = multiple && !full;
        foreach (var tab in _tabs)
            if (tab.Window is ISessionWindow session) session.SessionTitleBar.IsEmbedded = multiple;

        Title = _current is null ? "Sessions — Mesh Control" : $"{_current.Title} — Mesh Control";
        WindowTitleBar.Title = Title;
        UpdateTabScrollButtons();
    }

    protected override void OnHostWindowStateChanged()
    {
        // Plein écran d'une session : le bandeau d'onglets ne doit pas rester
        // posé en haut de l'image.
        if (WindowTitleBar is not null) UpdateLayoutForCount();
    }

    /// <summary>Donne le focus clavier au contenu de l'onglet actif.</summary>
    private void FocusCurrentContent()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_current?.Window is ISessionWindow session) session.FocusTarget.Focus();
        }, DispatcherPriority.Input);
    }

    private void ReleaseRemoteKeyboard()
    {
        if (_current?.Window is ISessionWindow session) session.ReleaseKeyboard();
    }

    // MARK: - Navigation entre onglets

    /// <summary>
    /// Ctrl+Tab et Ctrl+Maj+Tab pour passer d'une session à l'autre, Ctrl+1 à
    /// Ctrl+9 pour aller directement à l'une d'elles, Ctrl+F4 pour fermer la
    /// session affichée. Rien n'est capté tant qu'une seule session est
    /// ouverte : la frappe garde alors tout son sens côté distant.
    /// </summary>
    private void SessionsWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_tabs.Count < 2 || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        switch (e.Key)
        {
            case Key.Tab:
                MoveSelection(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : +1);
                break;
            case Key.F4:
                if (_current is { } current)
                {
                    ReleaseRemoteKeyboard();
                    CloseTab(current);
                }
                break;
            case >= Key.D1 and <= Key.D9:
                SelectByPosition(e.Key - Key.D1);
                break;
            default:
                return;   // touche qui ne nous concerne pas : elle suit son chemin
        }
        e.Handled = true;
    }

    /// <summary>Onglet suivant ou précédent, en bouclant.</summary>
    private void MoveSelection(int delta)
    {
        if (_current is null || _tabs.Count == 0) return;
        var index = _tabs.IndexOf(_current) + delta;
        Select(_tabs[(index % _tabs.Count + _tabs.Count) % _tabs.Count]);
    }

    /// <summary>Ctrl+9 va au dernier, comme dans les navigateurs.</summary>
    private void SelectByPosition(int index)
    {
        var target = index >= 8 ? _tabs.Count - 1 : index;
        if (target >= 0 && target < _tabs.Count) Select(_tabs[target]);
    }

    // MARK: - Fermeture d'onglet

    private void CloseTab(SessionTab tab)
    {
        if (!_tabs.Contains(tab)) return;
        // Fermer la fenêtre porteuse arrête la session ; son Closed retire l'onglet.
        try { tab.Window.Close(); } catch { RemoveTab(tab); }
    }

    /// <summary>Retire l'onglet d'une session terminée.</summary>
    private void RemoveTab(SessionTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        _tabs.RemoveAt(index);
        TabPanel.Children.Remove(tab.Button);
        tab.Window.SetHost(null);
        if (_current == tab)
        {
            SessionHost.Children.Clear();
            _current = null;
        }

        // Plus aucune session : la fenêtre n'a plus de raison d'être.
        if (_tabs.Count == 0)
        {
            Close();
            return;
        }
        if (_current is null) Select(_tabs[Math.Min(index, _tabs.Count - 1)]);
        else UpdateLayoutForCount();
    }

    // MARK: - Défilement du bandeau

    private void UpdateTabScrollButtons()
    {
        var overflow = TabScroller.Extent.Width > TabScroller.Viewport.Width + 0.5;
        ScrollTabsLeft.IsVisible = ScrollTabsRight.IsVisible = overflow;
        ScrollTabsLeft.IsEnabled = TabScroller.Offset.X > 0.5;
        ScrollTabsRight.IsEnabled = TabScroller.Offset.X < TabScroller.Extent.Width - TabScroller.Viewport.Width - 0.5;
    }

    private void ScrollTabsLeft_Click(object? sender, RoutedEventArgs e)
        => TabScroller.Offset = TabScroller.Offset.WithX(Math.Max(0, TabScroller.Offset.X - TabScrollStep));

    private void ScrollTabsRight_Click(object? sender, RoutedEventArgs e)
        => TabScroller.Offset = TabScroller.Offset.WithX(TabScroller.Offset.X + TabScrollStep);
}
