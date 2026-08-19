using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using MeshControl.Core;

namespace MeshControl.Desktop;

public partial class DesktopWindow : Window, MeshControl.UI.ISessionWindow
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

    /// <summary>Démarre la session ; sans effet si elle l'est déjà.</summary>
    public async Task StartSessionAsync()
    {
        if (_sessionStarted) return;
        _sessionStarted = true;
        await _session.StartAsync();
    }

    private readonly DesktopSession _session;
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
        _session = new DesktopSession(connection, node, Dispatcher);

        InitializeComponent();
        // L'arbre visuel existe : les handlers peuvent désormais y toucher.
        _uiReady = true;
        ApplyDefaultsFromSettings();
        ApplyFitMode();

        TitleText.Text = node.Name;
        _session.FrameUpdated += OnFrameUpdated;
        _session.ScreenSizeChanged += OnScreenSizeChanged;
        _session.StateChanged += OnStateChanged;
        _session.DisplaysChanged += OnDisplaysChanged;

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
        RootPanel.Loaded += (_, _) => RootPanel.Focus();

        // La barre d'état se rafraîchit à intervalle fixe : la recalculer à
        // chaque tuile reçue ferait travailler l'UI des dizaines de fois par
        // seconde pour un affichage que l'œil ne suit pas.
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer.Stop();
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
            ? _session.ScalePercent == 100
                ? $"{_session.ScreenWidth} × {_session.ScreenHeight}"
                : $"{_session.ScreenWidth} × {_session.ScreenHeight} · {_session.ScalePercent} %"
            : "Résolution —";

        var total = _session.BytesReceived;
        StatusTotal.Text = total >= 1024L * 1024
            ? $"{total / 1024.0 / 1024.0:0.0} Mo reçus"
            : $"{total / 1024} Ko reçus";
    }

    /// <summary>
    /// Positionne les contrôles selon les préférences enregistrées. Les
    /// gestionnaires de sélection appliquent ensuite les valeurs à la session.
    /// </summary>
    private void ApplyDefaultsFromSettings()
    {
        QualityCombo.SelectedIndex = Math.Clamp(_settings.DefaultDesktopQuality, 0, 2);

        FrameRateCombo.SelectedIndex = _settings.DefaultDesktopFrameRate switch
        {
            5 => 0,
            20 => 2,
            30 => 3,
            45 => 4,
            60 => 5,
            _ => 1,      // 10 img/s
        };

        FitCheck.IsChecked = _settings.DefaultDesktopFit;

        // Avant tout envoi : la valeur part avec les premiers réglages de
        // compression, sans provoquer un second aller-retour.
        _session.ScalePercent = _settings.DefaultDesktopScale;
    }

    // MARK: - Événements de session

    private void OnFrameUpdated()
    {
        // Le WriteableBitmap est déjà mis à jour ; on s'assure juste que les
        // Image pointent dessus (une seule fois) et on rafraîchit les stats.
        if (ScreenImage.Source is null && _session.Framebuffer.Bitmap is not null)
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

        var bmp = _session.Framebuffer.Bitmap;
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

    private void Fit_Changed(object sender, RoutedEventArgs e) => ApplyFitMode();

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
        if (TryMapToRemote(e.GetPosition((IInputElement)sender), out int x, out int y))
            _session.SendMouseMove(x, y);
    }

    private void Screen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Donner le focus au panneau racine (et non à la fenêtre) : c'est lui qui
        // porte les gestionnaires clavier, y compris en mode onglets.
        RootPanel.Focus();
        if (!TryMapToRemote(e.GetPosition((IInputElement)sender), out int x, out int y)) return;
        var button = MapButton(e.ChangedButton);
        if (button is null) return;
        _session.SendMouseButton(button.Value, down: true, x, y);
    }

    private void Screen_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!TryMapToRemote(e.GetPosition((IInputElement)sender), out int x, out int y)) return;
        var button = MapButton(e.ChangedButton);
        if (button is null) return;
        _session.SendMouseButton(button.Value, down: false, x, y);
    }

    private void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
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

        if (ctrlShift && e.Key is Key.V or Key.C or Key.S or Key.T or Key.E or Key.W)
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
                case Key.W: OpenWebPage_Click(this, new RoutedEventArgs()); break;
            }
            return;
        }

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
        _session.Quality = QualityCombo.SelectedIndex switch
        {
            0 => DesktopQuality.Low,
            2 => DesktopQuality.High,
            _ => DesktopQuality.Balanced,
        };
    }

    private void FrameRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _session.FrameRate = FrameRateCombo.SelectedIndex switch
        {
            0 => 5,
            2 => 20,
            3 => 30,
            4 => 45,
            5 => 60,
            _ => 10,
        };
    }

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

    private void Refresh_Click(object sender, RoutedEventArgs e) => _session.RequestRefresh();

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
        ShortcutsMenu.PlacementTarget = ShortcutsButton;
        ShortcutsMenu.IsOpen = true;
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: RemoteShortcut shortcut }) return;
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

        ScaleMenu.Header = _session.ScalePercent == 100
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
        => OpenCompanionSession(new Files.FilesWindow(_connection, _node), "📁", "Fichiers");

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

    // MARK: - Capture d'écran

    /// <summary>
    /// Enregistre l'écran distant en PNG et le place dans le presse-papiers.
    /// L'image provient du framebuffer déjà décodé : aucune requête au serveur.
    /// </summary>
    private void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        var bitmap = _session.Framebuffer.Bitmap;
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

    /// <summary>Secours si le clavier distant semble bloqué en Ctrl/Alt/Maj.</summary>
    private void ReleaseModifiers_Click(object sender, RoutedEventArgs e)
    {
        _session.ReleaseModifiers();
        StatsText.Text = "Modificateurs relâchés.";
    }

    // MARK: - Presse-papiers

    /// <summary>
    /// « Colle » le presse-papiers local en le saisissant caractère par caractère
    /// sur la machine distante. Le protocole KVM ne synchronise pas les
    /// presse-papiers : c'est la seule voie possible, et elle a l'avantage de
    /// fonctionner sur Windows comme sur Linux.
    /// </summary>
    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            if (!Clipboard.ContainsText())
            {
                StatsText.Text = "Le presse-papiers ne contient pas de texte.";
                return;
            }
            text = Clipboard.GetText();
        }
        catch (Exception ex)
        {
            StatsText.Text = $"Presse-papiers inaccessible : {ex.Message}";
            return;
        }

        if (string.IsNullOrEmpty(text)) return;

        // Un texte très long prend un temps notable (~6 ms par caractère) :
        // on demande confirmation plutôt que de bloquer l'utilisateur par surprise.
        const int warnThreshold = 2000;
        if (text.Length > warnThreshold)
        {
            var seconds = text.Length * 6 / 1000;
            var answer = MessageBox.Show(
                $"Le presse-papiers contient {text.Length} caractères.\n"
                + $"La saisie prendra environ {seconds} secondes, pendant lesquelles "
                + "il ne faut pas utiliser la machine distante.\n\nContinuer ?",
                "Coller un texte long", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }

        await SendClipboardTextAsync(text);
    }

    private CancellationTokenSource? _pasteCts;

    private async Task SendClipboardTextAsync(string text)
    {
        // Une frappe déjà en cours est annulée : deux saisies simultanées
        // entrelaceraient les caractères.
        _pasteCts?.Cancel();
        _pasteCts = new CancellationTokenSource();
        var token = _pasteCts.Token;

        PasteButton.IsEnabled = false;
        try
        {
            StatsText.Text = $"Saisie de {text.Length} caractères…";
            await _session.SendTextAsync(text, token);
            if (!token.IsCancellationRequested) StatsText.Text = "Texte saisi.";
        }
        catch (OperationCanceledException)
        {
            StatsText.Text = "Saisie interrompue.";
        }
        finally
        {
            PasteButton.IsEnabled = true;
        }
    }
}
