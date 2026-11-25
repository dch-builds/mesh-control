using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>
/// Fenêtre unique regroupant les sessions en onglets.
///
/// Principe : chaque session reste une fenêtre à part entière (avec toute sa
/// logique), mais son contenu visuel est détaché et placé dans un onglet. La
/// fenêtre d'origine n'est jamais affichée ; elle sert de porteuse pour la
/// session et ses gestionnaires d'événements. Cela évite de dupliquer le code
/// des bureaux, terminaux et navigateurs de fichiers.
/// </summary>
public partial class SessionsWindow : ChromeWindow
{
    /// <summary>Instance unique : toutes les sessions se regroupent ici.</summary>
    private static SessionsWindow? _instance;

    /// <summary>Fenêtre porteuse associée à chaque onglet, pour la fermer proprement.</summary>
    private readonly Dictionary<TabItem, Window> _hosts = new();

    private SessionsWindow()
    {
        InitializeComponent();

        // Navigation entre sessions : voir SessionsWindow_PreviewKeyDown.
        PreviewKeyDown += SessionsWindow_PreviewKeyDown;
        // Sur la fenêtre et non sur Tabs : avec plusieurs sessions, le bandeau
        // d'onglets vit dans la barre de titre, hors du TabControl.
        PreviewMouseDown += Tabs_PreviewMouseDown;

        Closed += (_, _) =>
        {
            // Fermer les sessions sous-jacentes, sinon elles resteraient actives
            // sans interface pour les piloter.
            foreach (var host in _hosts.Values.ToList())
            {
                try { host.Close(); } catch { /* déjà fermée */ }
            }
            _hosts.Clear();
            _instance = null;
        };
    }

    /// <summary>
    /// Ajoute une session dans un onglet. La fenêtre passée n'est jamais
    /// affichée : seul son contenu est repris.
    /// </summary>
    public static void AddSession(Window sessionWindow, string title, string glyph)
    {
        _instance ??= new SessionsWindow();

        // Détacher le contenu visuel de sa fenêtre d'origine : un élément WPF ne
        // peut avoir qu'un seul parent.
        var content = sessionWindow.Content as UIElement;
        sessionWindow.Content = null;

        var tab = new TabItem
        {
            Header = title,
            Content = content,
            ToolTip = title,
            Tag = glyph,     // affiché devant le titre par le modèle d'onglet
        };

        _instance._hosts[tab] = sessionWindow;
        _instance.Tabs.Items.Add(tab);
        _instance.Tabs.SelectedItem = tab;

        // Quand la session se termine d'elle-même (connexion perdue, fermeture
        // demandée par le code), l'onglet doit disparaître aussi.
        sessionWindow.Closed += (_, _) => _instance?.RemoveTab(tab);

        _instance.Show();
        _instance.Activate();
        _instance.UpdateTitle();

        // Rendre le focus au contenu : sans cela, les frappes clavier de la
        // session n'arriveraient pas.
        _instance.FocusCurrentContent();

        // La fenêtre porteuse n'étant jamais affichée, son événement Loaded ne
        // se déclenche pas : c'est à nous de lancer la session.
        if (sessionWindow is ISessionWindow session)
        {
            _ = _instance.Dispatcher.BeginInvoke(async () =>
            {
                try { await session.StartSessionAsync(); }
                catch (Exception ex)
                {
                    Log.Error($"Démarrage de la session « {title} » impossible", ex, "Sessions");
                }
            });
        }
    }

    // MARK: - Navigation entre onglets

