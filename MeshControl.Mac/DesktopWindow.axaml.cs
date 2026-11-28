using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MeshControl.Core;
using MeshControl.Desktop;
using RemoteButton = MeshControl.Desktop.MouseButton;

namespace MeshControl.Mac;

/// <summary>
/// Bureau à distance — portage de Desktop/DesktopWindow.xaml.cs (version
/// Windows) : même barre d'outils, même barre d'état, mêmes raccourcis
/// (Ctrl+Maj+…), interceptés localement.
///
/// Clavier — deux chemins, comme l'exige un Mac face à un poste Windows :
/// les caractères partent en Unicode depuis la saisie de texte (é, @, [ ou €
/// arrivent tels que tapés, quelle que soit la disposition du clavier distant) ;
/// les touches de commande et les combinaisons avec Ctrl ou Cmd partent en
/// codes VK Windows, Cmd étant traduit en Ctrl. Les modificateurs ne sont
/// transmis qu'au moment où une touche de commande en a besoin.
/// </summary>
public partial class DesktopWindow : ChromeWindow, ISessionWindow
{
    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;
    private readonly DesktopSession _session;
    private readonly AvaloniaFramebuffer _framebuffer = new();
    private readonly DispatcherTimer _statsTimer;
    private readonly bool _ready;

    private static readonly int[] QualityTags = [3, 0, 1, 2, 4];
    private static readonly int[] FrameRates = [5, 10, 15, 20, 25, 30, 40];

    public string NodeId { get; }

    /// <summary>Ouvre le terminal de la machine (fourni par la fenêtre principale).</summary>
    public Action? OpenTerminal { get; init; }

    private void OpenTerminal_Click(object? sender, RoutedEventArgs e) => OpenTerminal?.Invoke();

    /// <summary>Ouvre les fichiers de la machine (fourni par la fenêtre principale).</summary>
    public Action? OpenFiles { get; init; }

    private void OpenFiles_Click(object? sender, RoutedEventArgs e) => OpenFiles?.Invoke();

    /// <summary>Ouvre les processus de la machine (fourni par la fenêtre principale).</summary>
    public Action? OpenProcesses { get; init; }

    private void OpenProcesses_Click(object? sender, RoutedEventArgs e) => OpenProcesses?.Invoke();

    /// <summary>Ouvre la fiche d'informations système (fourni par la fenêtre principale).</summary>
    public Action? OpenSystemInfo { get; init; }

    private void OpenSystemInfo_Click(object? sender, RoutedEventArgs e) => OpenSystemInfo?.Invoke();

    public DesktopWindow(MeshServerConnection connection, MeshNode node)
    {
        _connection = connection;
        _node = node;
        NodeId = node.Id;
        _session = new DesktopSession(connection, node, AvaloniaUiDispatcher.Instance, _framebuffer);

        InitializeComponent();
        Title = $"{node.Name} — Mesh Control";
        TitleText.Text = node.Name;
        ToolTip.SetTip(TitleText, node.Name);
        TitleBar.MarkInteractive(ToolbarControls);

        // Réglages par défaut (volet des réglages de la fenêtre principale).
        var quality = App.Settings.DesktopQuality;
        QualityCombo.SelectedIndex = Math.Max(0, Array.IndexOf(QualityTags, quality));
        _session.Quality = (DesktopQuality)quality;
        var fps = DesktopSession.NearestFrameRate(App.Settings.DesktopFrameRate);
        FrameRateCombo.SelectedIndex = Math.Max(0, Array.IndexOf(FrameRates, fps));
        _session.FrameRate = fps;
        _session.ScalePercent = App.Settings.DefaultDesktopScale;
        FitCheck.IsChecked = App.Settings.DefaultDesktopFit;
        FitHost.IsVisible = FitCheck.IsChecked == true;
        Scroller.IsVisible = FitCheck.IsChecked != true;
        _ready = true;

        _session.StateChanged += UpdateState;
        _session.ScreenSizeChanged += OnScreenSizeChanged;
        _session.FrameUpdated += OnFrameUpdated;
        _session.DisplaysChanged += UpdateDisplays;
        _session.SlowLinkDetected += OnSlowLinkDetected;

        foreach (var image in new[] { ScreenImage, ScreenImageNative })
        {
            image.PointerMoved += Screen_PointerMoved;
            image.PointerPressed += Screen_PointerPressed;
            image.PointerReleased += Screen_PointerReleased;
            image.PointerCaptureLost += Screen_PointerCaptureLost;
            image.PointerWheelChanged += Screen_PointerWheelChanged;
        }

        // Sur le contenu et non sur la fenêtre : en onglet, la fenêtre porteuse
        // n'est jamais affichée, c'est le contenu qui reçoit les frappes.
        RootPanel.AddHandler(KeyDownEvent, Screen_KeyDown, RoutingStrategies.Tunnel);
        RootPanel.AddHandler(KeyUpEvent, Screen_KeyUp, RoutingStrategies.Tunnel);
        ScreenHost.TextInput += Screen_TextInput;
        ScreenHost.PointerPressed += (_, _) => ScreenHost.Focus();
        Deactivated += (_, _) => ReleaseAllKeys();

        BuildShortcutsMenu();
        InitializeFullscreenBar();
        UpdateScaleMenu();
        InitializeToolbarLayout();
        InitializeClipboardSync();
        InitializeZoom();
        InitializeDrop();
        InitializeStreamPause();
        UpdateModeControls();
        UpdateInputControls();

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => UpdateStats();

        UpdateState();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        await StartSessionAsync();
    }

