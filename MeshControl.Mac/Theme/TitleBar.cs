using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MeshControl.Mac;

/// <summary>
/// Barre de titre de l'application, reprise de la version Windows (gabarit
/// AppWindow de Theme.xaml) : 44 px, fond de carte, filet en bas, icône et
/// titre — ou un contenu fourni par la fenêtre (<see cref="Content"/>).
///
/// Sous Windows, les boutons Réduire / Agrandir / Fermer sont redessinés comme
/// dans la version WPF. Sous macOS, les boutons natifs (« feux tricolores »)
/// restent à leur place, à gauche de la barre : ce sont eux que l'utilisateur
/// attend, avec le plein écran et l'agencement des fenêtres qu'ils portent.
/// </summary>
public sealed class TitleBar : Border
{
    public const double BarHeight = 44;

    /// <summary>Place laissée aux boutons natifs de macOS, à gauche.</summary>
    private const double MacButtonsWidth = 78;

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<TitleBar, string?>(nameof(Title));

    public static readonly StyledProperty<Control?> ContentProperty =
        AvaloniaProperty.Register<TitleBar, Control?>(nameof(Content));

    public static readonly StyledProperty<bool> ShowMinMaxProperty =
        AvaloniaProperty.Register<TitleBar, bool>(nameof(ShowMinMax), true);

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>Contenu posé dans la barre à la place du titre (onglets, nom de machine).</summary>
    public Control? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }

    /// <summary>Faux pour une boîte de dialogue : ni réduction ni agrandissement.</summary>
    public bool ShowMinMax { get => GetValue(ShowMinMaxProperty); set => SetValue(ShowMinMaxProperty, value); }

    private readonly TextBlock _title;
    private readonly ContentControl _host;
    private readonly StackPanel _buttons;
    private readonly Button _minButton;
    private readonly Button _maxButton;

    private static Bitmap? _appIcon;

    public TitleBar()
    {
        Height = BarHeight;
        Background = Palette.Resource("CardBrush");
        BorderBrush = Palette.Resource("SeparatorBrush");
        BorderThickness = new Thickness(0, 0, 0, 1);

        var left = new DockPanel
        {
            Margin = new Thickness(OperatingSystem.IsMacOS() ? MacButtonsWidth : 15, 0, 8, 0),
            LastChildFill = true,
        };

        var icon = new Image
        {
            Source = _appIcon ??= LoadIcon(),
            Width = 17,
            Height = 17,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(icon, Dock.Left);
        left.Children.Add(icon);

        _title = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.Resource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        DockPanel.SetDock(_title, Dock.Left);
        left.Children.Add(_title);

        _host = new ContentControl { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_host);

        _minButton = WindowButton(Glyph("M 0,5 L 10,5"), "Réduire");
        _minButton.Click += (_, _) => { if (Window is { } w) w.WindowState = WindowState.Minimized; };

        _maxButton = WindowButton(Glyph("M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z"), "Agrandir");
        _maxButton.Click += (_, _) => ToggleMaximize();

        var close = WindowButton(Glyph("M 0,0 L 10,10 M 10,0 L 0,10"), "Fermer");
        close.Classes.Add("close");
        close.Click += (_, _) => Window?.Close();

        _buttons = new StackPanel { Orientation = Orientation.Horizontal, IsVisible = !OperatingSystem.IsMacOS() };
        _buttons.Children.Add(_minButton);
        _buttons.Children.Add(_maxButton);
        _buttons.Children.Add(close);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(left);
        Grid.SetColumn(_buttons, 1);
        grid.Children.Add(_buttons);
        Child = grid;

        // Rôles transmis au système : la barre déplace la fenêtre (avec
        // l'accrochage aux bords et le double-clic natifs), ses boutons
        // reçoivent les clics normalement. Une fenêtre qui loge des contrôles
        // dans la barre leur donne le rôle User (voir DesktopWindow).
        WindowDecorationProperties.SetElementRole(this, WindowDecorationsElementRole.TitleBar);
        WindowDecorationProperties.SetElementRole(_minButton, WindowDecorationsElementRole.User);
        WindowDecorationProperties.SetElementRole(_maxButton, WindowDecorationsElementRole.User);
        WindowDecorationProperties.SetElementRole(close, WindowDecorationsElementRole.User);

        PointerPressed += OnPointerPressed;
        DoubleTapped += (_, e) =>
        {
            if (IsOnButton(e.Source)) return;
            ToggleMaximize();
        };
    }

    private Window? Window => TopLevel.GetTopLevel(this) as Window;

    private static Bitmap? LoadIcon()
    {
        try { return new Bitmap(AssetLoader.Open(new Uri("avares://MeshControl.Mac/Assets/app.png"))); }
        catch { return null; }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Window is { } window)
        {
            window.PropertyChanged += (_, args) =>
            {
                if (args.Property == Window.WindowStateProperty) UpdateMaxGlyph(window);
            };
            UpdateMaxGlyph(window);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty || change.Property == ContentProperty)
        {
            _title.Text = Title;
            _host.Content = Content;
            // Titre textuel tant que la fenêtre ne loge rien dans la barre.
            _title.IsVisible = Content is null;
            _host.IsVisible = Content is not null;
        }
        else if (change.Property == ShowMinMaxProperty)
        {
            _minButton.IsVisible = ShowMinMax;
            _maxButton.IsVisible = ShowMinMax;
        }
    }

    private void UpdateMaxGlyph(Window window)
    {
        bool maximized = window.WindowState == WindowState.Maximized;
        _maxButton.Content = Glyph(maximized
            ? "M 2.5,0.5 L 9.5,0.5 L 9.5,7.5 M 0.5,2.5 L 7.5,2.5 L 7.5,9.5 L 0.5,9.5 Z"
            : "M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z");
        ToolTip.SetTip(_maxButton, maximized ? "Restaurer" : "Agrandir");
    }

    private void ToggleMaximize()
    {
        if (Window is not { } window || !ShowMinMax || !window.CanResize) return;
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    /// <summary>Glisser la barre déplace la fenêtre, comme une barre de titre système.</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsOnButton(e.Source)) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.ClickCount > 1) return;
        Window?.BeginMoveDrag(e);
    }

    /// <summary>Clic sur un contrôle posé dans la barre (bouton, onglet) : pas de déplacement.</summary>
    private bool IsOnButton(object? source)
    {
        for (var element = source as StyledElement; element is not null && element != this; element = element.Parent)
        {
            if (element is Button or TabItem or TextBox) return true;
        }
        return false;
    }

    private static Button WindowButton(Control content, string tip)
    {
        var button = new Button { Content = content };
        button.Classes.Add("window");
        ToolTip.SetTip(button, tip);
        return button;
    }

    /// <summary>Pictogramme de 10 × 10, au trait d'un pixel, comme Segoe MDL2.</summary>
    private static Control Glyph(string data)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 1,
            Width = 10,
            Height = 10,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty,
                  path.GetObservable(Avalonia.Controls.Documents.TextElement.ForegroundProperty));
        return path;
    }
}
