using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MeshControl.Core;

namespace MeshControl.Desktop;

public partial class DesktopWindow : MeshControl.UI.ChromeWindow, MeshControl.UI.ISessionWindow
{
    /// <summary>Empêche un double démarrage (Loaded et hôte d'onglet).</summary>
    private bool _sessionStarted;

    /// <summary>
    /// Fenêtre à utiliser comme propriétaire des boîtes de dialogue. En mode
    /// onglets, cette fenêtre n'est jamais affichée : WPF refuserait alors de
    /// l'accepter comme propriétaire. On remonte donc à la fenêtre qui héberge
    /// réellement le contenu.
    /// </summary>
    private Window DialogOwner
    {
        get
        {
            if (IsLoaded && IsVisible) return this;
            // Le contenu a migré dans un onglet : retrouver sa fenêtre hôte.
            var host = RootPanel is null ? null : Window.GetWindow(RootPanel);
            return host ?? Application.Current.MainWindow ?? this;
        }
    }

    /// <summary>
    /// Ne se déclenche que si la fenêtre s'affiche elle-même : en mode onglets,
    /// elle n'est jamais montrée et la barre d'outils reste dans son panneau.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        MeshControl.UI.ChromeWindow.SetTitleBarContent(this, DetachToolbar());
    }

    /// <summary>Apparence d'origine de la barre, rétablie à son retour dans le panneau.</summary>
    private System.Windows.Media.Brush? _toolbarBackground;
    private Thickness _toolbarPadding;
    private System.Windows.Media.Effects.Effect? _toolbarEffect;

    /// <summary>
    /// Sort la barre d'outils de son panneau pour la loger dans une barre de
    /// titre : une seule bande de 44 px au lieu de deux, l'image distante gagne
    /// une cinquantaine de pixels en hauteur. Le titre de la fenêtre faisait
    /// doublon avec le nom de la machine affiché par la barre.
    ///
    /// Sert en fenêtre autonome, et en mode onglets tant qu'une seule session
    /// est ouverte (voir SessionsWindow).
    /// </summary>
    public UIElement? DetachToolbar()
    {
        if (Toolbar.Parent != RootPanel) return Toolbar;

        _toolbarBackground = Toolbar.Background;
        _toolbarPadding = Toolbar.Padding;
        _toolbarEffect = Toolbar.Effect;

        RootPanel.Children.Remove(Toolbar);
        // La barre de titre fournit déjà fond, séparateur et marges.
        Toolbar.Background = System.Windows.Media.Brushes.Transparent;
        Toolbar.Padding = new Thickness(0, 0, 4, 0);
        Toolbar.Effect = null;
        Toolbar.VerticalAlignment = VerticalAlignment.Center;

        // Hors de RootPanel, les frappes faites quand un bouton de la barre a
        // le focus ne passeraient plus par ses gestionnaires : les raccourcis
        // Ctrl+Maj+… et la saisie vers la machine distante seraient perdus.
        // Abonnés ici seulement : dans le panneau, RootPanel les traite déjà.
        Toolbar.PreviewKeyDown += Content_PreviewKeyDown;
        Toolbar.PreviewKeyUp += Content_PreviewKeyUp;
        return Toolbar;
    }

    /// <summary>Ramène la barre d'outils dans son panneau, en tête.</summary>
    public void RestoreToolbar()
    {
        if (Toolbar.Parent == RootPanel) return;

        // La barre de titre qui l'hébergeait ne la lâche qu'à la passe de mise
        // en page suivante : WPF refuserait un élément qui a encore un parent.
        if (System.Windows.Media.VisualTreeHelper.GetParent(Toolbar) is not null)
            Window.GetWindow(Toolbar)?.UpdateLayout();
        if (System.Windows.Media.VisualTreeHelper.GetParent(Toolbar) is not null)
            return;

        Toolbar.PreviewKeyDown -= Content_PreviewKeyDown;
        Toolbar.PreviewKeyUp -= Content_PreviewKeyUp;

        Toolbar.Background = _toolbarBackground;
        Toolbar.Padding = _toolbarPadding;
        Toolbar.Effect = _toolbarEffect;
        Toolbar.ClearValue(VerticalAlignmentProperty);

        // En tête : le DockPanel range ses enfants dans l'ordre, et la barre
        // doit passer avant la barre d'état et la zone d'affichage.
        RootPanel.Children.Insert(0, Toolbar);
    }

    /// <summary>Démarre la session ; sans effet si elle l'est déjà.</summary>
    public async Task StartSessionAsync()
    {
        if (_sessionStarted) return;
        _sessionStarted = true;
        await _session.StartAsync();
    }

    private readonly DesktopSession _session;

    /// <summary>Surface WPF de la session (WriteableBitmap), créée avec elle.</summary>
    private readonly KVMFramebuffer _framebuffer = new();
    private readonly string _deviceName;
    private readonly MeshNode _node;
    // Le presse-papiers distant passe par le canal de contrôle du serveur, et non
    // par le tunnel KVM : la fenêtre a donc besoin de la connexion.
    private readonly MeshServerConnection _connection;
    private readonly AppSettings _settings;

    /// <param name="settings">
    /// Réglages de l'application : fournissent la qualité, la fluidité et le mode
    /// d'ajustement appliqués à l'ouverture de la session.
    /// </param>
    public DesktopWindow(MeshServerConnection connection, MeshNode node,
                         AppSettings? settings = null)
    {
        _deviceName = node.Name;
        _node = node;
        _connection = connection;
        _settings = settings ?? AppSettings.Load();
        // IMPORTANT : créer _session AVANT InitializeComponent. Le chargement du
        // XAML déclenche QualityCombo_SelectionChanged (via IsSelected="True"),
        // qui accède à _session ; il doit donc déjà exister.
        _session = new DesktopSession(connection, node, new UI.WpfUiDispatcher(Dispatcher), _framebuffer);

        InitializeComponent();
        // L'arbre visuel existe : les handlers peuvent désormais y toucher.
        _uiReady = true;
        _fullscreen = new UI.SessionFullscreen(RootPanel, FullscreenHint, Toolbar, StatusBar);
        _fullscreen.Changed += OnFullscreenChanged;
        ApplyDefaultsFromSettings();
        ApplyFitMode();
        InitializeClipboardSync();
        InitializeFullscreenBar();
        InitializeToolbarLayout();
        ApplyOpenModeOnFirstDisplay();
        UpdateInputControls();

        TitleText.Text = node.Name;
        _session.FrameUpdated += OnFrameUpdated;
        _session.ScreenSizeChanged += OnScreenSizeChanged;
        _session.StateChanged += OnStateChanged;
        _session.DisplaysChanged += OnDisplaysChanged;
        _session.SlowLinkDetected += OnSlowLinkDetected;

        // En mode onglets, cette fenêtre n'est jamais affichée : Loaded ne se
        // déclencherait pas et la session ne démarrerait jamais. StartSession()
        // est donc appelé explicitement par l'hôte (voir SessionsWindow).
        Loaded += async (_, _) => await StartSessionAsync();
        Closed += (_, _) => _session.Stop();

        // À la perte de focus, les KeyUp des touches encore enfoncées ne nous
        // parviennent plus : sans ce nettoyage, un modificateur resterait bloqué
        // côté distant et le clavier semblerait « cassé » au retour.
        // Attaché au panneau : en mode onglets, la fenêtre n'est jamais activée,
        // donc son événement Deactivated ne se déclencherait pas.
        Deactivated += (_, _) => _session.ReleaseModifiers();
        RootPanel.LostKeyboardFocus += (_, _) => _session.ReleaseModifiers();

        // Prendre le focus dès que le panneau est prêt, pour que le clavier
        // fonctionne sans clic préalable.
        // Sauf en vignette (voir DesktopWindow.Docked) : le clavier y reste au
        // terminal tant qu'on ne clique pas sur l'écran distant, et le
        // panneau se recharge à chaque retour sur l'onglet du terminal.
        RootPanel.Loaded += (_, _) =>
        {
            if (!_docked) RootPanel.Focus();
        };

        // La barre d'état se rafraîchit à intervalle fixe : la recalculer à
        // chaque tuile reçue ferait travailler l'UI des dizaines de fois par
        // seconde pour un affichage que l'œil ne suit pas.
        _statusTimer.Tick += (_, _) =>
        {
            UpdateStatusBar();
            UpdateStreamPause();
        };
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();

        // Réduction de la fenêtre en mode « une fenêtre par session » : la
        // réaction est alors immédiate, sans attendre le tick suivant.
        StateChanged += (_, _) => UpdateStreamPause();

        // En mode onglets, la fenêtre hôte a pu changer d'état pendant qu'une
        // autre session était affichée : on se réaligne en revenant ici.
        // En vignette, le plein écran de la fenêtre hôte est celui du
        // terminal : la vignette ne doit pas s'y aligner.
        RootPanel.IsVisibleChanged += (_, _) =>
        {
            if (!_docked) _fullscreen.Sync();
            UpdateStreamPause();
        };

        // Fermer la session en plein écran laisserait la fenêtre hôte sans
        // bordure ni barre d'onglets, y compris pour les autres sessions.
        Closed += (_, _) => _fullscreen.Exit();

        // La loupe est exprimée en fraction de l'image : elle se recalcule quand
        // la fenêtre ou l'image changent de taille.
        FitHost.SizeChanged += (_, _) => ApplyZoom();
        ScreenImage.SizeChanged += (_, _) => ApplyZoom();

        Closed += (_, _) =>
        {
            StopWatchingReboot();
            _dropCts?.Cancel();
        };
    }

    /// <summary>Cadence de rafraîchissement de la barre d'état.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };

    /// <summary>
    /// Met à jour latence, débit, résolution et volume reçu. Les valeurs absentes
    /// s'affichent en tiret plutôt que de laisser un vide inexpliqué.
    /// </summary>
    private void UpdateStatusBar()
    {
        if (!_uiReady) return;

        StatusLatency.Text = _session.LatencyMs is { } ms
            ? $"Latence {ms} ms"
            : "Latence —";

        var kbps = _session.ThroughputKbps;
        StatusThroughput.Text = kbps >= 1024
            ? $"Débit {kbps / 1024:0.0} Mo/s"
            : $"Débit {kbps:0} Ko/s";

        // À échelle réduite, ces dimensions sont celles de l'image reçue, pas
        // celles de l'écran distant : le préciser évite de croire à une
        // mauvaise détection de résolution.
        StatusResolution.Text = _session.ScreenWidth > 0
            ? _session.EffectiveScalePercent == 100
                ? $"{_session.ScreenWidth} × {_session.ScreenHeight}"
                : $"{_session.ScreenWidth} × {_session.ScreenHeight} · {_session.EffectiveScalePercent} %"
            : "Résolution —";

        var total = _session.BytesReceived;
        StatusTotal.Text = total >= 1024L * 1024
            ? $"{total / 1024.0 / 1024.0:0.0} Mo reçus"
            : $"{total / 1024} Ko reçus";
    }

    /// <summary>Vrai quand le flux d'images a été suspendu faute de spectateur.</summary>
    private bool _streamPaused;

    /// <summary>
    /// Suspend le flux d'images quand personne ne le regarde : onglet passé à
    /// l'arrière-plan, fenêtre réduite. Une session laissée ouverte continuait
    /// sinon de recevoir l'écran complet à la cadence demandée — trois sessions
    /// ouvertes, c'étaient trois flux pour un seul écran réellement regardé.
    ///
    /// L'état est relu au tick de la barre d'état plutôt que surveillé par
    /// abonnement : réduire une fenêtre ne rend pas son contenu invisible au
    /// sens de WPF, et la fenêtre hôte change dès que la session passe en
    /// onglet, ce qui obligerait à défaire et refaire l'abonnement.
    /// </summary>
    private void UpdateStreamPause()
    {
        // Avant le démarrage, le contenu n'est pas encore à l'écran : sans ce
        // garde, la session s'ouvrirait sur une pause immédiatement défaite.
        if (!_uiReady || !_sessionStarted) return;

        var host = Window.GetWindow(RootPanel);
        var watched = RootPanel.IsVisible && host?.WindowState != WindowState.Minimized;
        if (!watched == _streamPaused) return;

        _streamPaused = !watched;
        // La reprise redemande une image complète : l'agent n'envoie que les
        // tuiles modifiées, et celles d'une pause ne reviendraient jamais.
        _session.SetPaused(_streamPaused);
    }

    /// <summary>
    /// Positionne les contrôles selon les préférences enregistrées. Les
    /// gestionnaires de sélection appliquent ensuite les valeurs à la session.
    /// </summary>
    private void ApplyDefaultsFromSettings()
    {
        QualityCombo.SelectedItem = QualityItemFor(QualityCombo, _settings.DefaultDesktopQuality);

        var frameRate = DesktopSession.NearestFrameRate(_settings.DefaultDesktopFrameRate);
        FrameRateCombo.SelectedItem = FrameRateCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => FrameRateOf(item) == frameRate);

        FitCheck.IsChecked = _settings.DefaultDesktopFit;
        UpdateModeControls();

        // Avant tout envoi : la valeur part avec les premiers réglages de
        // compression, sans provoquer un second aller-retour.
        _session.ScalePercent = _settings.DefaultDesktopScale;
    }

    // MARK: - Événements de session

    private void OnFrameUpdated()
    {
        // Le WriteableBitmap est déjà mis à jour ; on s'assure juste que les
        // Image pointent dessus (une seule fois) et on rafraîchit les stats.
        if (ScreenImage.Source is null && _framebuffer.Bitmap is not null)
            ApplyFitMode();
        // Les compteurs vivent désormais dans la barre d'état ; StatsText est
        // réservé aux messages ponctuels (« Copié », « Envoyé : … »).
    }

    private void OnScreenSizeChanged()
    {
        ApplyFitMode();
        // La résolution s'affiche dans la barre d'état ; ResolutionText ne porte
        // plus que l'état de la connexion (reconnexion, session fermée).
        UpdateStatusBar();
    }

    private void OnStateChanged()
    {
        switch (_session.State)
        {
            case DesktopSessionState.Connected:
                StatusDot.Fill = (System.Windows.Media.Brush)FindResource("OnlineBrush");
                ReconnectButton.Visibility = Visibility.Collapsed;
                // Effacer le message de coupure : la session est rétablie.
                ResolutionText.Text = "";
                UpdateStatusBar();
                break;

            case DesktopSessionState.Reconnecting:
                StatusDot.Fill = (System.Windows.Media.Brush)FindResource("OfflineBrush");
                ResolutionText.Text = "Reconnexion… "
                    + $"(tentative {_session.ReconnectAttempt}/{_session.ReconnectAttemptsMax})";
                StatsText.Text = "Reconnexion…";
                ReconnectButton.Visibility = Visibility.Collapsed;
                break;

            case DesktopSessionState.Closed:
                StatsText.Text = "Session fermée";
                StatusDot.Fill = (System.Windows.Media.Brush)FindResource("OfflineBrush");
                if (_session.CloseReason is { } reason)
                    ResolutionText.Text = reason;
                // Reprise abandonnée : laisser la main à l'utilisateur.
                ReconnectButton.Visibility = Visibility.Visible;
                break;
        }
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        ReconnectButton.Visibility = Visibility.Collapsed;

        // Session jamais démarrée (échec de la toute première connexion) :
        // RetryNow ne relancerait rien, il faut repasser par le démarrage.
        if (_session.State == DesktopSessionState.Closed && !_sessionStarted)
        {
            _sessionStarted = true;
            await _session.StartAsync();
            return;
        }

        _session.RetryNow();
    }

    // MARK: - Affichage / ajustement

    /// <summary>
    /// Faux tant que InitializeComponent n'a pas fini de construire l'arbre visuel.
    /// Les handlers déclenchés par le XAML (IsChecked="True" sur FitCheck) tombent
    /// alors sur des contrôles encore null.
    /// </summary>
    private bool _uiReady;

    /// <summary>L'image actuellement visible (selon le mode d'affichage).</summary>
    private System.Windows.Controls.Image ActiveImage =>
        FitCheck.IsChecked == true ? ScreenImage : ScreenImageNative;

    /// <summary>
    /// Bascule entre les deux conteneurs. En mode « Ajuster », l'Image est dans
    /// une Grid avec Stretch=Uniform : WPF gère la mise à l'échelle et suit le
    /// redimensionnement de la fenêtre tout seul — pas de recalcul manuel, donc
    /// plus besoin de décocher/recocher l'option.
    /// </summary>
    private void ApplyFitMode()
    {
        if (!_uiReady) return;

        var bmp = _framebuffer.Bitmap;
        bool fit = FitCheck.IsChecked == true;

        FitHost.Visibility = fit ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = fit ? Visibility.Collapsed : Visibility.Visible;

        if (bmp is null) return;
        // Les deux Image partagent le même WriteableBitmap : aucune copie.
        ScreenImage.Source = bmp;
        ScreenImageNative.Source = bmp;

        if (!fit)
        {
            ScreenImageNative.Width = bmp.PixelWidth;
            ScreenImageNative.Height = bmp.PixelHeight;
        }
    }

    private void Fit_Changed(object sender, RoutedEventArgs e)
    {
        // La loupe ne vit que dans la vue ajustée ; en taille réelle, le
        // défilement en tient lieu.
        if (FitCheck.IsChecked != true)
        {
            CancelZoomSelection();
            ResetZoom(quiet: true);
        }
        ApplyFitMode();
    }

    /// <summary>Bascule entre l'image ajustée à la fenêtre et la taille réelle.</summary>
    private void ToggleFit()
    {
        FitCheck.IsChecked = FitCheck.IsChecked != true;
        StatsText.Text = FitCheck.IsChecked == true
            ? "Image ajustée à la fenêtre."
            : "Image à sa taille réelle.";
    }

    // MARK: - Conversion coordonnées écran WPF -> pixels distants

    private bool TryMapToRemote(Point p, out int rx, out int ry)
    {
        rx = ry = 0;
        int rw = _session.ScreenWidth, rh = _session.ScreenHeight;
        if (rw <= 0 || rh <= 0) return false;

        // p est exprimé dans le repère de l'image qui a reçu l'événement.
        // L'image rendue épouse son contenu (Uniform centré, ou taille native),
        // donc un simple rapport d'échelle suffit — pas de letterbox interne.
        var img = ActiveImage;
        double dispW = img.ActualWidth;
        double dispH = img.ActualHeight;
        if (dispW <= 0 || dispH <= 0) return false;

        double lx = p.X / dispW * rw;
        double ly = p.Y / dispH * rh;

        rx = (int)Math.Round(Math.Clamp(lx, 0, rw));
        ry = (int)Math.Round(Math.Clamp(ly, 0, rh));
        return true;
    }

    // MARK: - Souris

    // `sender` est l'Image qui a reçu l'événement : on l'utilise comme repère,
    // ce qui reste correct quel que soit le mode d'affichage actif.
    private void Screen_MouseMove(object sender, MouseEventArgs e)
    {
        if (_viewOnly) return;
        if (TryMapToRemote(e.GetPosition((IInputElement)sender), out int x, out int y))
            _session.SendMouseMove(x, y);
    }

    private void Screen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Donner le focus au panneau racine (et non à la fenêtre) : c'est lui qui
        // porte les gestionnaires clavier, y compris en mode onglets.
        RootPanel.Focus();

        if (_viewOnly) return;

        // Boutons latéraux : le protocole KVM ne transporte que les trois
        // boutons principaux. On les traduit en touches de navigation, celles
        // que Windows produit lui-même pour ces boutons — l'Explorateur et les
        // navigateurs y répondent de la même façon.
        if (e.ChangedButton is System.Windows.Input.MouseButton.XButton1
                            or System.Windows.Input.MouseButton.XButton2)
        {
            _session.SendKeyTap(e.ChangedButton == System.Windows.Input.MouseButton.XButton1
                ? Vk.BrowserBack
                : Vk.BrowserForward);
            return;
        }

        if (!TryMapToRemote(e.GetPosition((IInputElement)sender), out int x, out int y)) return;
        var button = MapButton(e.ChangedButton);
        if (button is null) return;

        // Capturer la souris pour la durée du glisser. Relâché au-dessus de la
        // barre d'outils ou hors de la fenêtre, le bouton ne produirait aucun
        // MouseUp ici : la machine distante le garderait enfoncé, et un
        // glisser-déposer qui dérape laissait l'icône collée au curseur.
        if (sender is UIElement image) image.CaptureMouse();

        _session.SendMouseButton(button.Value, down: true, x, y);
    }

    private void Screen_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // Rendre la capture dès que plus aucun bouton n'est enfoncé : la garder
        // priverait le reste de la fenêtre de la souris.
        if (sender is UIElement image && image.IsMouseCaptured && !AnyMouseButtonPressed)
            image.ReleaseMouseCapture();

        if (_viewOnly) return;
        if (!TryMapToRemote(e.GetPosition((IInputElement)sender), out int x, out int y)) return;
        var button = MapButton(e.ChangedButton);
        if (button is null) return;
        _session.SendMouseButton(button.Value, down: false, x, y);
    }

    private static bool AnyMouseButtonPressed =>
        Mouse.LeftButton == MouseButtonState.Pressed
        || Mouse.RightButton == MouseButtonState.Pressed
        || Mouse.MiddleButton == MouseButtonState.Pressed;

    /// <summary>
    /// Capture perdue autrement que par un relâchement normal : une boîte de
    /// dialogue qui s'ouvre, un changement d'application. Les boutons encore
    /// enfoncés ne recevront jamais leur relâchement, il faut l'envoyer nous-mêmes.
    /// </summary>
    private void Screen_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (AnyMouseButtonPressed) _session.ReleaseMouseButtons();
    }

    private void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_viewOnly) return;
        if (TryMapToRemote(e.GetPosition((IInputElement)sender), out int x, out int y))
            _session.SendScroll(e.Delta, x, y); // WPF Delta déjà en multiples de 120
    }

    private static MouseButton? MapButton(System.Windows.Input.MouseButton b) => b switch
    {
        System.Windows.Input.MouseButton.Left => MeshControl.Desktop.MouseButton.Left,
        System.Windows.Input.MouseButton.Right => MeshControl.Desktop.MouseButton.Right,
        System.Windows.Input.MouseButton.Middle => MeshControl.Desktop.MouseButton.Middle,
        _ => null,
    };

    // MARK: - Clavier

    /// <summary>
    /// Traitement d'une frappe. Attaché au CONTENU et non à la fenêtre : en mode
    /// onglets, la fenêtre porteuse n'est jamais affichée et ne recevrait aucun
    /// événement clavier.
    /// </summary>
    private void Content_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Maj+V est intercepté LOCALEMENT (collage du presse-papiers) et ne
        // doit surtout pas partir vers la machine distante. Ctrl+Maj+V plutôt que
        // Ctrl+V, pour laisser Ctrl+V disponible côté distant.
        var ctrlShift = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                        && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // Échap n'est capté que pendant le tracé de la loupe : le reste du
        // temps, il appartient à la machine distante.
        if (_zoomSelecting && e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelZoomSelection();
            StatsText.Text = "Zoom annulé.";
            return;
        }

        // Ctrl+Maj+Alt+V : collage en simulant la frappe. Avec Alt enfoncé, WPF
        // livre la touche sous Key.System, la vraie lettre étant dans SystemKey.
        if (ctrlShift && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)
            && e.Key == Key.System && e.SystemKey == Key.V)
        {
            e.Handled = true;
            _session.ReleaseModifiers();
            PasteAsTyping_Click(this, new RoutedEventArgs());
            return;
        }

        // Bascule d'écran : uniquement quand la machine en a plusieurs, sinon
        // Ctrl+Maj+flèches doit rester disponible côté distant (sélection de
        // texte par mot, par exemple).
        if (ctrlShift && e.Key is Key.Left or Key.Right && _displayChoices.Count > 0)
        {
            e.Handled = true;
            _session.ReleaseModifiers();
            CycleDisplay(e.Key == Key.Right ? +1 : -1);
            return;
        }

        if (ctrlShift && e.Key is Key.V or Key.C or Key.S or Key.T or Key.E or Key.P or Key.W
                               or Key.F or Key.R or Key.D or Key.A or Key.Z or Key.B
                               or Key.O or Key.Delete)
        {
            e.Handled = true;
            // Ctrl et Maj ont déjà été transmis « enfoncés » par leurs propres
            // événements ; comme on intercepte ici, leur relâchement n'arriverait
            // jamais côté distant et la machine resterait bloquée en Ctrl+Maj.
            _session.ReleaseModifiers();

            switch (e.Key)
            {
                case Key.V: Paste_Click(this, new RoutedEventArgs()); break;
                case Key.C: CopyFromRemote_Click(this, new RoutedEventArgs()); break;
                case Key.S: Screenshot_Click(this, new RoutedEventArgs()); break;
                case Key.T: OpenTerminal_Click(this, new RoutedEventArgs()); break;
                case Key.E: OpenFiles_Click(this, new RoutedEventArgs()); break;
                case Key.P: OpenProcesses_Click(this, new RoutedEventArgs()); break;
                case Key.W: OpenWebPage_Click(this, new RoutedEventArgs()); break;
                case Key.F: ToggleFullscreen(); break;
                case Key.R: Refresh_Click(this, new RoutedEventArgs()); break;
                case Key.D: ReleaseModifiers_Click(this, new RoutedEventArgs()); break;
                case Key.A: ToggleFit(); break;
                case Key.Z: ToggleZoom(); break;
                case Key.B: Reboot_Click(this, new RoutedEventArgs()); break;
                case Key.O: SetViewOnly(!_viewOnly); break;

                // Ctrl+Maj+Suppr pour Ctrl+Alt+Suppr : la vraie combinaison est
                // confisquée par Windows en local. On passe par la commande KVM
                // dédiée, la seule qui atteigne l'écran de sécurité.
                case Key.Delete:
                    SendCtrlAltDel();
                    break;
            }
            return;
        }

        // Vue seule : rien ne part. La touche n'est pas marquée traitée, pour
        // que les raccourcis locaux (Alt+F4 par exemple) fonctionnent. Les
        // relâchements, eux, partent toujours : ils ne peuvent rien déclencher
        // et évitent qu'une touche reste enfoncée côté distant.
        if (_viewOnly) return;

        if (SendVirtualKey(e.Key, down: true)) e.Handled = true;
    }

    private void Content_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (SendVirtualKey(e.Key, down: false)) e.Handled = true;
    }

    /// <summary>
    /// Convertit une touche WPF en code VK Windows et l'envoie. MeshCentral
    /// attend précisément des codes VK Windows, donc la conversion est directe
    /// via KeyInterop — un avantage majeur du portage sur PC.
    /// </summary>
    private bool SendVirtualKey(Key key, bool down)
    {
        // Alt (et certaines combinaisons) arrive à WPF sous forme de Key.System ;
        // la vraie touche est alors dans SystemKey.
        if (key == Key.System)
            key = Keyboard.IsKeyDown(Key.RightAlt) ? Key.RightAlt : Key.LeftAlt;

        // La touche Windows n'est pas transmise. Windows l'intercepte de toute
        // façon en local : les deux menus Démarrer s'ouvraient ensemble, et
        // celui du poste local prenant la main, le relâchement n'arrivait jamais
        // à la machine distante, qui restait bloquée en Win. Le menu
        // « Raccourcis » l'envoie proprement, seule ou en combinaison.
        if (key is Key.LWin or Key.RWin)
        {
            if (down) StatsText.Text = "Touche Windows : passez par le menu « Raccourcis ».";
            return true;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0 || vk > 255) return false;

        // Touches « étendues » (flèches, Inser/Suppr, etc.) : le protocole a des
        // actions dédiées 3/4, mais VK + down/up simple fonctionne pour l'essentiel.
        _session.SendKey((byte)vk, down ? KeyAction.Down : KeyAction.Up);
        return true;
    }

    // MARK: - Barre d'outils

    private void QualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // _session est créé avant InitializeComponent (voir le constructeur),
        // donc il est déjà valide même si ce handler se déclenche pendant le
        // chargement du XAML.
        if (QualityCombo.SelectedItem is not ComboBoxItem item) return;
        _session.Quality = QualityFromSetting(QualityOf(item));
        if (!_uiReady) return;

        UpdateModeControls();
        UpdateStatusBar();
        if (_session.IsTroubleshootMode) SlowLinkBanner.Visibility = Visibility.Collapsed;
        StatsText.Text = _session.Quality switch
        {
            DesktopQuality.Troubleshoot => "Mode dépannage. « Image nette » (Ctrl+Maj+R) pour lire le texte.",
            DesktopQuality.Fluid => "Mode fluide : 40 img/s, bonne qualité.",
            _ => "",
        };
    }

    /// <summary>Vrai pendant que le code aligne la liste de fluidité sur le mode.</summary>
    private bool _syncingFrameRate;

    /// <summary>
    /// Aligne les contrôles sur le mode de qualité. Les modes dépannage et
    /// fluide fixent eux-mêmes la cadence : la liste l'affiche, grisée, et
    /// retrouve le choix de l'utilisateur quand on en sort. En dépannage, le
    /// bouton « Rafraîchir » devient « Image nette ».
    /// </summary>
    private void UpdateModeControls()
    {
        var forced = _session.IsTroubleshootMode || _session.IsFluidMode;
        var shown = forced
            ? DesktopSession.NearestFrameRate(_session.EffectiveFrameRate)
            : _session.FrameRate;

        _syncingFrameRate = true;
        try
        {
            FrameRateCombo.SelectedItem = FrameRateCombo.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => FrameRateOf(i) == shown) ?? FrameRateCombo.SelectedItem;
        }
        finally
        {
            _syncingFrameRate = false;
        }

        FrameRateCombo.IsEnabled = !forced;
        FrameRateCombo.ToolTip = _session.Quality switch
        {
            DesktopQuality.Troubleshoot => "Fluidité fixée par le mode dépannage",
            DesktopQuality.Fluid => "Fluidité fixée par le mode fluide",
            _ => "Fluidité : taux de rafraîchissement de l'écran distant",
        };

        var troubleshoot = _session.IsTroubleshootMode;
        _refreshLabel = troubleshoot ? "Image nette" : "Rafraîchir";
        if (RefreshButton.Content is not TextBlock { Tag: "glyph" })
            RefreshButton.Content = _refreshLabel;
        RefreshButton.ToolTip = troubleshoot
            ? "Recevoir une fois l'écran en bonne qualité, pour lire le texte (Ctrl+Maj+R)"
            : "Redemander une image complète (Ctrl+Maj+R)";
        ScheduleFitToolbar();
    }

    // MARK: - Liaison lente

    private void OnSlowLinkDetected()
    {
        // En vignette, le bandeau couvrirait l'essentiel de l'image.
        if (_session.IsTroubleshootMode || _docked) return;
        SlowLinkBanner.Visibility = Visibility.Visible;
    }

    private void SlowLinkSwitch_Click(object sender, RoutedEventArgs e)
    {
        SlowLinkBanner.Visibility = Visibility.Collapsed;
        QualityCombo.SelectedItem = QualityItemFor(QualityCombo, 3);
        if (!_docked) RootPanel.Focus();
    }

    private void SlowLinkDismiss_Click(object sender, RoutedEventArgs e)
    {
        SlowLinkBanner.Visibility = Visibility.Collapsed;
        if (!_docked) RootPanel.Focus();
    }

    /// <summary>
    /// Valeur enregistrée portée par un élément de la liste de qualité (son
    /// Tag) : 0 basse, 1 équilibrée, 2 haute, 3 dépannage, 4 fluide.
    /// </summary>
    internal static int QualityOf(ComboBoxItem item) =>
        item.Tag is string tag && int.TryParse(tag, out var value) ? value : 1;

    /// <summary>Élément d'une liste de qualité correspondant à une valeur enregistrée.</summary>
    internal static ComboBoxItem? QualityItemFor(ComboBox combo, int value)
    {
        var items = combo.Items.OfType<ComboBoxItem>().ToList();
        return items.FirstOrDefault(i => QualityOf(i) == value)
            ?? items.FirstOrDefault(i => QualityOf(i) == 1);
    }

    private static DesktopQuality QualityFromSetting(int value) => value switch
    {
        0 => DesktopQuality.Low,
        2 => DesktopQuality.High,
        3 => DesktopQuality.Troubleshoot,
        4 => DesktopQuality.Fluid,
        _ => DesktopQuality.Balanced,
    };

    private void FrameRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFrameRate) return;
        if (FrameRateCombo.SelectedItem is ComboBoxItem item && FrameRateOf(item) is { } fps)
            _session.FrameRate = fps;
    }

    /// <summary>Cadence portée par un élément de la liste (son Tag).</summary>
    private static int? FrameRateOf(ComboBoxItem item) =>
        item.Tag is string tag && int.TryParse(tag, out var fps) ? fps : null;

    // MARK: - Presse-papiers de la machine distante

    /// <summary>
    /// Récupère le presse-papiers de la machine distante et le place dans le
    /// presse-papiers local. Contrairement au collage (qui simule une frappe), il
    /// s'agit d'un vrai échange : l'agent renvoie le contenu au serveur.
    /// </summary>
    private async void CopyFromRemote_Click(object sender, RoutedEventArgs e)
    {
        CopyFromRemoteButton.IsEnabled = false;
        StatsText.Text = "Lecture du presse-papiers distant…";
        try
        {
            var text = await _connection.GetRemoteClipboardAsync(_node.Id);

            if (string.IsNullOrEmpty(text))
            {
                StatsText.Text = "Presse-papiers distant vide.";
                return;
            }

            // Référence de la synchronisation : ce texte est désormais des deux
            // côtés, il ne doit pas repartir vers la machine.
            _remoteClipKnown = text;
            Clipboard.SetText(text);
            StatsText.Text = $"Copié ({text.Length} car.)";
            Log.Info($"Presse-papiers récupéré de {_node.Name} ({text.Length} caractères)",
                     "Bureau");
        }
        catch (Exception ex)
        {
            Log.Warning($"Lecture du presse-papiers distant impossible : {ex.Message}", "Bureau");
            MessageBox.Show(ex.Message, "Mesh Control",
                MessageBoxButton.OK, MessageBoxImage.Information);
            StatsText.Text = "";
        }
        finally
        {
            CopyFromRemoteButton.IsEnabled = true;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _session.RequestSharpFrame();
        if (_session.IsTroubleshootMode)
            StatsText.Text = "Image nette en cours de réception…";
    }

    // MARK: - Raccourcis clavier distants

    /// <summary>
    /// Construit le menu des raccourcis en fonction du système de la machine
    /// distante : les combinaisons utiles diffèrent entre Windows et Linux.
    /// </summary>
    private void BuildShortcutsMenu()
    {
        ShortcutsMenu.Items.Clear();

        var isLinux = _node.OsFamily == OSFamily.Linux;
        var systemShortcuts = isLinux ? KeyboardShortcuts.Linux : KeyboardShortcuts.Windows;
        var systemHeader = isLinux ? "LINUX" : "WINDOWS";

        AddSection(systemHeader, systemShortcuts);
        ShortcutsMenu.Items.Add(new Separator());
        AddSection("ÉDITION", KeyboardShortcuts.Editing);

        void AddSection(string header, IReadOnlyList<RemoteShortcut> shortcuts)
        {
            ShortcutsMenu.Items.Add(new MenuItem
            {
                Header = header,
                IsEnabled = false,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
            });

            foreach (var shortcut in shortcuts)
            {
                var item = new MenuItem
                {
                    Header = shortcut.Label,
                    InputGestureText = shortcut.Description,
                    Tag = shortcut,
                };
                item.Click += Shortcut_Click;
                ShortcutsMenu.Items.Add(item);
            }
        }
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        // Le menu est reconstruit à chaque ouverture : l'OS détecté peut avoir
        // été précisé entre-temps par le serveur.
        BuildShortcutsMenu();
        // Ouvert depuis la barre d'outils ou depuis la barre du plein écran :
        // le menu se place sous le bouton cliqué.
        ShortcutsMenu.PlacementTarget = sender as UIElement ?? ShortcutsButton;
        ShortcutsMenu.IsOpen = true;
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: RemoteShortcut shortcut }) return;
        if (RejectedByViewOnly()) return;
        _session.SendChord(shortcut.Modifiers, shortcut.Key, shortcut.Extended);
        StatsText.Text = $"Envoyé : {shortcut.Label}";
    }

    // MARK: - Écrans multiples

    /// <summary>
    /// Écrans proposés dans le menu, vue combinée comprise. Sert aussi au cycle
    /// clavier Ctrl+Maj+← / →.
    /// </summary>
    private List<RemoteDisplay> _displayChoices = new();

    /// <summary>
    /// La machine a annoncé ses écrans. Le bouton n'apparaît qu'à partir de
    /// deux : sur un poste ordinaire, il resterait un contrôle inutile.
    /// </summary>
    private void OnDisplaysChanged()
    {
        // Les écrans réels, hors vue combinée : selon l'agent, celle-ci figure
        // déjà dans la liste annoncée. On l'écarte ici pour la rajouter une fois
        // et une seule, en dernière position.
        var real = _session.DisplayList
            .Where(d => d.Id != RemoteDisplay.AllDisplays)
            .ToList();

        if (real.Count <= 1)
        {
            DisplayButton.Visibility = Visibility.Collapsed;
            _displayChoices = new List<RemoteDisplay>();
            return;
        }

        _displayChoices = new List<RemoteDisplay>(real)
        {
            // Vue combinée : l'agent compose lui-même les écrans en une image
            // unique. C'est lui qui fait le travail, pas nous.
            new RemoteDisplay(RemoteDisplay.AllDisplays, 0, 0, 0, 0),
        };

        DisplayButton.Visibility = Visibility.Visible;
        UpdateDisplayButton();
    }

    /// <summary>
    /// Le bouton porte le numéro de l'écran courant : réduit à une icône, il
    /// doit malgré tout indiquer où l'on se trouve.
    /// </summary>
    private void UpdateDisplayButton()
    {
        var current = _displayChoices.FirstOrDefault(d => d.Id == _session.SelectedDisplay);
        DisplayButton.Content = current is null || current.Id == RemoteDisplay.AllDisplays
            ? "🖵 ⁘"                       // vue combinée
            : $"🖵 {current.Id}";
    }

    private void Display_Click(object sender, RoutedEventArgs e)
    {
        // Menu reconstruit à chaque ouverture : les écrans peuvent avoir été
        // branchés ou débranchés depuis la dernière fois.
        DisplayMenu.Items.Clear();

        foreach (var display in _displayChoices)
        {
            var item = new MenuItem
            {
                Header = display.Id == RemoteDisplay.AllDisplays
                    ? "Tous les écrans"
                    : display.Label,
                Tag = display,
                IsCheckable = true,
                IsChecked = display.Id == _session.SelectedDisplay,
            };
            item.Click += DisplayItem_Click;

            // La vue combinée ferme la liste : un trait la détache des écrans.
            if (display.Id == RemoteDisplay.AllDisplays)
                DisplayMenu.Items.Add(new Separator());
            DisplayMenu.Items.Add(item);
        }

        DisplayMenu.PlacementTarget = DisplayButton;
        DisplayMenu.IsOpen = true;
    }

    private void DisplayItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: RemoteDisplay display }) return;
        SelectDisplay(display);
    }

    private void SelectDisplay(RemoteDisplay display)
    {
        if (display.Id == _session.SelectedDisplay)
        {
            UpdateDisplayButton();   // rétablit la coche si on reclique le même
            return;
        }

        // Une zone agrandie sur un écran n'a pas de sens sur le suivant.
        ResetZoom(quiet: true);
        _session.SelectDisplay(display.Id);
        UpdateDisplayButton();
        StatsText.Text = display.Id == RemoteDisplay.AllDisplays
            ? "Tous les écrans."
            : $"Écran {display.Id}.";
    }

    /// <summary>
    /// Passe à l'écran suivant ou précédent, en bouclant. La vue « tous les
    /// écrans » fait partie du cycle : elle en est la dernière étape.
    /// </summary>
    private void CycleDisplay(int delta)
    {
        if (_displayChoices.Count == 0) return;

        int current = _displayChoices.FindIndex(d => d.Id == _session.SelectedDisplay);
        if (current < 0) current = 0;

        int index = (current + delta + _displayChoices.Count) % _displayChoices.Count;
        SelectDisplay(_displayChoices[index]);
    }

    // MARK: - Autres outils de la machine

    private void More_Click(object sender, RoutedEventArgs e)
    {
        UpdateScaleMenu();
        MoreMenu.PlacementTarget = MoreButton;
        MoreMenu.IsOpen = true;
    }

    /// <summary>Coche l'échelle active et rappelle sa valeur dans l'en-tête.</summary>
    private void UpdateScaleMenu()
    {
        foreach (var item in ScaleMenu.Items.OfType<MenuItem>())
        {
            var value = int.TryParse(item.Tag as string, out var v) ? v : 100;
            item.IsChecked = value == _session.ScalePercent;
        }

        ScaleMenu.Header = _session.EffectiveScalePercent != _session.ScalePercent
            ? $"Échelle de l'image  ({_session.EffectiveScalePercent} %, mode dépannage)"
            : _session.ScalePercent == 100
                ? "Échelle de l'image"
                : $"Échelle de l'image  ({_session.ScalePercent} %)";
    }

    /// <summary>
    /// Change l'échelle d'encodage. L'agent renvoie une nouvelle taille d'écran :
    /// le framebuffer est recréé et les coordonnées souris suivent
    /// automatiquement, puisqu'elles se calculent sur les dimensions reçues.
    /// </summary>
    private void Scale_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        if (!int.TryParse(item.Tag as string, out var percent)) return;

        _session.ScalePercent = percent;
        UpdateScaleMenu();

        StatsText.Text = percent == 100
            ? "Échelle : taille réelle."
            : $"Échelle : {percent} %.";
    }

    /// <summary>
    /// Ouvre le terminal sur la même machine, sans repasser par la liste.
    /// </summary>
    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
        => OpenCompanionSession(new Ssh.TerminalWindow(_connection, _node), "▶", "Terminal");

    private void OpenFiles_Click(object sender, RoutedEventArgs e)
        => OpenCompanionSession(new Files.FilesWindow(_connection, _node, _settings), "📁", "Fichiers");

    /// <summary>
    /// Processus de la machine : l'application qui fige l'écran distant se
    /// repère et s'arrête sans quitter le bureau à distance.
    /// </summary>
    private void OpenProcesses_Click(object sender, RoutedEventArgs e)
    {
        var window = new UI.ProcessWindow(_connection, _node);
        // Même règle que les informations système : pas de propriétaire invisible.
        if (DialogOwner is { IsVisible: true } owner) window.Owner = owner;
        window.Show();
        StatsText.Text = "Processus ouverts.";
    }

    private void OpenSystemInfo_Click(object sender, RoutedEventArgs e)
    {
        var window = new UI.SystemInfoWindow(_connection, _node);
        // Owner nul accepté : en mode onglets, cette fenêtre n'est pas affichée
        // et WPF refuserait une fenêtre invisible comme propriétaire.
        if (DialogOwner is { IsVisible: true } owner) window.Owner = owner;
        window.Show();
    }

    /// <summary>
    /// Ouvre la fiche MeshCentral de la machine dans le navigateur par défaut.
    /// </summary>
    private void OpenWebPage_Click(object sender, RoutedEventArgs e)
    {
        if (_connection.Profile.DeviceWebUrl(_node.Id) is not { } url)
        {
            StatsText.Text = "Adresse du serveur indisponible.";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            StatsText.Text = "Page web ouverte.";
        }
        catch (Exception ex)
        {
            // Pas de navigateur associé : on laisse au moins l'adresse sous la main.
            try { Clipboard.SetText(url); } catch { }
            MessageBox.Show($"Impossible d'ouvrir le navigateur : {ex.Message}\n\n"
                          + "L'adresse a été copiée dans le presse-papiers.",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Présente une session ouverte depuis le bureau à distance, en respectant la
    /// préférence « sessions en onglets » comme le ferait la fenêtre principale.
    /// </summary>
    private void OpenCompanionSession(Window session, string glyph, string kind)
    {
        if (_settings.UseTabbedSessions)
        {
            UI.SessionsWindow.AddSession(session, _node.Name, glyph);
        }
        else
        {
            session.Show();
            session.Activate();
        }
        StatsText.Text = $"{kind} ouvert.";
    }

    // MARK: - Plein écran

    /// <summary>
    /// Plein écran de cette session. Le mécanisme est partagé avec les
    /// terminaux : il porte sur la fenêtre hôte, qui n'est pas celle-ci en mode
    /// onglets.
    /// </summary>
    private UI.SessionFullscreen _fullscreen = null!;

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        // Vignette : passer en grand, c'est la sortir du terminal.
        if (_docked)
        {
            _session.ReleaseModifiers();
            UndockRequested?.Invoke();
            return;
        }

        _fullscreen.Toggle();
        // Le clavier doit continuer d'arriver à la machine distante après la
        // bascule : le changement de style de fenêtre fait perdre le focus.
        RootPanel.Focus();
    }

    /// <summary>
    /// Ouverture agrandie ou en plein écran, selon le réglage. Appliquée une
    /// seule fois, au premier affichage : c'est alors seulement que l'on sait
    /// quelle fenêtre héberge la session (la sienne, ou celle des onglets).
    /// </summary>
    private void ApplyOpenModeOnFirstDisplay()
    {
        var mode = _settings.DesktopOpenMode;
        if (mode is not (1 or 2)) return;

        // Fenêtre autonome : agrandie avant même d'apparaître, sans saut
        // visible. En mode onglets, cette fenêtre porteuse n'est jamais montrée.
        if (mode == 1) WindowState = WindowState.Maximized;

        RoutedEventHandler? onLoaded = null;
        onLoaded = (_, _) =>
        {
            RootPanel.Loaded -= onLoaded;
            Dispatcher.BeginInvoke(() =>
            {
                // Vignette : la fenêtre hôte est celle du terminal, qui n'a
                // pas demandé à être agrandie.
                if (_docked) return;
                if (Window.GetWindow(RootPanel) is not { } host) return;
                // Une autre session a déjà mis la fenêtre des onglets en plein écran.
                if (UI.FullscreenController.IsFullscreen(host)) return;

                if (mode == 1) host.WindowState = WindowState.Maximized;
                else ToggleFullscreen();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        RootPanel.Loaded += onLoaded;
    }

    private void OnFullscreenChanged(bool fullscreen)
    {
        FullscreenButton.ToolTip = fullscreen
            ? "Quitter le plein écran (Ctrl+Maj+F)"
            : "Plein écran (Ctrl+Maj+F)";
        if (!fullscreen) HideFullscreenBar();
    }

    // MARK: - Capture d'écran

    /// <summary>
    /// Enregistre l'écran distant en PNG et le place dans le presse-papiers.
    /// L'image provient du framebuffer déjà décodé : aucune requête au serveur.
    /// </summary>
    private void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        var bitmap = _framebuffer.Bitmap;
        if (bitmap is null)
        {
            StatsText.Text = "Aucune image à capturer.";
            return;
        }

        // Nom par défaut : machine + horodatage, pour retrouver la capture plus tard.
        var safeName = string.Join("_", _deviceName.Split(Path.GetInvalidFileNameChars()));
        var dialog = new SaveFileDialog
        {
            Title = "Enregistrer la capture",
            FileName = $"{safeName}_{DateTime.Now:yyyy-MM-dd_HHmmss}.png",
            Filter = "Image PNG (*.png)|*.png",
            DefaultExt = ".png",
        };
        if (dialog.ShowDialog(DialogOwner) != true) return;

        try
        {
            // Figer l'image : le framebuffer continue d'être écrit par la session.
            var frozen = bitmap.Clone();
            frozen.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frozen));
            using (var stream = File.Create(dialog.FileName))
            {
                encoder.Save(stream);
            }

            // Également dans le presse-papiers : souvent collée directement
            // dans un ticket ou un message.
            try { Clipboard.SetImage(frozen); } catch { /* presse-papiers occupé */ }

            StatsText.Text = "Capture enregistrée.";
            Log.Info($"Capture de {_deviceName} enregistrée : {dialog.FileName}", "Bureau");
        }
        catch (Exception ex)
        {
            Log.Error("Enregistrement de la capture impossible", ex, "Bureau");
            MessageBox.Show($"Échec de l'enregistrement : {ex.Message}", "Mesh Control",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Relâche les modificateurs côté distant. Appelé par la fenêtre à onglets
    /// quand elle intercepte un raccourci : les touches parties « enfoncées »
    /// avant l'interception ne recevraient jamais leur relâchement.
    /// </summary>
    public void ReleaseKeyboard() => _session.ReleaseModifiers();

    /// <summary>Secours si le clavier distant semble bloqué en Ctrl/Alt/Maj.</summary>
    private void ReleaseModifiers_Click(object sender, RoutedEventArgs e)
    {
        _session.ReleaseModifiers();
        StatsText.Text = "Modificateurs relâchés.";
    }

    // MARK: - Presse-papiers

    /// <summary>
    /// État du presse-papiers de la machine distante : null tant qu'on n'a pas
    /// essayé de l'écrire, vrai s'il accepte l'écriture, faux s'il faut se
    /// replier sur la saisie clavier. Déterminé une fois par session : la
    /// configuration du serveur ne change pas entre deux collages.
    /// </summary>
    private bool? _remoteClipboardWritable;

    /// <summary>
    /// Longueur au-delà de laquelle on n'essaie pas d'écrire le presse-papiers
    /// distant : le texte transite en JSON par le canal de contrôle du serveur,
    /// qui porte aussi tout le reste de la session.
    /// </summary>
    private const int MaxRemoteClipboardLength = 256 * 1024;

    /// <summary>
    /// Colle le presse-papiers local sur la machine distante.
    ///
    /// Voie normale : écrire le presse-papiers distant, puis envoyer Ctrl+V.
    /// C'est instantané, quelle que soit la longueur du texte. Si le serveur
    /// refuse l'écriture (clipboardset désactivé) ou si l'agent ne la confirme
    /// pas, on se replie sur la saisie caractère par caractère : elle fonctionne
    /// partout, mais coûte 6 ms par caractère.
    /// </summary>
    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        // Une saisie est en cours (Ctrl+Maj+V contourne le bouton grisé) : on
        // ne l'interrompt pas par surprise, le bouton « Arrêter » est là pour ça.
        if (_typing)
        {
            StatsText.Text = "Saisie en cours — « Arrêter » pour l'interrompre.";
            return;
        }
        if (_pasteBusy || RejectedByViewOnly()) return;

        if (ReadClipboardText() is not { } text) return;

        // Synchronisation active et texte déjà présent côté distant : Ctrl+V
        // suffit, sans nouvel aller-retour.
        if (_clipboardSync && _remoteClipboardWritable == true
            && _remoteClipKnown is not null && SameClipboardText(text, _remoteClipKnown))
        {
            _session.SendChord(new[] { Vk.Ctrl }, Vk.Letter('V'));
            StatsText.Text = $"Collé ({text.Length} car.)";
            return;
        }

        if (_remoteClipboardWritable != false && text.Length <= MaxRemoteClipboardLength)
        {
            _pasteBusy = true;
            UpdateInputControls();
            bool pasted;
            try
            {
                await WaitForClipboardSyncIdleAsync();
                pasted = await TryPasteViaRemoteClipboardAsync(text);
            }
            finally
            {
                _pasteBusy = false;
                UpdateInputControls();
            }

            if (pasted) return;
        }

        await PasteByTypingAsync(text);
    }

    /// <summary>
    /// Laisse finir un échange de synchronisation en cours : sa réponse
    /// pourrait sinon être prise pour celle de la vérification du collage.
    /// </summary>
    private async Task WaitForClipboardSyncIdleAsync()
    {
        for (int i = 0; i < 80 && _clipboardSyncBusy; i++)
            await Task.Delay(50);
    }

    /// <summary>
    /// Colle en simulant la frappe, sans passer par le presse-papiers distant.
    /// Utile quand l'application distante ignore Ctrl+V — certains terminaux
    /// attendent Ctrl+Maj+V. Pendant une saisie, le même bouton l'interrompt :
    /// un long texte immobilise sinon la machine distante sans recours.
    /// </summary>
    private async void PasteAsTyping_Click(object sender, RoutedEventArgs e)
    {
        if (_typing)
        {
            _pasteCts?.Cancel();
            return;
        }
        if (RejectedByViewOnly()) return;

        if (ReadClipboardText() is { } text) await PasteByTypingAsync(text);
    }

    /// <summary>
    /// Écrit le texte dans le presse-papiers distant, vérifie qu'il y est bien
    /// arrivé, puis envoie Ctrl+V. Renvoie faux s'il faut se replier sur la frappe.
    ///
    /// L'écriture n'est pas acquittée : le serveur peut l'avoir écartée sans rien
    /// dire. On relit donc le presse-papiers distant — une seule fois par
    /// session — plutôt que d'envoyer un Ctrl+V qui collerait l'ancien contenu :
    /// une erreur silencieuse est bien plus gênante qu'un collage lent.
    /// </summary>
    private async Task<bool> TryPasteViaRemoteClipboardAsync(string text)
    {
        try
        {
            StatsText.Text = "Envoi vers le presse-papiers distant…";
            await _connection.SetRemoteClipboardAsync(_node.Id, text);
            // Référence de la synchronisation : ce texte n'est pas à recopier en
            // local. La lecture suivante confirmera ce que la machine a gardé.
            _remoteClipKnown = text;
            _remoteClipRebaseline = true;

            if (_remoteClipboardWritable is null)
            {
                // Délai court : ce n'est qu'une vérification, et l'utilisateur
                // attend devant son écran. Le repli, lui, fonctionne toujours.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var remote = await _connection.GetRemoteClipboardAsync(_node.Id, timeout.Token);
                _remoteClipKnown = remote;
                _remoteClipRebaseline = false;
                _remoteClipboardWritable = SameBeginning(remote, text);

                if (_remoteClipboardWritable == false)
                {
                    Log.Info($"{_node.Name} : le presse-papiers distant n'a pas pris le "
                           + "texte, repli sur la saisie clavier.", "Bureau");
                    return false;
                }
            }

            _session.SendChord(new[] { Vk.Ctrl }, Vk.Letter('V'));
            StatsText.Text = $"Collé ({text.Length} car.)";
            return true;
        }
        catch (Exception ex)
        {
            // Écriture ou relecture impossible : sans confirmation, on ne prend
            // pas le risque d'un Ctrl+V qui collerait autre chose.
            _remoteClipboardWritable = false;
            Log.Info($"{_node.Name} : presse-papiers distant inutilisable "
                   + $"({ex.Message}), repli sur la saisie clavier.", "Bureau");
            return false;
        }
    }

    /// <summary>
    /// Le presse-papiers distant contient-il bien ce qu'on vient d'y écrire ?
    ///
    /// Comparaison du début seulement : l'agent peut tronquer un texte long, et
    /// les fins de ligne sont normalisées au passage.
    /// </summary>
    private static bool SameBeginning(string remote, string sent)
    {
        const int compared = 200;
        var a = remote.Replace("\r", "");
        var b = (sent.Length > compared * 2 ? sent[..(compared * 2)] : sent).Replace("\r", "");
        if (a.Length == 0 || b.Length == 0) return false;

        var length = Math.Min(Math.Min(a.Length, b.Length), compared);
        return string.CompareOrdinal(a, 0, b, 0, length) == 0;
    }

    /// <summary>Texte du presse-papiers local, ou null s'il n'y a rien d'exploitable.</summary>
    private string? ReadClipboardText()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                StatsText.Text = "Le presse-papiers ne contient pas de texte.";
                return null;
            }

            var text = Clipboard.GetText();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex)
        {
            StatsText.Text = $"Presse-papiers inaccessible : {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Saisit le texte touche par touche sur la machine distante. Sûr, mais lent :
    /// au-delà de quelques milliers de caractères, on demande confirmation plutôt
    /// que de bloquer la machine distante par surprise.
    /// </summary>
    private async Task PasteByTypingAsync(string text)
    {
        const int warnThreshold = 2000;
        if (text.Length > warnThreshold)
        {
            var seconds = text.Length * 6 / 1000;
            var answer = MessageBox.Show(
                $"Le presse-papiers contient {text.Length} caractères.\n"
                + $"La saisie prendra environ {seconds} secondes, pendant lesquelles "
                + "il ne faut pas utiliser la machine distante.\n\nContinuer ?",
                "Coller un texte long", MessageBoxButton.OKCancel, MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) return;
        }

        await SendClipboardTextAsync(text);
    }

    private CancellationTokenSource? _pasteCts;

    /// <summary>Vrai pendant une saisie : le bouton « Taper » devient « Arrêter ».</summary>
    private bool _typing;

    private async Task SendClipboardTextAsync(string text)
    {
        // Une frappe déjà en cours est annulée : deux saisies simultanées
        // entrelaceraient les caractères.
        _pasteCts?.Cancel();
        var cts = new CancellationTokenSource();
        _pasteCts = cts;
        var token = cts.Token;

        SetTypingState(true);
        try
        {
            StatsText.Text = $"Saisie de {text.Length} caractères…";
            await _session.SendTextAsync(text, token);
            if (!token.IsCancellationRequested) StatsText.Text = "Texte saisi.";
            else StatsText.Text = "Saisie interrompue.";
        }
        catch (OperationCanceledException)
        {
            StatsText.Text = "Saisie interrompue.";
        }
        finally
        {
            // Seule la dernière saisie lancée rétablit les boutons.
            if (ReferenceEquals(_pasteCts, cts))
            {
                _pasteCts = null;
                SetTypingState(false);
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// Bascule le bloc de collage entre l'état normal et l'état « saisie en
    /// cours » : Coller est grisé, Taper devient Arrêter.
    /// </summary>
    private void SetTypingState(bool typing)
    {
        _typing = typing;
        UpdateInputControls();
        BarTypingGlyph.Text = typing ? "\uE71A" : "\uE765";
        BarTypingLabel.Text = typing ? "Arrêter" : "Taper";
        BarTypingButton.ToolTip = typing
            ? "Interrompre la saisie en cours"
            : "Coller en simulant la frappe (Ctrl+Maj+Alt+V)";
        PasteTypingGlyph.Text = typing ? "\uE71A" : "\uE765"; // Stop / Clavier
        PasteTypingLabel.Text = typing ? "Arrêter" : "Taper";
        PasteTypingButton.ToolTip = typing
            ? "Interrompre la saisie en cours"
            : "Coller en simulant la frappe : lent, mais fonctionne partout, "
              + "même là où Ctrl+V est ignoré (Ctrl+Maj+Alt+V)";
    }

    // MARK: - Alimentation

    /// <summary>
    /// Vrai entre un redémarrage demandé d'ici et le retour de la machine : la
    /// session est alors relancée d'elle-même, même si la reprise automatique a
    /// abandonné entre-temps (mises à jour Windows, démarrage lent).
    /// </summary>
    private bool _rebootPending;

    /// <summary>La machine a été vue hors ligne depuis l'ordre de redémarrage.</summary>
    private bool _rebootSawOffline;

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        PowerMenu.PlacementTarget = PowerButton;
        PowerMenu.IsOpen = true;
    }

    private async void Reboot_Click(object sender, RoutedEventArgs e)
        => await RunPowerActionAsync(PowerActionType.Reboot);

    private async void Sleep_Click(object sender, RoutedEventArgs e)
        => await RunPowerActionAsync(PowerActionType.Sleep);

    private async void PowerOff_Click(object sender, RoutedEventArgs e)
        => await RunPowerActionAsync(PowerActionType.PowerOff);

    /// <summary>
    /// Envoie un ordre d'alimentation après confirmation. Une seule boîte, dont
    /// le bouton par défaut est OK : Entrée suffit à confirmer.
    /// </summary>
    private async Task RunPowerActionAsync(PowerActionType action)
    {
        var (title, detail, done) = action switch
        {
            PowerActionType.Reboot => (
                $"Redémarrer « {_node.Name} » ?",
                "La machine va redémarrer. Les travaux non enregistrés seront perdus.\n\n"
                + "La session de bureau reprendra d'elle-même au retour de la machine.",
                "Redémarrage demandé."),
            PowerActionType.Sleep => (
                $"Mettre « {_node.Name} » en veille ?",
                "La machine sera suspendue et ne répondra plus jusqu'à son réveil.",
                "Mise en veille demandée."),
            _ => (
                $"Éteindre « {_node.Name} » ?",
                "La machine sera arrêtée. Il faudra un réveil Wake-on-LAN ou une action "
                + "physique pour la rallumer.",
                "Extinction demandée."),
        };

        if (MessageBox.Show(DialogOwner, detail, title, MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning, MessageBoxResult.OK) != MessageBoxResult.OK)
        {
            RootPanel.Focus();
            return;
        }
        RootPanel.Focus();

        try
        {
            await _connection.PowerActionAsync(_node.Id, action);
            StatsText.Text = done;
            Log.Info($"{done} ({_node.Name}, depuis le bureau à distance)", "Bureau");

            if (action == PowerActionType.Reboot) WatchForReboot();
        }
        catch (Exception ex)
        {
            Log.Warning($"Ordre d'alimentation refusé pour {_node.Name} : {ex.Message}", "Bureau");
            MessageBox.Show(DialogOwner, $"L'ordre n'a pas pu être envoyé : {ex.Message}",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Suit l'état de la machine jusqu'à son retour. La reprise automatique de
    /// la session couvre environ deux minutes ; au-delà, c'est le retour de
    /// l'agent auprès du serveur qui relance la connexion.
    /// </summary>
    private void WatchForReboot()
    {
        if (_rebootPending) return;
        _rebootPending = true;
        _rebootSawOffline = false;
        _connection.DevicesChanged += OnDevicesChangedDuringReboot;
    }

    private void StopWatchingReboot()
    {
        if (!_rebootPending) return;
        _rebootPending = false;
        _connection.DevicesChanged -= OnDevicesChangedDuringReboot;
    }

    // Levé hors du thread UI : la connexion laisse le marshaling à l'appelant.
    private void OnDevicesChangedDuringReboot() => Dispatcher.BeginInvoke(CheckRebootReturn);

    private void CheckRebootReturn()
    {
        if (!_rebootPending) return;
        if (!_connection.Nodes.TryGetValue(_node.Id, out var node)) return;

        // Tant que la machine n'a pas disparu, elle n'a pas encore redémarré :
        // la voir « en ligne » juste après l'ordre ne dit rien.
        if (!node.IsOnline)
        {
            _rebootSawOffline = true;
            return;
        }
        if (!_rebootSawOffline) return;

        StopWatchingReboot();
        StatsText.Text = "Machine redémarrée.";
        if (_session.State == DesktopSessionState.Closed) _session.RetryNow();
    }

    // MARK: - Loupe

    /// <summary>
    /// Zone agrandie, en fraction de l'image (0 à 1) : elle survit ainsi au
    /// redimensionnement de la fenêtre comme à un changement de résolution.
    /// Null quand l'écran est affiché en entier.
    /// </summary>
    private Rect? _zoomRect;

    /// <summary>Vrai pendant le tracé du rectangle : la souris reste locale.</summary>
    private bool _zoomSelecting;

    private Point _zoomStart;

    /// <summary>Agrandissement maximal : au-delà, on ne voit plus que des pixels.</summary>
    private const double MaxZoom = 12;

    /// <summary>Côté minimal du rectangle tracé, en pixels de la fenêtre.</summary>
    private const double MinZoomBand = 12;

    private void Zoom_Click(object sender, RoutedEventArgs e)
    {
        // Maj+clic pendant un zoom : nouveau tracé dans la zone déjà agrandie.
        ToggleZoom(refine: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        RootPanel.Focus();
    }

    private void ZoomBadge_Click(object sender, RoutedEventArgs e)
    {
        ResetZoom(quiet: false);
        RootPanel.Focus();
    }

    /// <summary>
    /// Un même geste pour tout : annule un tracé en cours, sinon revient à la
    /// vue entière, sinon commence un tracé.
    /// </summary>
    /// <param name="refine">
    /// Pendant un zoom, tracer une nouvelle zone dans la vue agrandie plutôt
    /// que revenir à la vue entière.
    /// </param>
    private void ToggleZoom(bool refine = false)
    {
        if (_zoomSelecting)
        {
            CancelZoomSelection();
            StatsText.Text = "Zoom annulé.";
        }
        else if (_zoomRect is not null && !refine)
        {
            ResetZoom(quiet: false);
        }
        else
        {
            BeginZoomSelection();
        }
    }

    private void BeginZoomSelection()
    {
        if (_framebuffer.Bitmap is null)
        {
            StatsText.Text = "Aucune image à agrandir.";
            return;
        }

        // La loupe s'appuie sur la vue ajustée.
        if (FitCheck.IsChecked != true) FitCheck.IsChecked = true;

        _zoomSelecting = true;
        ZoomBand.Visibility = Visibility.Collapsed;
        ZoomOverlay.Visibility = Visibility.Visible;
        ZoomHint.Visibility = Visibility.Visible;
        UpdateZoomButton();
        StatsText.Text = "Tracez la zone à agrandir.";
    }

    private void CancelZoomSelection()
    {
        if (!_zoomSelecting) return;
        _zoomSelecting = false;
        if (ZoomOverlay.IsMouseCaptured) ZoomOverlay.ReleaseMouseCapture();
        ZoomOverlay.Visibility = Visibility.Collapsed;
        ZoomHint.Visibility = Visibility.Collapsed;
        ZoomBand.Visibility = Visibility.Collapsed;
        UpdateZoomButton();
    }

    private void ResetZoom(bool quiet)
    {
        if (_zoomRect is null) return;
        _zoomRect = null;
        ApplyZoom();
        if (!quiet) StatsText.Text = "Vue entière.";
    }

    private void ZoomOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _zoomStart = e.GetPosition(ZoomOverlay);
        ZoomOverlay.CaptureMouse();
        UpdateZoomBand(_zoomStart);
        ZoomBand.Visibility = Visibility.Visible;
    }

    private void ZoomOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (ZoomOverlay.IsMouseCaptured) UpdateZoomBand(e.GetPosition(ZoomOverlay));
    }

    private void ZoomOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!ZoomOverlay.IsMouseCaptured) return;
        e.Handled = true;
        ZoomOverlay.ReleaseMouseCapture();

        var end = e.GetPosition(ZoomOverlay);
        if (Math.Abs(end.X - _zoomStart.X) < MinZoomBand || Math.Abs(end.Y - _zoomStart.Y) < MinZoomBand)
        {
            // Simple clic ou rectangle minuscule : on reste en mode tracé.
            ZoomBand.Visibility = Visibility.Collapsed;
            StatsText.Text = "Zone trop petite : faites glisser pour tracer un rectangle.";
            return;
        }

        // Coins ramenés dans le repère de l'image, transformation actuelle
        // comprise : tracer pendant un zoom affine la zone déjà agrandie.
        var a = ZoomOverlay.TranslatePoint(_zoomStart, ScreenImage);
        var b = ZoomOverlay.TranslatePoint(end, ScreenImage);
        double w = ScreenImage.ActualWidth, h = ScreenImage.ActualHeight;
        CancelZoomSelection();
        if (w <= 0 || h <= 0) return;

        double x1 = Math.Clamp(Math.Min(a.X, b.X), 0, w), x2 = Math.Clamp(Math.Max(a.X, b.X), 0, w);
        double y1 = Math.Clamp(Math.Min(a.Y, b.Y), 0, h), y2 = Math.Clamp(Math.Max(a.Y, b.Y), 0, h);
        if (x2 - x1 < 1 || y2 - y1 < 1)
        {
            StatsText.Text = "La zone tracée est hors de l'écran distant.";
            return;
        }

        _zoomRect = new Rect(x1 / w, y1 / h, (x2 - x1) / w, (y2 - y1) / h);
        ApplyZoom();
    }

    private void ZoomOverlay_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CancelZoomSelection();
        StatsText.Text = "Zoom annulé.";
    }

    private void UpdateZoomBand(Point current)
    {
        Canvas.SetLeft(ZoomBand, Math.Min(_zoomStart.X, current.X));
        Canvas.SetTop(ZoomBand, Math.Min(_zoomStart.Y, current.Y));
        ZoomBand.Width = Math.Abs(current.X - _zoomStart.X);
        ZoomBand.Height = Math.Abs(current.Y - _zoomStart.Y);
    }

    /// <summary>
    /// Agrandit l'image pour que la zone retenue remplisse la vue, en gardant
    /// ses proportions. Tout passe par une transformation de rendu : les
    /// coordonnées de souris, relevées dans le repère de l'image, restent donc
    /// justes sans aucun calcul supplémentaire.
    /// </summary>
    private void ApplyZoom()
    {
        if (!_uiReady) return;

        double iw = ScreenImage.ActualWidth, ih = ScreenImage.ActualHeight;
        double hw = FitHost.ActualWidth, hh = FitHost.ActualHeight;

        if (_zoomRect is not { } r || iw <= 0 || ih <= 0 || hw <= 0 || hh <= 0)
        {
            ScreenImage.RenderTransform = System.Windows.Media.Transform.Identity;
            ZoomBadge.Visibility = Visibility.Collapsed;
            UpdateZoomButton();
            return;
        }

        // Position de l'image dans son hôte, hors transformation : le Grid la centre.
        double ox = (hw - iw) / 2, oy = (hh - ih) / 2;

        double scale = Math.Min(hw / (r.Width * iw), hh / (r.Height * ih));
        scale = Math.Clamp(scale, 1, MaxZoom);

        double cx = (r.X + r.Width / 2) * iw, cy = (r.Y + r.Height / 2) * ih;
        double tx = ClampOffset(hw / 2 - ox - scale * cx, ox, scale * iw, hw);
        double ty = ClampOffset(hh / 2 - oy - scale * cy, oy, scale * ih, hh);

        ScreenImage.RenderTransform = new System.Windows.Media.MatrixTransform(scale, 0, 0, scale, tx, ty);

        ZoomBadge.Content = $"Zoom ×{scale:0.#}";
        ZoomBadge.Visibility = Visibility.Visible;
        UpdateZoomButton();
    }

    /// <summary>
    /// Décalage le long d'un axe : l'image agrandie ne laisse pas de bande vide
    /// quand elle déborde de la vue, et reste centrée quand elle y tient.
    /// </summary>
    private static double ClampOffset(double offset, double origin, double size, double host)
    {
        if (size <= host) return (host - size) / 2 - origin;
        return Math.Clamp(offset, host - size - origin, -origin);
    }

    private void UpdateZoomButton()
    {
        // Loupe « moins » quand un clic ramène à la vue entière.
        var zoomed = _zoomRect is not null && !_zoomSelecting;
        ZoomButtonGlyph.Text = zoomed ? "" : "";
        ZoomButton.ToolTip = zoomed
            ? "Revenir à la vue entière (Ctrl+Maj+Z) — Maj+clic pour agrandir encore"
            : "Agrandir une zone : tracer un rectangle sur l'écran (Ctrl+Maj+Z)";
    }

    // MARK: - Fichiers déposés sur l'écran

    /// <summary>
    /// Fichiers en attente d'envoi, chacun avec son dossier d'arrivée. Un dépôt
    /// fait pendant un envoi s'ajoute à la file plutôt que d'ouvrir un second
    /// tunnel.
    /// </summary>
    private readonly Queue<(Files.OutgoingFile File, string Root)> _dropQueue = new();
    private CancellationTokenSource? _dropCts;
    private bool _dropRunning;
    private int _dropIndex;
    private int _dropTotal;
    private string? _dropCurrentName;

    private void DisplayArea_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        if (DropOverlay.Visibility != Visibility.Visible)
        {
            DropDestinationText.Text = _dropRunning
                ? "Ils rejoindront l'envoi en cours, dans le dossier de votre choix"
                : "Vous choisirez ensuite le dossier de destination";
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void DisplayArea_DragLeave(object sender, DragEventArgs e)
    {
        // Passer de l'image au fond de la zone lève aussi DragLeave : on ne
        // masque que si la souris a réellement quitté la zone d'affichage.
        var p = e.GetPosition(DisplayArea);
        if (new Rect(DisplayArea.RenderSize).Contains(p)) return;
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void DisplayArea_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        // Hors du thread UI : un dossier peut compter des milliers de fichiers.
        // L'attente rend aussi la main à l'Explorateur, que la boîte de
        // dialogue figerait sinon jusqu'à sa fermeture.
        var files = await Task.Run(() => paths
            .Select(Files.OutgoingEntry.From)
            .OfType<Files.OutgoingEntry>()
            .SelectMany(entry => entry.Files)
            .ToList());

        if (files.Count == 0)
        {
            StatsText.Text = "Rien à envoyer : fichiers introuvables ou dossiers vides.";
            return;
        }

        var dialog = new DropDestinationDialog(_connection, _node, DescribeFiles(files));
        if (DialogOwner is { IsVisible: true } owner) dialog.Owner = owner;
        var confirmed = dialog.ShowDialog() == true;
        RootPanel.Focus();

        if (!confirmed || dialog.Destination is not { } root)
        {
            StatsText.Text = "Envoi annulé.";
            return;
        }

        foreach (var file in files) _dropQueue.Enqueue((file, root));
        _dropTotal += files.Count;

        if (_dropRunning)
        {
            UpdateDropStatus(null);
            return;
        }

        _dropCts = new CancellationTokenSource();
        TransferPanel.Visibility = Visibility.Visible;
        try
        {
            await RunDropQueueAsync(_dropCts.Token);
        }
        finally
        {
            _dropQueue.Clear();
            _dropTotal = 0;
            _dropIndex = 0;
            _dropCurrentName = null;
            _dropCts.Dispose();
            _dropCts = null;
            TransferPanel.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>« « rapport.pdf » · 1,2 Mo », ou « 3 fichiers · 12,4 Mo ».</summary>
    private static string DescribeFiles(IReadOnlyList<Files.OutgoingFile> files)
    {
        var size = Files.FileRow.FormatBytes(files.Sum(f => f.Size));
        return files.Count == 1
            ? $"« {Path.GetFileName(files[0].LocalPath)} » · {size}"
            : $"{files.Count} fichiers · {size}";
    }

    private void CancelDrop_Click(object sender, RoutedEventArgs e)
    {
        _dropCts?.Cancel();
        RootPanel.Focus();
    }

    /// <summary>
    /// Envoie la file sur un tunnel de fichiers ouvert pour l'occasion, refermé
    /// une fois la file vide. Les dossiers glissés gardent leur arborescence.
    /// </summary>
    private async Task RunDropQueueAsync(CancellationToken ct)
    {
        _dropRunning = true;
        var sent = 0;
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var session = new Files.FilesSession(_connection, _node, new UI.WpfUiDispatcher(Dispatcher));
        void OnProgress() => UpdateDropStatus(session.Transfer);
        session.TransferChanged += OnProgress;

        StatusTransfer.Text = "Connexion pour l'envoi…";

        try
        {
            await session.StartAsync(listOnStart: false);
            if (session.State != Files.FilesSessionState.Ready)
                throw new MeshException(session.CloseReason ?? "Connexion à la machine impossible.");

            var ensured = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (_dropQueue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (file, root) = _dropQueue.Dequeue();

                if (ensured.Add(root)) await session.EnsureDirectoryAsync(root, ct);
                var directory = root;
                foreach (var segment in file.RelativeDirectory)
                    directory = session.CombinePath(directory, segment);
                if (ensured.Add(directory)) await session.EnsureDirectoryAsync(directory, ct);

                _dropIndex++;
                _dropCurrentName = Path.GetFileName(file.LocalPath);
                UpdateDropStatus(null);

                await session.UploadAsync(file.LocalPath, directory, ct);
                sent++;
                roots.Add(root);
            }

            var where = roots.Count == 1 ? $" dans {roots.First()}" : "";
            StatsText.Text = sent == 1 ? $"Fichier reçu{where}" : $"{sent} fichiers reçus{where}";
            Log.Info($"{sent} fichier(s) déposé(s) sur {_node.Name} ({string.Join(", ", roots)})", "Bureau");
        }
        catch (OperationCanceledException)
        {
            StatsText.Text = sent > 0 ? $"Envoi annulé ({sent} fichier(s) reçu(s))." : "Envoi annulé.";
        }
        catch (Exception ex)
        {
            Log.Warning($"Dépôt de fichiers sur {_node.Name} : {ex.Message}", "Bureau");
            StatsText.Text = "Envoi interrompu.";
            MessageBox.Show(DialogOwner,
                $"L'envoi vers « {_node.Name} » s'est interrompu"
                + (sent > 0 ? $" après {sent} fichier(s)" : "")
                + $" : {ex.Message}",
                "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            session.TransferChanged -= OnProgress;
            session.Stop();
            _dropRunning = false;
        }
    }

    private void UpdateDropStatus(Files.TransferProgress? progress)
    {
        if (!_dropRunning || _dropCurrentName is null) return;

        var parts = new List<string> { $"Envoi {_dropIndex}/{_dropTotal}", _dropCurrentName };
        if (progress is not null)
        {
            parts.Add($"{Math.Floor(progress.Fraction * 100)} %");
            if (Files.FileRow.FormatSpeed(progress.BytesPerSecond) is { Length: > 0 } speed)
                parts.Add(speed);
        }
        StatusTransfer.Text = string.Join(" · ", parts);
    }
}
