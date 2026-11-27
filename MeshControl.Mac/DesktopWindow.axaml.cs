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
public partial class DesktopWindow : ChromeWindow
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
        WindowDecorationProperties.SetElementRole(ToolbarControls, Avalonia.Input.WindowDecorationsElementRole.User);

        // Réglages par défaut (volet des réglages de la fenêtre principale).
        var quality = App.Settings.DesktopQuality;
        QualityCombo.SelectedIndex = Math.Max(0, Array.IndexOf(QualityTags, quality));
        _session.Quality = (DesktopQuality)quality;
        var fps = DesktopSession.NearestFrameRate(App.Settings.DesktopFrameRate);
        FrameRateCombo.SelectedIndex = Math.Max(0, Array.IndexOf(FrameRates, fps));
        _session.FrameRate = fps;
        _ready = true;

        _session.StateChanged += UpdateState;
        _session.ScreenSizeChanged += OnScreenSizeChanged;
        _session.FrameUpdated += OnFrameUpdated;
        _session.DisplaysChanged += UpdateDisplays;

        foreach (var image in new[] { ScreenImage, ScreenImageNative })
        {
            image.PointerMoved += Screen_PointerMoved;
            image.PointerPressed += Screen_PointerPressed;
            image.PointerReleased += Screen_PointerReleased;
            image.PointerCaptureLost += Screen_PointerCaptureLost;
            image.PointerWheelChanged += Screen_PointerWheelChanged;
        }

        AddHandler(KeyDownEvent, Screen_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, Screen_KeyUp, RoutingStrategies.Tunnel);
        ScreenHost.TextInput += Screen_TextInput;
        ScreenHost.PointerPressed += (_, _) => ScreenHost.Focus();
        Deactivated += (_, _) => ReleaseAllKeys();

        BuildShortcutsMenu();
        UpdateScaleMenu();
        InitializeToolbarLayout();

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => UpdateStats();

        UpdateState();
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ScreenHost.Focus();
        _statsTimer.Start();
        await _session.StartAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _statsTimer.Stop();
        _typingCts?.Cancel();
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
        UpdateStats();
        ScreenHost.Focus();
    }

    private void FrameRateCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || FrameRateCombo.SelectedIndex < 0) return;
        _session.FrameRate = FrameRates[FrameRateCombo.SelectedIndex];
        ScreenHost.Focus();
    }

    private void Fit_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        bool fit = FitCheck.IsChecked == true;
        FitHost.IsVisible = fit;
        Scroller.IsVisible = !fit;
        ActiveImage.InvalidateVisual();
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
        var menu = (MenuFlyout)ShortcutsButton.Flyout!;
        var system = _node.OsFamily == OSFamily.Linux ? KeyboardShortcuts.Linux : KeyboardShortcuts.Windows;
        foreach (var shortcut in system) menu.Items.Add(ShortcutItem(shortcut));
        menu.Items.Add(new Separator());
        foreach (var shortcut in KeyboardShortcuts.Editing) menu.Items.Add(ShortcutItem(shortcut));
    }

    private MenuItem ShortcutItem(RemoteShortcut shortcut)
    {
        var item = new MenuItem { Header = $"{shortcut.Label}  —  {shortcut.Description}" };
        item.Click += (_, _) =>
        {
            if (shortcut.Modifiers is [Vk.Ctrl, Vk.Alt] && shortcut.Key == Vk.Delete && _node.OsFamily != OSFamily.Linux)
                _session.SendCtrlAltDel();
            else
                _session.SendChord(shortcut.Modifiers, shortcut.Key, shortcut.Extended);
            ScreenHost.Focus();
        };
        return item;
    }

    private void UpdateDisplays()
    {
        DisplayButton.IsVisible = _session.HasMultipleDisplays;
        var menu = (MenuFlyout)DisplayButton.Flyout!;
        menu.Items.Clear();
        foreach (var display in _session.DisplayList)
        {
            var item = new MenuItem
            {
                Header = display.Label,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = display.Id == _session.SelectedDisplay,
            };
            var id = display.Id;
            item.Click += (_, _) => { _session.SelectDisplay(id); ScreenHost.Focus(); };
            menu.Items.Add(item);
        }
        if (_session.HasMultipleDisplays)
        {
            var all = new MenuItem
            {
                Header = "Tous les écrans",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _session.SelectedDisplay == RemoteDisplay.AllDisplays,
            };
            all.Click += (_, _) => { _session.SelectDisplay(RemoteDisplay.AllDisplays); ScreenHost.Focus(); };
            menu.Items.Insert(0, all);
        }
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

    // MARK: - Presse-papiers

    private CancellationTokenSource? _typingCts;

    private async Task<string?> LocalClipboardTextAsync()
        => Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;

    /// <summary>
    /// Coller : le presse-papiers local devient celui de la machine, puis
    /// Ctrl+V. Si le serveur refuse l'écriture, repli sur la frappe.
    /// </summary>
    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        ScreenHost.Focus();
        var text = await LocalClipboardTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            ShowStatus("Presse-papiers vide");
            return;
        }
        try
        {
            await _connection.SetRemoteClipboardAsync(_node.Id, text);
            await Task.Delay(150);
            _session.SendChord([Vk.Ctrl], Vk.Letter('V'));
        }
        catch
        {
            await TypeAsync(text);
        }
    }

    private async void PasteAsTyping_Click(object? sender, RoutedEventArgs e)
    {
        ScreenHost.Focus();
        if (_typingCts is not null)
        {
            _typingCts.Cancel();
            return;
        }
        var text = await LocalClipboardTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            ShowStatus("Presse-papiers vide");
            return;
        }
        await TypeAsync(text);
    }

    private async Task TypeAsync(string text)
    {
        _typingCts = new CancellationTokenSource();
        PasteTypingLabel.Text = "Arrêter";
        try { await _session.SendTextAsync(text, _typingCts.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            _typingCts = null;
            PasteTypingLabel.Text = "Taper";
        }
    }

    private async void CopyFromRemote_Click(object? sender, RoutedEventArgs e)
    {
        ScreenHost.Focus();
        try
        {
            var text = await _connection.GetRemoteClipboardAsync(_node.Id);
            if (string.IsNullOrEmpty(text))
            {
                ShowStatus("Presse-papiers distant vide");
                return;
            }
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            ShowStatus("Presse-papiers récupéré");
        }
        catch (Exception ex)
        {
            ShowStatus($"Copie impossible : {ex.Message}");
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
        bool full = WindowState != WindowState.FullScreen;
        WindowState = full ? WindowState.FullScreen : WindowState.Normal;
        WindowTitleBar.IsVisible = !full;
        StatusBar.IsVisible = !full;
        if (full)
        {
            OverlayText.Text = "Plein écran — Ctrl+Maj+F pour revenir";
            OverlayText.IsVisible = true;
            DispatcherTimer.RunOnce(UpdateState, TimeSpan.FromSeconds(2.5));
        }
        ScreenHost.Focus();
    }

    private async void Reboot_Click(object? sender, RoutedEventArgs e) => await PowerAsync(PowerActionType.Reboot);
    private async void Sleep_Click(object? sender, RoutedEventArgs e) => await PowerAsync(PowerActionType.Sleep);
    private async void PowerOff_Click(object? sender, RoutedEventArgs e) => await PowerAsync(PowerActionType.PowerOff);

    private async Task PowerAsync(PowerActionType action)
    {
        var (title, detail) = action switch
        {
            PowerActionType.Reboot => ($"Redémarrer « {_node.Name} » ?",
                "La machine va redémarrer. Les travaux non enregistrés seront perdus. La session reprendra à son retour."),
            PowerActionType.Sleep => ($"Mettre « {_node.Name} » en veille ?",
                "La machine sera suspendue et ne répondra plus jusqu'à son réveil."),
            _ => ($"Éteindre « {_node.Name} » ?",
                "La machine sera arrêtée. Il faudra un réveil Wake-on-LAN ou une action physique pour la rallumer."),
        };
        if (!await Dialogs.ConfirmAsync(this, title, detail)) return;
        try
        {
            await _connection.PowerActionAsync(_node.Id, action);
            ShowStatus("Ordre envoyé");
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
        if (TryMapToRemote(e, out int x, out int y)) _session.SendMouseMove(x, y);
    }

    private void Screen_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ScreenHost.Focus();
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
        if (button is not null && TryMapToRemote(e, out int x, out int y))
            _session.SendMouseButton(button.Value, down: false, x, y);

        var props = e.GetCurrentPoint(ActiveImage).Properties;
        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed && !props.IsMiddleButtonPressed)
            e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void Screen_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        _session.ReleaseMouseButtons();

    /// <summary>
    /// Molette : le protocole attend des crans de 120. Le trackpad d'un Mac
    /// envoie de petits déplacements fractionnaires : on les cumule.
    /// </summary>
    private double _wheelAccumulator;

    private void Screen_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        if (!TryMapToRemote(e, out int x, out int y)) return;
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
        if (e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift)) return false;
        switch (e.Key)
        {
            case Key.V: Paste_Click(this, e); break;
            case Key.C: CopyFromRemote_Click(this, e); break;
            case Key.S: Screenshot_Click(this, e); break;
            case Key.F: ToggleFullscreen(); break;
            case Key.R: _session.RequestRefresh(); break;
            case Key.D: ReleaseModifiers_Click(this, e); break;
            case Key.A: FitCheck.IsChecked = FitCheck.IsChecked != true; break;
            case Key.B: Reboot_Click(this, e); break;
            case Key.W: OpenWebPage_Click(this, e); break;
            case Key.Delete: _session.SendCtrlAltDel(); break;
            default: return false;
        }
        return true;
    }

    private void Screen_KeyDown(object? sender, KeyEventArgs e)
    {
        // Champ de saisie d'une boîte de dialogue, menu ouvert : pas pour nous.
        if (e.Source is TextBox) return;
        var key = e.Key;

        if (HandleLocalShortcut(e))
        {
            e.Handled = true;
            ReleaseAllKeys();
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
        if (string.IsNullOrEmpty(e.Text)) return;
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
