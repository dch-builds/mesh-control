using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeshControl.Core;

namespace MeshControl.Desktop;

public partial class DesktopWindow : Window
{
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

        Loaded += async (_, _) => await _session.StartAsync();
        Closed += (_, _) => _session.Stop();

        // À la perte de focus, les KeyUp des touches encore enfoncées ne nous
        // parviennent plus : sans ce nettoyage, un modificateur resterait bloqué
        // côté distant et le clavier semblerait « cassé » au retour.
        Deactivated += (_, _) => _session.ReleaseModifiers();
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
    }

    // MARK: - Événements de session

    private void OnFrameUpdated()
    {
        // Le WriteableBitmap est déjà mis à jour ; on s'assure juste que les
        // Image pointent dessus (une seule fois) et on rafraîchit les stats.
        if (ScreenImage.Source is null && _session.Framebuffer.Bitmap is not null)
            ApplyFitMode();
        StatsText.Text = $"{_session.TilesReceived} tuiles";
    }

    private void OnScreenSizeChanged()
    {
        ApplyFitMode();
        ResolutionText.Text = $"{_session.ScreenWidth} × {_session.ScreenHeight}";
    }

    private void OnStateChanged()
    {
        if (_session.State == DesktopSessionState.Closed)
        {
            StatsText.Text = "Session fermée";
            StatusDot.Fill = (System.Windows.Media.Brush)FindResource("OfflineBrush");
            if (_session.CloseReason is { } reason)
                ResolutionText.Text = reason;
        }
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
        Focus(); // récupérer le clavier
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // Ctrl+Maj+V est intercepté LOCALEMENT (collage du presse-papiers) et ne
        // doit surtout pas partir vers la machine distante. Ctrl+Maj+V plutôt que
        // Ctrl+V, pour laisser Ctrl+V disponible côté distant.
        var ctrlShift = Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                        && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (ctrlShift && e.Key is Key.V or Key.C)
        {
            e.Handled = true;
            // Ctrl et Maj ont déjà été transmis « enfoncés » par leurs propres
            // événements ; comme on intercepte ici, leur relâchement n'arriverait
            // jamais côté distant et la machine resterait bloquée en Ctrl+Maj.
            _session.ReleaseModifiers();

            if (e.Key == Key.V) Paste_Click(this, new RoutedEventArgs());
            else CopyFromRemote_Click(this, new RoutedEventArgs());
            return;
        }

        if (SendVirtualKey(e.Key, down: true)) e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
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