    private bool _started;

    /// <summary>Démarre la session : à l'ouverture de la fenêtre, ou par la fenêtre à onglets.</summary>
    public async Task StartSessionAsync()
    {
        if (_started) return;
        _started = true;
        ApplyOpenMode();
        ScreenHost.Focus();
        _statsTimer.Start();
        await _session.StartAsync();
        UpdateStreamPause();
    }

    public void ReleaseKeyboard() => ReleaseAllKeys();

    /// <summary>
    /// Ouverture agrandie ou en plein écran, selon le réglage. Appliquée une
    /// seule fois, au premier affichage : c'est alors seulement que l'on sait
    /// quelle fenêtre héberge la session (la sienne, ou celle des onglets).
    /// </summary>
    private void ApplyOpenMode()
    {
        // Vignette : la fenêtre hôte est celle du terminal, qui n'a rien demandé.
        if (_docked) return;
        var host = Host;
        switch (App.Settings.DesktopOpenMode)
        {
            case 1 when host.WindowState == WindowState.Normal:
                host.WindowState = WindowState.Maximized;
                break;
            // Une autre session a peut-être déjà mis la fenêtre des onglets en plein écran.
            case 2 when host.WindowState != WindowState.FullScreen:
                host.WindowState = WindowState.FullScreen;
                break;
        }
    }
    public TitleBar SessionTitleBar => WindowTitleBar;
    public Control FocusTarget => ScreenHost;

    protected override void OnClosed(EventArgs e)
    {
        _statsTimer.Stop();
        _typingCts?.Cancel();
        _clipboardPollTimer.Stop();
        _session.AutoReconnect = false;
        ReleaseAllKeys();
        _session.Stop();
        _session.Dispose();
        base.OnClosed(e);
    }

    // MARK: - État et affichage

    private Image ActiveImage => FitCheck.IsChecked == true ? ScreenImage : ScreenImageNative;

    private void UpdateState()
    {
        var state = _session.State;
        StatusDot.Fill = Palette.Resource(state switch
        {
            DesktopSessionState.Connected => "OnlineBrush",
            DesktopSessionState.Closed => "DangerBrush",
            _ => "WarningTextBrush",
        });
        StatsText.Text = state switch
        {
            DesktopSessionState.Connecting => "Connexion…",
            DesktopSessionState.Reconnecting => $"Reconnexion… ({_session.ReconnectAttempt}/{_session.ReconnectAttemptsMax})",
            DesktopSessionState.Closed => "Session fermée",
            _ => "",
        };
        ReconnectButton.IsVisible = state == DesktopSessionState.Closed;

        OverlayText.Text = state switch
        {
            DesktopSessionState.Closed => $"Session fermée.\n{_session.CloseReason}".Trim(),
            DesktopSessionState.Reconnecting => "Connexion interrompue, nouvelle tentative…",
            _ when ScreenImage.Source is null => "Connexion au bureau distant…",
            _ => "",
        };
        OverlayText.IsVisible = OverlayText.Text.Length > 0;
    }

    private void OnScreenSizeChanged()
    {
        ScreenImage.Source = _framebuffer.Bitmap;
        ScreenImageNative.Source = _framebuffer.Bitmap;
        UpdateState();
        UpdateStats();
    }

    private void OnFrameUpdated()
    {
        if (ScreenImage.Source is null && _framebuffer.Bitmap is not null)
        {
            ScreenImage.Source = _framebuffer.Bitmap;
            ScreenImageNative.Source = _framebuffer.Bitmap;
            UpdateState();
        }
        // Le bitmap a été modifié en place : l'image doit être redessinée.
        ActiveImage.InvalidateVisual();
    }