    /// <summary>
    /// Raccourcis de la fenêtre à onglets : Ctrl+Tab et Ctrl+Maj+Tab pour passer
    /// d'une session à l'autre, Ctrl+1 à Ctrl+9 pour aller directement à l'une
    /// d'elles, Ctrl+F4 pour fermer la session affichée.
    ///
    /// Traités ici plutôt que dans les sessions : les événements « Preview »
    /// descendent de la fenêtre vers son contenu, ce qui nous laisse la priorité
    /// sur le bureau à distance. Sans cela, Ctrl+Tab partirait vers la machine
    /// distante au lieu de changer d'onglet.
    ///
    /// Rien n'est capté tant qu'une seule session est ouverte : la frappe garde
    /// alors tout son sens côté distant (Ctrl+Tab dans un navigateur, Ctrl+F4
    /// pour fermer un document), et il n'y a de toute façon aucun voisin où aller.
    /// </summary>
    private void SessionsWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Tabs.Items.Count < 2) return;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        switch (e.Key)
        {
            case Key.Tab:
                MoveSelection(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : +1);
                break;

            case Key.F4:
                if (Tabs.SelectedItem is TabItem current)
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

    /// <summary>
    /// Va à l'onglet suivant ou précédent, en bouclant : la navigation ne doit
    /// pas buter sur le bout de la rangée.
    /// </summary>
    private void MoveSelection(int delta)
    {
        var count = Tabs.Items.Count;
        if (count == 0) return;

        var index = Tabs.SelectedIndex + delta;
        SelectTab((index % count + count) % count);
    }

    /// <summary>
    /// Sélectionne l'onglet à cette position. Ctrl+9 va au dernier, comme dans
    /// les navigateurs, plutôt que de ne rien faire tant qu'il y a moins de neuf
    /// sessions ouvertes.
    /// </summary>
    private void SelectByPosition(int index)
    {
        var target = index >= 8 ? Tabs.Items.Count - 1 : index;
        if (target < 0 || target >= Tabs.Items.Count) return;
        SelectTab(target);
    }

    private void SelectTab(int index)
    {
        // La session que l'on quitte a reçu « Ctrl enfoncé » avant que l'on
        // intercepte la frappe : sans ce nettoyage, son clavier distant resterait
        // bloqué en Ctrl jusqu'au prochain retour sur l'onglet.
        ReleaseRemoteKeyboard();
        Tabs.SelectedIndex = index;   // le changement redonne le focus au contenu
    }

    /// <summary>Relâche les touches maintenues sur la machine de la session affichée.</summary>
    private void ReleaseRemoteKeyboard()
    {
        if (Tabs.SelectedItem is TabItem tab
            && _hosts.TryGetValue(tab, out var host)
            && host is ISessionWindow session)
        {
            session.ReleaseKeyboard();
        }
    }

    /// <summary>
    /// Clic du milieu sur un onglet : il se ferme, comme dans un navigateur. Un
    /// clic du milieu dans le contenu d'une session n'est pas concerné — celui-ci
    /// n'appartient pas à un onglet, et la frappe continue vers la machine.
    /// </summary>
    private void Tabs_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindParent<TabItem>(source) is not { } tab) return;

        e.Handled = true;
        CloseTab(tab);
    }

    // MARK: - Fermeture d'onglet

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        // Le bouton vit dans le modèle de l'onglet : on remonte l'arbre visuel
        // pour retrouver le TabItem auquel il appartient.
        if (sender is DependencyObject start && FindParent<TabItem>(start) is { } tab)
            CloseTab(tab);
        e.Handled = true;   // ne pas sélectionner l'onglet qu'on ferme
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = System.Windows.Media.VisualTreeHelper.GetParent(child);
        while (current is not null and not T)
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        return current as T;
    }

    private void CloseTab(TabItem tab)
    {
        if (_hosts.TryGetValue(tab, out var host))
        {
            _hosts.Remove(tab);
            // Fermer la fenêtre porteuse arrête la session (son handler Closed
            // libère le tunnel).
            try { host.Close(); } catch { }
        }
        Tabs.Items.Remove(tab);
        UpdateTitle();

        // Plus aucune session : la fenêtre n'a plus de raison d'être.
        if (Tabs.Items.Count == 0) Close();
    }

    /// <summary>Retire un onglet dont la session s'est terminée d'elle-même.</summary>
    private void RemoveTab(TabItem tab)
    {
        if (!_hosts.ContainsKey(tab)) return;   // déjà retiré par CloseTab
        _hosts.Remove(tab);
        Dispatcher.BeginInvoke(() =>
        {
            Tabs.Items.Remove(tab);
            UpdateTitle();
            if (Tabs.Items.Count == 0) Close();
        });
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        UpdateTitle();
        FocusCurrentContent();
        ScrollSelectedTabIntoView();
    }

    // MARK: - Défilement du bandeau d'onglets

    /// <summary>Pas de défilement d'un clic sur une flèche, en pixels.</summary>
    private const double TabScrollStep = 180;

    /// <summary>Zone défilante du bandeau, une fois le gabarit appliqué.</summary>
    private ScrollViewer? _tabScroller;

    /// <summary>
    /// Branche le défilement du bandeau : molette sur les onglets, flèches
    /// affichées seulement quand ils débordent.
    /// </summary>
    private void HookTabScroller()
    {
        if (_tabScroller is not null) return;
        if (Tabs.Template?.FindName("TabScroller", Tabs) is not ScrollViewer scroller) return;

        _tabScroller = scroller;
        scroller.ScrollChanged += (_, _) => UpdateTabScrollButtons();
        // La molette fait défiler la rangée horizontalement : sans cela, elle
        // ne ferait rien, le défilement vertical étant désactivé.
        scroller.PreviewMouseWheel += (_, e) =>
        {
            if (scroller.ScrollableWidth <= 0) return;
            scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        UpdateTabScrollButtons();
    }

    private void UpdateTabScrollButtons()
    {
        if (_tabScroller is not { } scroller) return;
        if (Tabs.Template?.FindName("ScrollTabsLeft", Tabs) is not UIElement left) return;
        if (Tabs.Template?.FindName("ScrollTabsRight", Tabs) is not UIElement right) return;

        var overflow = scroller.ScrollableWidth > 0.5;
        left.Visibility = right.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        left.IsEnabled = scroller.HorizontalOffset > 0.5;
        right.IsEnabled = scroller.HorizontalOffset < scroller.ScrollableWidth - 0.5;
    }

    private void ScrollTabsLeft_Click(object sender, RoutedEventArgs e) =>
        _tabScroller?.ScrollToHorizontalOffset(_tabScroller.HorizontalOffset - TabScrollStep);

    private void ScrollTabsRight_Click(object sender, RoutedEventArgs e) =>
        _tabScroller?.ScrollToHorizontalOffset(_tabScroller.HorizontalOffset + TabScrollStep);

    /// <summary>
    /// Amène l'onglet actif dans la partie visible du bandeau : Ctrl+Tab ou
    /// l'ouverture d'une session ne doivent pas sélectionner un onglet caché.
    /// Différé : l'onglet tout juste ajouté n'est pas encore mesuré.
    /// </summary>
    private void ScrollSelectedTabIntoView() =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            HookTabScroller();
            (Tabs.SelectedItem as TabItem)?.BringIntoView();
        }), System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>
    /// Donne le focus clavier au contenu de l'onglet actif. Sans cela, les
    /// frappes n'atteindraient pas la session : ce sont les panneaux racines qui
    /// portent les gestionnaires, pas la fenêtre.
    /// </summary>
    private void FocusCurrentContent()
    {
        // Différé : au moment du changement d'onglet, WPF n'a pas encore
        // rattaché le contenu à l'arbre visuel, et Focus() échouerait.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Tabs.SelectedContent is not UIElement content) return;
            content.Focus();
            Keyboard.Focus(content);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void UpdateTitle()
    {
        var current = (Tabs.SelectedItem as TabItem)?.Header as string;
        Title = string.IsNullOrEmpty(current)
            ? "Sessions — Mesh Control"
            : $"{current} — Mesh Control";

        UpdateTabStripVisibility();
    }

    /// <summary>Vrai quand une session en plein écran demande de tout masquer.</summary>
    private bool _tabStripHidden;

    /// <summary>
    /// Masque ou rétablit le bandeau d'onglets. Utilisé par le plein écran d'une
    /// session : sans cela, le bandeau resterait posé en haut de l'image.
    /// </summary>
    public void SetTabStripVisible(bool visible)
    {
        _tabStripHidden = !visible;
        UpdateTabStripVisibility();
    }

    /// <summary>
    /// Masque le bandeau d'onglets quand une seule session est ouverte : un
    /// onglet unique n'apporte rien et grignoterait de la hauteur utile.
    /// </summary>
    private void UpdateTabStripVisibility()
    {
        // Le bandeau vit dans le modèle du TabControl : il faut attendre que
        // celui-ci soit appliqué pour le retrouver.
        if (Tabs.Template?.FindName("TabStrip", Tabs) is not UIElement strip)
        {
            // Modèle pas encore appliqué : réessayer après la passe de rendu.
            Dispatcher.BeginInvoke(new Action(UpdateTabStripVisibility),
                System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        strip.Visibility = !_tabStripHidden && Tabs.Items.Count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;

        HookTabScroller();

        if (strip is Border border) UpdateTitleBar(border);
    }

    /// <summary>Session dont la barre d'outils occupe la barre de titre.</summary>
    private ISessionWindow? _mergedSession;

    /// <summary>Panneau du gabarit d'où vient le bandeau d'onglets, pour l'y remettre.</summary>
    private Panel? _tabStripHome;

    /// <summary>
    /// Choisit ce qu'occupe la barre de titre :
    /// - plusieurs sessions : le bandeau d'onglets, comme un navigateur — une
    ///   bande de moins, la barre d'outils de chaque session restant dessous ;
    /// - une seule : la barre d'outils de la session, comme en fenêtre
    ///   autonome. Elle regagne sa session dès qu'un second onglet s'ouvre,
    ///   sinon on verrait celle d'une session en en regardant une autre.
    /// </summary>
    private void UpdateTitleBar(Border strip)
    {
        var multiple = Tabs.Items.Count > 1;

        ISessionWindow? session = null;
        if (Tabs.Items.Count == 1
            && Tabs.Items[0] is TabItem only
            && _hosts.TryGetValue(only, out var host))
        {
            session = host as ISessionWindow;
        }

        var current = GetTitleBarContent(this);
        var upToDate = multiple
            ? ReferenceEquals(current, strip)
            : session is not null && ReferenceEquals(session, _mergedSession);
        if (upToDate) return;

        // Libérer la barre de titre avant d'y loger autre chose : chaque élément
        // doit retrouver sa place d'origine.
        SetTitleBarContent(this, null);
        if (_mergedSession is not null)
        {
            _mergedSession.RestoreToolbar();
            _mergedSession = null;
        }
        if (ReferenceEquals(current, strip)) RestoreTabStrip(strip);

        if (multiple)
        {
            if (DetachTabStrip(strip)) SetTitleBarContent(this, strip);
        }
        else if (session?.DetachToolbar() is { } toolbar)
        {
            SetTitleBarContent(this, toolbar);
            _mergedSession = session;
        }
    }

    /// <summary>
    /// Sort le bandeau d'onglets du gabarit du TabControl. Le TabPanel reste
    /// relié à ses onglets : ce lien passe par le parent de gabarit, que le
    /// déplacement ne change pas.
    /// </summary>
    private bool DetachTabStrip(Border strip)
    {
        if (System.Windows.Media.VisualTreeHelper.GetParent(strip) is not Panel home)
            return false;

        _tabStripHome = home;
        home.Children.Remove(strip);
        // Fond de la barre de titre ; les onglets, en pastilles, centrés.
        strip.Background = System.Windows.Media.Brushes.Transparent;
        strip.VerticalAlignment = VerticalAlignment.Center;
        return true;
    }

    private void RestoreTabStrip(Border strip)
    {
        if (_tabStripHome is not { } home) return;

        // La barre de titre ne lâche l'élément qu'à la passe de mise en page
        // suivante : WPF refuserait un élément qui a encore un parent.
        if (System.Windows.Media.VisualTreeHelper.GetParent(strip) is not null) UpdateLayout();
        if (System.Windows.Media.VisualTreeHelper.GetParent(strip) is not null) return;

        strip.ClearValue(Border.BackgroundProperty);
        strip.ClearValue(VerticalAlignmentProperty);
        // En tête : le DockPanel du gabarit range ses enfants dans l'ordre.
        home.Children.Insert(0, strip);
        _tabStripHome = null;
    }
}