    private void UpdateStats()
    {
        bool connected = _session.State == DesktopSessionState.Connected;
        StatusLatency.Text = connected && _session.LatencyMs is int latency ? $"Latence {latency} ms" : "Latence —";
        StatusThroughput.Text = connected ? $"Débit {_session.ThroughputKbps:0} Ko/s" : "Débit —";
        StatusResolution.Text = _session.ScreenWidth > 0 ? $"{_session.ScreenWidth} × {_session.ScreenHeight}" : "";
        var mb = _session.BytesReceived / (1024.0 * 1024.0);
        StatusTotal.Text = $"{mb:0.0} Mo reçus";
        ResolutionText.Text = _session.EffectiveScalePercent != 100 ? $"{_session.EffectiveScalePercent} %" : "";
    }

    private void ShowStatus(string message)
    {
        StatsText.Text = message;
        DispatcherTimer.RunOnce(() => { if (StatsText.Text == message) UpdateState(); }, TimeSpan.FromSeconds(4));
    }

    // MARK: - Barre d'outils

    private void QualityCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || QualityCombo.SelectedIndex < 0) return;
        _session.Quality = (DesktopQuality)QualityTags[QualityCombo.SelectedIndex];
        UpdateModeControls();
        UpdateStats();
        if (_session.IsTroubleshootMode) SlowLinkBanner.IsVisible = false;
        var message = _session.Quality switch
        {
            DesktopQuality.Troubleshoot => "Mode dépannage. « Image nette » (Ctrl+Maj+R) pour lire le texte.",
            DesktopQuality.Fluid => "Mode fluide : 40 img/s, bonne qualité.",
            _ => "",
        };
        if (message.Length > 0) ShowStatus(message);
        ScreenHost.Focus();
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
        var shown = forced ? DesktopSession.NearestFrameRate(_session.EffectiveFrameRate) : _session.FrameRate;

        _syncingFrameRate = true;
        try
        {
            var index = Array.IndexOf(FrameRates, shown);
            if (index >= 0) FrameRateCombo.SelectedIndex = index;
        }
        finally
        {
            _syncingFrameRate = false;
        }

        FrameRateCombo.IsEnabled = !forced;
        ToolTip.SetTip(FrameRateCombo, _session.Quality switch
        {
            DesktopQuality.Troubleshoot => "Fluidité fixée par le mode dépannage",
            DesktopQuality.Fluid => "Fluidité fixée par le mode fluide",
            _ => "Fluidité : taux de rafraîchissement de l'écran distant",
        });

        var troubleshoot = _session.IsTroubleshootMode;
        _refreshLabel = troubleshoot ? "Image nette" : "Rafraîchir";
        if (RefreshButton.Content is not MeshControl.Mac.Icon) RefreshButton.Content = _refreshLabel;
        ToolTip.SetTip(RefreshButton, troubleshoot
            ? "Recevoir une fois l'écran en bonne qualité, pour lire le texte (Ctrl+Maj+R)"
            : "Redemander une image complète (Ctrl+Maj+R)");
        ScheduleFitToolbar();
    }

    private void FrameRateCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingFrameRate || FrameRateCombo.SelectedIndex < 0) return;
        _session.FrameRate = FrameRates[FrameRateCombo.SelectedIndex];
        ScreenHost.Focus();
    }

    // MARK: - Liaison lente

    private void OnSlowLinkDetected()
    {
        if (_session.IsTroubleshootMode) return;
        SlowLinkBanner.IsVisible = true;
    }

    private void SlowLinkSwitch_Click(object? sender, RoutedEventArgs e)
    {
        SlowLinkBanner.IsVisible = false;
        QualityCombo.SelectedIndex = Array.IndexOf(QualityTags, (int)DesktopQuality.Troubleshoot);
        ScreenHost.Focus();
    }

    private void SlowLinkDismiss_Click(object? sender, RoutedEventArgs e)
    {
        SlowLinkBanner.IsVisible = false;
        ScreenHost.Focus();
    }

    private void Fit_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool fit = FitCheck.IsChecked == true;
        if (!fit)
        {
            CancelZoomSelection();
            ResetZoom(quiet: true);
        }
        FitHost.IsVisible = fit;
        Scroller.IsVisible = !fit;
        ActiveImage.InvalidateVisual();
        ScreenHost.Focus();
    }

    private void CtrlAltDel_Click(object? sender, RoutedEventArgs e)
    {
        SendCtrlAltDel();
        ScreenHost.Focus();
    }

    private void Reconnect_Click(object? sender, RoutedEventArgs e)
    {
        _session.RetryNow();
        ScreenHost.Focus();
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        _session.RequestRefresh();
        ScreenHost.Focus();
    }

    private void ReleaseModifiers_Click(object? sender, RoutedEventArgs e)
    {
        ReleaseAllKeys();
        ShowStatus("Clavier débloqué");
        ScreenHost.Focus();
    }

    private void BuildShortcutsMenu()
    {
        // Même menu dans la barre d'outils et dans la barre flottante du plein écran.
        foreach (var button in new[] { ShortcutsButton, BarShortcutsButton })
        {
            var menu = (MenuFlyout)button.Flyout!;
            var system = _node.OsFamily == OSFamily.Linux ? KeyboardShortcuts.Linux : KeyboardShortcuts.Windows;
            foreach (var shortcut in system) menu.Items.Add(ShortcutItem(shortcut));
            menu.Items.Add(new Separator());
            foreach (var shortcut in KeyboardShortcuts.Editing) menu.Items.Add(ShortcutItem(shortcut));
        }
    }

    private MenuItem ShortcutItem(RemoteShortcut shortcut)
    {
        var item = new MenuItem { Header = $"{shortcut.Label}  —  {shortcut.Description}" };
        item.Click += (_, _) =>
        {
            if (RejectedByViewOnly()) return;
            if (shortcut.Modifiers is [Vk.Ctrl, Vk.Alt] && shortcut.Key == Vk.Delete && _node.OsFamily != OSFamily.Linux)
                _session.SendCtrlAltDel();
            else
                _session.SendChord(shortcut.Modifiers, shortcut.Key, shortcut.Extended);
            ScreenHost.Focus();
        };
        return item;
    }

    private void Scale_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out var percent)) return;
        _session.ScalePercent = percent;
        UpdateScaleMenu();
        UpdateStats();
        ScreenHost.Focus();
    }

    private void UpdateScaleMenu()
    {
        foreach (var item in ScaleMenu.Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is string tag && tag == _session.ScalePercent.ToString();
    }

    // MARK: - Repli de la barre d'outils

    /// <summary>
    /// Degrés de repli, du plus complet au plus compact — comme sous Windows
    /// (DesktopWindow.Toolbar.cs) : libellés remplacés par des pictogrammes,
    /// puis statistiques masquées, puis boutons secondaires rangés dans « ⋯ ».
    /// </summary>
    private enum ToolbarCompactness { Full, Icons, NoStats, Overflow }

    /// <summary>Place gardée au nom de la machine avant de replier la barre.</summary>
    private const double ToolbarTitleReserve = 140;

    private bool _toolbarFitting;
    private object? _refreshLabel, _pasteLabel, _copyLabel;

    private void InitializeToolbarLayout()
    {
        _refreshLabel = RefreshButton.Content;
        _pasteLabel = PasteButton.Content;
        _copyLabel = CopyFromRemoteButton.Content;
        Toolbar.SizeChanged += (_, _) => ScheduleFitToolbar();
        ToolbarControls.SizeChanged += (_, _) => ScheduleFitToolbar();
    }

    private void ScheduleFitToolbar() => Dispatcher.UIThread.Post(FitToolbar, DispatcherPriority.Loaded);

    private void FitToolbar()
    {
        if (_toolbarFitting || Toolbar.Bounds.Width <= 0) return;
        _toolbarFitting = true;
        try
        {
            var available = Toolbar.Bounds.Width - ToolbarTitleReserve;
            var chosen = ToolbarCompactness.Overflow;
            foreach (var level in Enum.GetValues<ToolbarCompactness>())
            {
                ApplyToolbarCompactness(level);
                ToolbarControls.Measure(Avalonia.Size.Infinity);
                if (ToolbarControls.DesiredSize.Width <= available)
                {
                    chosen = level;
                    break;
                }
            }
            ApplyToolbarCompactness(chosen);
        }
        finally
        {
            _toolbarFitting = false;
        }
    }

    private void ApplyToolbarCompactness(ToolbarCompactness level)
    {
        var icons = level >= ToolbarCompactness.Icons;
        SetIconOnly(RefreshButton, icons, _refreshLabel, IconKind.Refresh);
        SetIconOnly(PasteButton, icons, _pasteLabel, IconKind.Paste);
        SetIconOnly(CopyFromRemoteButton, icons, _copyLabel, IconKind.Copy);
        PasteTypingLabel.IsVisible = !icons;
        PasteTypingGlyph.Margin = icons ? new Avalonia.Thickness(0, 1, 0, 0) : new Avalonia.Thickness(0, 1, 6, 0);

        StatsText.IsVisible = level < ToolbarCompactness.NoStats;

        var overflow = level >= ToolbarCompactness.Overflow;
        CopyFromRemoteButton.IsVisible = !overflow;
        ScreenshotButton.IsVisible = !overflow;
        ZoomButton.IsVisible = !overflow;
        OverflowZoomItem.IsVisible = overflow;
        ReleaseModifiersButton.IsVisible = !overflow;
        OverflowCopyItem.IsVisible = overflow;
        OverflowScreenshotItem.IsVisible = overflow;
        OverflowReleaseItem.IsVisible = overflow;
        OverflowSeparator.IsVisible = overflow;
    }

    private static void SetIconOnly(Button button, bool iconOnly, object? label, IconKind icon)
    {
        if (iconOnly)
        {
            if (button.Content is Icon) return;
            button.Content = new Icon { Kind = icon, Size = 13 };
            button.Padding = new Avalonia.Thickness(9, 5);
        }
        else if (button.Content is Icon)
        {
            button.Content = label;
            button.ClearValue(PaddingProperty);
        }
    }

    // MARK: - Capture, plein écran, alimentation, page web

    private void Screenshot_Click(object? sender, RoutedEventArgs e)
    {
        ScreenHost.Focus();
        if (_framebuffer.Bitmap is not { } bitmap) return;
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var safe = string.Concat(_node.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(folder, $"{safe} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        try
        {
            bitmap.Save(path);
            ShowStatus($"Capture enregistrée dans {Path.GetFileName(folder)}");
        }
        catch (Exception ex)
        {
            ShowStatus($"Capture impossible : {ex.Message}");
        }
    }

    private void Fullscreen_Click(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        // En vignette, le plein écran n'a pas de sens : la vignette passe en grand.
        if (_docked)
        {
            UndockRequested?.Invoke();
            return;
        }
        Host.WindowState = Host.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        ScreenHost.Focus();
    }

    /// <summary>
    /// Barres masquées en plein écran, rétablies à la sortie. Réglé sur l'état
    /// réel de la fenêtre, et non sur la bascule : on sort aussi du plein écran
    /// autrement (bouton vert sous macOS, raccourci du système), et les barres
    /// restaient alors masquées.
    /// </summary>
    protected override void OnHostWindowStateChanged()
    {
        UpdateStreamPause();
        // En vignette, les barres restent masquées, quoi que fasse le terminal.
        if (WindowTitleBar is null || _docked) return;

        bool full = Host.WindowState == WindowState.FullScreen;
        WindowTitleBar.IsVisible = !full;
        StatusBar.IsVisible = !full;
        ToolTip.SetTip(FullscreenButton, full ? "Quitter le plein écran (Ctrl+Maj+F)" : "Plein écran (Ctrl+Maj+F)");
        if (full) ShowFullscreenHint();
        else
        {
            HideFullscreenHint();
            HideFullscreenBar();
        }
    }

    // MARK: - Barre flottante du plein écran

    /// <summary>Masque la barre quand la souris l'a quittée depuis ce délai.</summary>
    private readonly DispatcherTimer _fullscreenBarHideTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    /// <summary>Durée du rappel affiché à l'entrée en plein écran.</summary>
    private readonly DispatcherTimer _fullscreenHintTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    /// <summary>Hauteur, en pixels depuis le haut, de la bande qui fait apparaître la barre.</summary>
    private const double FullscreenBarTriggerHeight = 3;

    private void InitializeFullscreenBar()
    {
        _fullscreenBarHideTimer.Tick += (_, _) =>
        {
            _fullscreenBarHideTimer.Stop();
            TryHideFullscreenBar();
        };
        _fullscreenHintTimer.Tick += (_, _) => HideFullscreenHint();

        // Suivi de la souris sur toute la zone d'affichage, y compris quand
        // l'image distante a déjà traité l'événement.
        ScreenHost.AddHandler(PointerMovedEvent, ScreenHost_PointerMoved,
                              RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        FullscreenBar.PointerEntered += (_, _) => _fullscreenBarHideTimer.Stop();
        FullscreenBar.PointerExited += (_, _) => RestartFullscreenBarHideTimer();

        // Le menu des raccourcis s'ouvre hors de la barre : la souris la quitte
        // pour l'atteindre. La barre reste affichée tant qu'il est ouvert.
        ((MenuFlyout)BarShortcutsButton.Flyout!).Closed += (_, _) =>
        {
            if (FullscreenBar.IsVisible) RestartFullscreenBarHideTimer();
        };
        Closed += (_, _) =>
        {
            _fullscreenBarHideTimer.Stop();
            _fullscreenHintTimer.Stop();
        };
    }

    private void ScreenHost_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_docked || Host.WindowState != WindowState.FullScreen || FullscreenBar.IsVisible) return;
        // Pas pendant un glisser : la barre s'ouvrirait sous une sélection qui remonte.
        if (_buttonsDown.Count > 0) return;
        if (e.GetPosition(ScreenHost).Y <= FullscreenBarTriggerHeight) ShowFullscreenBar();
    }

    private void ShowFullscreenBar()
    {
        FullscreenBarTitle.Text = _node.Name;
        FullscreenBarDot.Fill = StatusDot.Fill;
        // Le rappel d'entrée en plein écran occupe la même place.
        HideFullscreenHint();
        FullscreenBar.IsVisible = true;
        Dispatcher.UIThread.Post(() => FullscreenBar.Opacity = 1, DispatcherPriority.Render);
        // Si la souris n'y entre pas (bord touché dans un coin), elle repart.
        RestartFullscreenBarHideTimer();
    }

    private void TryHideFullscreenBar()
    {
        var shortcutsOpen = ((MenuFlyout)BarShortcutsButton.Flyout!).IsOpen;
        if (FullscreenBar.IsPointerOver || shortcutsOpen) return;
        HideFullscreenBar();
    }

    private void HideFullscreenBar()
    {
        _fullscreenBarHideTimer.Stop();
        FullscreenBar.Opacity = 0;
        FullscreenBar.IsVisible = false;
    }

    private void RestartFullscreenBarHideTimer()
    {
        _fullscreenBarHideTimer.Stop();
        _fullscreenBarHideTimer.Start();
    }

    private void ShowFullscreenHint()
    {
        FullscreenHint.IsVisible = true;
        _fullscreenHintTimer.Stop();
        _fullscreenHintTimer.Start();
    }

    private void HideFullscreenHint()
    {
        _fullscreenHintTimer.Stop();
        FullscreenHint.IsVisible = false;
    }

    private async void Reboot_Click(object? sender, RoutedEventArgs e) => await PowerAsync(PowerActionType.Reboot);
    private async void Sleep_Click(object? sender, RoutedEventArgs e) => await PowerAsync(PowerActionType.Sleep);
    private async void PowerOff_Click(object? sender, RoutedEventArgs e) => await PowerAsync(PowerActionType.PowerOff);

    private async Task PowerAsync(PowerActionType action)
    {
        var (title, detail) = action switch
        {
            PowerActionType.Reboot => ($"Redémarrer « {_node.Name} » ?",
                "La machine va redémarrer. Les travaux non enregistrés seront perdus.\n\n"
                + "La session de bureau reprendra d'elle-même au retour de la machine."),
            PowerActionType.Sleep => ($"Mettre « {_node.Name} » en veille ?",
                "La machine sera suspendue et ne répondra plus jusqu'à son réveil."),
            _ => ($"Éteindre « {_node.Name} » ?",
                "La machine sera arrêtée. Il faudra un réveil Wake-on-LAN ou une action physique pour la rallumer."),
        };
        if (!await Dialogs.ConfirmAsync(Host, title, detail)) return;
        try
        {
            await _connection.PowerActionAsync(_node.Id, action);
            var done = action switch
            {
                PowerActionType.Reboot => "Redémarrage demandé.",
                PowerActionType.Sleep => "Mise en veille demandée.",
                _ => "Extinction demandée.",
            };
            ShowStatus(done);
            Log.Info($"{done} ({_node.Name}, depuis le bureau à distance)", "Bureau");
            if (action == PowerActionType.Reboot) WatchForReboot();
        }
        catch (Exception ex) { ShowStatus($"Action refusée : {ex.Message}"); }
    }

    private async void OpenWebPage_Click(object? sender, RoutedEventArgs e)
    {
        if (_connection.Profile.DeviceWebUrl(_node.Id) is { } url)
            await Launcher.LaunchUriAsync(new Uri(url));
    }

    // MARK: - Souris

    /// <summary>Position dans le repère de l'image, ramenée aux pixels distants.</summary>
    private bool TryMapToRemote(PointerEventArgs e, out int x, out int y)
    {
        x = y = 0;
        int rw = _session.ScreenWidth, rh = _session.ScreenHeight;
        var image = ActiveImage;
        var size = image.Bounds.Size;
        if (rw <= 0 || rh <= 0 || size.Width <= 0 || size.Height <= 0) return false;

        var p = e.GetPosition(image);
        x = (int)Math.Round(Math.Clamp(p.X / size.Width * rw, 0, rw));
        y = (int)Math.Round(Math.Clamp(p.Y / size.Height * rh, 0, rh));
        return true;
    }

    private void Screen_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_viewOnly) return;
        if (TryMapToRemote(e, out int x, out int y)) _session.SendMouseMove(x, y);
    }

    private void Screen_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ScreenHost.Focus();
        if (_viewOnly) return;
        var kind = e.GetCurrentPoint(ActiveImage).Properties.PointerUpdateKind;

        // Boutons latéraux : traduits en Précédent / Suivant, comme sous Windows.
        if (kind is PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)
        {
            _session.SendKeyTap(kind == PointerUpdateKind.XButton1Pressed ? Vk.BrowserBack : Vk.BrowserForward);
            e.Handled = true;
            return;
        }

        RemoteButton? button = kind switch
        {
            PointerUpdateKind.LeftButtonPressed => RemoteButton.Left,
            PointerUpdateKind.RightButtonPressed => RemoteButton.Right,
            PointerUpdateKind.MiddleButtonPressed => RemoteButton.Middle,
            _ => null,
        };
        if (button is null || !TryMapToRemote(e, out int x, out int y)) return;

        e.Pointer.Capture(ActiveImage);
        _buttonsDown.Add(button.Value);
        _session.SendMouseButton(button.Value, down: true, x, y);
        e.Handled = true;
    }

    private void Screen_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        RemoteButton? button = e.InitialPressMouseButton switch
        {
            Avalonia.Input.MouseButton.Left => RemoteButton.Left,
            Avalonia.Input.MouseButton.Right => RemoteButton.Right,
            Avalonia.Input.MouseButton.Middle => RemoteButton.Middle,
            _ => null,
        };
        if (button is not null && _buttonsDown.Remove(button.Value) && TryMapToRemote(e, out int x, out int y))
            _session.SendMouseButton(button.Value, down: false, x, y);

        var props = e.GetCurrentPoint(ActiveImage).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed && !props.IsMiddleButtonPressed)
            e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>Boutons transmis « enfoncés » à la machine, pas encore relâchés.</summary>
    private readonly HashSet<RemoteButton> _buttonsDown = new();

    /// <summary>
    /// Capture perdue sans relâchement (boîte de dialogue, changement
    /// d'application) : on relâche les boutons restés enfoncés côté distant —
    /// ceux-là seulement, comme la version Windows.
    /// </summary>
    private void Screen_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_buttonsDown.Count == 0) return;
        _buttonsDown.Clear();
        _session.ReleaseMouseButtons();
    }

    /// <summary>
    /// Molette : le protocole attend des crans de 120. Le trackpad d'un Mac
    /// envoie de petits déplacements fractionnaires : on les cumule.
    /// </summary>
    private double _wheelAccumulator;

    private void Screen_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        if (_viewOnly || !TryMapToRemote(e, out int x, out int y)) return;
        _wheelAccumulator += e.Delta.Y * 120;
        int notches = (int)(_wheelAccumulator / 120);
        if (notches == 0) return;
        _wheelAccumulator -= notches * 120;
        _session.SendScroll(notches * 120, x, y);
    }

    // MARK: - Clavier

    private readonly HashSet<Key> _heldModifiers = new();
    private readonly Dictionary<Key, byte> _sentModifiers = new();
    private readonly Dictionary<Key, byte> _sentKeys = new();

    private bool CommandHeld =>
        _heldModifiers.Contains(Key.LeftCtrl) || _heldModifiers.Contains(Key.RightCtrl)
        || _heldModifiers.Contains(Key.LWin) || _heldModifiers.Contains(Key.RWin);

    /// <summary>
    /// Raccourcis locaux, comme sous Windows : Ctrl+Maj+lettre ne part pas
    /// vers la machine distante.
    /// </summary>
    private bool HandleLocalShortcut(KeyEventArgs e)
    {
        // Ctrl+Maj+Alt+V : coller en simulant la frappe.
        if (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt) && e.Key == Key.V)
        {
            PasteAsTyping_Click(this, e);
            return true;
        }
        if (e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift)) return false;
        switch (e.Key)
        {
            case Key.V: Paste_Click(this, e); break;
            case Key.C: CopyFromRemote_Click(this, e); break;
            case Key.S: Screenshot_Click(this, e); break;
            case Key.F: ToggleFullscreen(); break;
            case Key.R: Refresh_Click(this, e); break;
            case Key.D: ReleaseModifiers_Click(this, e); break;
            case Key.A: FitCheck.IsChecked = FitCheck.IsChecked != true; break;
            case Key.B: Reboot_Click(this, e); break;
            case Key.W: OpenWebPage_Click(this, e); break;
            case Key.T: OpenTerminal?.Invoke(); break;
            case Key.E: OpenFiles?.Invoke(); break;
            case Key.P: OpenProcesses?.Invoke(); break;
            case Key.Z: ToggleZoom(); break;
            case Key.Left when _displayChoices.Count > 0: CycleDisplay(-1); break;
            case Key.Right when _displayChoices.Count > 0: CycleDisplay(+1); break;
            case Key.O: SetViewOnly(!_viewOnly); break;
            case Key.Delete: SendCtrlAltDel(); break;
            default: return false;
        }
        return true;
    }

    private void Screen_KeyDown(object? sender, KeyEventArgs e)
    {
        // Champ de saisie d'une boîte de dialogue, menu ouvert : pas pour nous.
        if (e.Source is TextBox) return;
        var key = e.Key;

        if (_zoomSelecting && key == Key.Escape)
        {
            CancelZoomSelection();
            ShowStatus("Zoom annulé.");
            e.Handled = true;
            return;
        }

        if (HandleLocalShortcut(e))
        {
            e.Handled = true;
            ReleaseAllKeys();
            return;
        }

        // Vue seule : rien ne part. La touche n'est pas marquée traitée, pour
        // que les raccourcis du système fonctionnent. Les modificateurs restent
        // suivis : Ctrl+Maj+O doit pouvoir rendre la main.
        if (_viewOnly)
        {
            if (KeyMapping.IsModifier(key)) _heldModifiers.Add(key);
            return;
        }

        if (KeyMapping.IsModifier(key))
        {
            _heldModifiers.Add(key);
            e.Handled = true;
            return;
        }

        // Caractère sans Ctrl ni Cmd : il partira de Screen_TextInput. La touche
        // n'est pas marquée traitée, sans quoi la saisie de texte n'aurait pas lieu.
        if ((KeyMapping.IsCharacterKey(key) || key == Key.Space) && !CommandHeld) return;

        byte vk = KeyMapping.ToVirtualKey(key);
        if (vk == 0) return;

        PressHeldModifiers();
        _sentKeys[key] = vk;
        _session.SendKey(vk, KeyAction.Down);
        e.Handled = true;
    }

    private void Screen_KeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox) return;
        var key = e.Key;
        if (KeyMapping.IsModifier(key))
        {
            _heldModifiers.Remove(key);
            if (_sentModifiers.Remove(key, out var modifierVk)) _session.SendKey(modifierVk, KeyAction.Up);
            e.Handled = true;
            return;
        }

        if (_sentKeys.Remove(key, out var vk))
        {
            _session.SendKey(vk, KeyAction.Up);
            e.Handled = true;
        }
    }

    private void Screen_TextInput(object? sender, TextInputEventArgs e)
    {
        if (_viewOnly || string.IsNullOrEmpty(e.Text)) return;
        foreach (var rune in e.Text.EnumerateRunes())
        {
            // Chemin Unicode limité à 16 bits : émojis ignorés.
            if (Rune.IsControl(rune) || rune.Value > 0xFFFF) continue;
            _session.SendUnicode(rune.Value);
        }
        e.Handled = true;
    }

    private void PressHeldModifiers()
    {
        foreach (var modifier in _heldModifiers)
        {
            if (_sentModifiers.ContainsKey(modifier)) continue;
            byte vk = modifier switch
            {
                // Cmd devient Ctrl : les raccourcis Mac gardent leur sens sous Windows.
                Key.LWin or Key.RWin => 0xA2,
                _ => KeyMapping.ToVirtualKey(modifier),
            };
            if (vk == 0) continue;
            _sentModifiers[modifier] = vk;
            _session.SendKey(vk, KeyAction.Down);
        }
    }

    private void ReleaseAllKeys()
    {
        foreach (var vk in _sentKeys.Values) _session.SendKey(vk, KeyAction.Up);
        foreach (var vk in _sentModifiers.Values) _session.SendKey(vk, KeyAction.Up);
        _sentKeys.Clear();
        _sentModifiers.Clear();
        _heldModifiers.Clear();
        _session.ReleaseModifiers();
    }
}
