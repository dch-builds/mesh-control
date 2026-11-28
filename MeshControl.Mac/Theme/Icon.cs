using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeshControl.Mac;

/// <summary>Pictogrammes de l'interface (équivalents des glyphes Segoe MDL2 de la version Windows).</summary>
public enum IconKind
{
    None,
    Search,          // E721
    Close,           // E711
    ChevronDown,     // E70D
    ChevronUp,       // E70E
    ChevronRight,    // E76C
    Filter,          // E71C
    Upload,          // E898
    Sort,            // E8CB
    Sidebar,         // E8A4
    Desktop,         // E7F4
    Terminal,        // E756
    Files,           // F12B
    Power,           // E7E8
    Bell,            // E7ED
    List,            // E71D
    Star,            // E735
    History,         // E81C
    Person,          // E77B
    Folder,          // ED25
    Pin,             // E718
    Hidden,          // ED1A
    Check,           // E73E
    Refresh,         // E72C
    Paste,           // E77F
    Copy,            // E8C8
    Camera,          // 📷
    Keyboard,        // E765 / ⌨
    FullScreen,      // ⛶
    BackToWindow,    // E73F
    Drive,           // EDA2
    Image,           // EB9F
    Video,           // E714
    Audio,           // E8D6
    Archive,         // E7B8
    Code,            // E943
    Document,        // E8A5
    FileGeneric,     // E7C3
    Download,        // E896
    NewFolder,       // E8F4
    Devices,         // E977
    ArrowUp,         // E74A
    ArrowRight,      // E72A
    ArrowLeft,       // E72B
    Computer,        // E7F8
    Error,           // E783
    Warning,         // E7BA
    Network,         // E968
    Settings,        // E713
    AppWindow,       // E737
    Sync,            // E895
    Add,             // E710
    Send,            // E724
    ZoomIn,          // E8A3
    ZoomOut,         // E71F
    View,            // E890
    Stop,            // E71A
    BellOff,         // EA8F
    Home,            // E80F
}

/// <summary>
/// Pictogramme vectoriel, tracé au trait dans un carré de 16 unités.
///
/// La version Windows dessine ses pictogrammes avec la police Segoe MDL2
/// Assets, présente sur tout Windows mais absente de macOS et non
/// redistribuable. Ces tracés reprennent les mêmes formes, à la même taille
/// apparente (<see cref="Size"/> joue le rôle de FontSize).
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<IconKind> KindProperty =
        AvaloniaProperty.Register<Icon, IconKind>(nameof(Kind));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 12);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<Icon>();

    public IconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Taille apparente, comme la FontSize d'un glyphe.</summary>
    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size * 1.1, Size * 1.1);

    public override void Render(DrawingContext context)
    {
        if (Kind == IconKind.None || Foreground is not { } brush) return;
        var (data, filled) = Shapes[Kind];
        var geometry = Geometries.TryGetValue(Kind, out var cached)
            ? cached
            : Geometries[Kind] = StreamGeometry.Parse(data);

        double scale = Bounds.Width / 16.0;
        var offset = new Vector((Bounds.Width - 16 * scale) / 2, (Bounds.Height - 16 * scale) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
        {
            if (filled)
                context.DrawGeometry(brush, null, geometry);
            else
                // Trait d'environ un pixel aux petites tailles, proportionnel
                // au-delà — comme l'épaisseur d'un glyphe de police.
                context.DrawGeometry(null, new Pen(brush, Math.Max(1.3, 1.05 / scale),
                                                   lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round),
                                     geometry);
        }
    }

    private static readonly Dictionary<IconKind, Geometry> Geometries = new();

    /// <summary>Tracés dans un carré de 16 × 16 ; « filled » pour les formes pleines.</summary>
    private static readonly Dictionary<IconKind, (string Data, bool Filled)> Shapes = new()
    {
        [IconKind.Home] = ("M 1.5,8 L 8,2 L 14.5,8 M 3.5,6.3 L 3.5,14 L 12.5,14 L 12.5,6.3", false),
        [IconKind.BellOff] = ("M 3,11.5 L 3,7 A 5,5 0 0 1 13,7 L 13,11.5 L 14.5,13 L 1.5,13 Z M 6.2,13 A 1.8,1.8 0 0 0 9.8,13 M 1.5,1.5 L 14.5,14.5", false),
        [IconKind.ZoomIn] = ("M 11.5,6.5 A 5,5 0 1 1 1.5,6.5 A 5,5 0 1 1 11.5,6.5 Z M 10.1,10.1 L 14.5,14.5 M 4,6.5 L 9,6.5 M 6.5,4 L 6.5,9", false),
        [IconKind.ZoomOut] = ("M 11.5,6.5 A 5,5 0 1 1 1.5,6.5 A 5,5 0 1 1 11.5,6.5 Z M 10.1,10.1 L 14.5,14.5 M 4,6.5 L 9,6.5", false),
        [IconKind.View] = ("M 1.2,10.5 A 7.4,6.4 0 0 1 14.8,10.5 M 10,10.5 A 2,2 0 1 1 6,10.5 A 2,2 0 1 1 10,10.5 Z", false),
        [IconKind.Stop] = ("M 2.5,2.5 L 13.5,2.5 L 13.5,13.5 L 2.5,13.5 Z", false),
        [IconKind.Add] = ("M 8,1.5 L 8,14.5 M 1.5,8 L 14.5,8", false),
        [IconKind.Send] = ("M 1.5,7.2 L 14.5,1.5 L 9.2,14.5 L 7.3,8.7 Z M 7.3,8.7 L 14.5,1.5", false),
        [IconKind.Settings] = ("M 13.11,6.25 L 13.35,7.24 L 15.16,7.27 L 15.16,8.73 L 13.35,8.76 L 12.85,10.38 L 12.32,11.24 L 13.58,12.55 L 12.55,13.58 L 11.24,12.32 L 9.75,13.11 L 8.76,13.35 L 8.73,15.16 L 7.27,15.16 L 7.24,13.35 L 5.62,12.85 L 4.76,12.32 L 3.45,13.58 L 2.42,12.55 L 3.68,11.24 L 2.89,9.75 L 2.65,8.76 L 0.84,8.73 L 0.84,7.27 L 2.65,7.24 L 3.15,5.62 L 3.68,4.76 L 2.42,3.45 L 3.45,2.42 L 4.76,3.68 L 6.25,2.89 L 7.24,2.65 L 7.27,0.84 L 8.73,0.84 L 8.76,2.65 L 10.38,3.15 L 11.24,3.68 L 12.55,2.42 L 13.58,3.45 L 12.32,4.76 Z M 10.4,8 A 2.4,2.4 0 1 1 5.6,8 A 2.4,2.4 0 1 1 10.4,8 Z", false),
        [IconKind.AppWindow] = ("M 1.5,2.5 L 14.5,2.5 L 14.5,13.5 L 1.5,13.5 Z M 1.5,5.5 L 14.5,5.5", false),
        [IconKind.Sync] = ("M 13.9,7 A 6,6 0 0 0 3.76,3.76 M 3.6,1 L 3.6,3.9 L 6.5,3.9 M 2.1,9 A 6,6 0 0 0 12.24,12.24 M 12.4,15 L 12.4,12.1 L 9.5,12.1", false),
        [IconKind.Search] = ("M 14.5,6.5 A 5,5 0 1 1 4.5,6.5 A 5,5 0 1 1 14.5,6.5 Z M 5.9,10.1 L 1.5,14.5", false),
        [IconKind.Close] = ("M 3.5,3.5 L 12.5,12.5 M 12.5,3.5 L 3.5,12.5", false),
        [IconKind.ChevronDown] = ("M 2.5,5.5 L 8,11 L 13.5,5.5", false),
        [IconKind.ChevronUp] = ("M 2.5,10.5 L 8,5 L 13.5,10.5", false),
        [IconKind.ChevronRight] = ("M 5.5,2.5 L 11,8 L 5.5,13.5", false),
        [IconKind.Filter] = ("M 1.5,2.5 L 14.5,2.5 L 9.5,8.5 L 9.5,13 L 6.5,14.5 L 6.5,8.5 Z", false),
        [IconKind.Upload] = ("M 2.5,1.8 L 13.5,1.8 M 8,14.5 L 8,4.8 M 3.8,9 L 8,4.8 L 12.2,9", false),
        [IconKind.Sort] = ("M 4.5,14 L 4.5,2 M 1.5,5 L 4.5,2 L 7.5,5 M 11.5,2 L 11.5,14 M 8.5,11 L 11.5,14 L 14.5,11", false),
        [IconKind.Sidebar] = ("M 1.5,3 L 14.5,3 M 5.5,8 L 14.5,8 M 5.5,13 L 14.5,13 M 1.5,8 L 2.5,8 M 1.5,13 L 2.5,13", false),
        [IconKind.Desktop] = ("M 1.5,2.5 L 14.5,2.5 L 14.5,11 L 1.5,11 Z M 5,14 L 11,14 M 8,11 L 8,14", false),
        [IconKind.Terminal] = ("M 1.5,2.5 L 14.5,2.5 L 14.5,13.5 L 1.5,13.5 Z M 4,6 L 6.5,8 L 4,10 M 8,10.5 L 12,10.5", false),
        [IconKind.Files] = ("M 1.5,3 L 6,3 L 7.5,4.8 L 14.5,4.8 L 14.5,13.5 L 1.5,13.5 Z M 1.5,7 L 14.5,7", false),
        [IconKind.Power] = ("M 8,1.5 L 8,7.5 M 4.6,3.8 A 5.6,5.6 0 1 0 11.4,3.8", false),
        [IconKind.Bell] = ("M 3,11.5 L 3,7 A 5,5 0 0 1 13,7 L 13,11.5 L 14.5,13 L 1.5,13 Z M 6.2,13 A 1.8,1.8 0 0 0 9.8,13", false),
        [IconKind.List] = ("M 1.5,3.5 L 2.5,3.5 M 5,3.5 L 14.5,3.5 M 1.5,8 L 2.5,8 M 5,8 L 14.5,8 M 1.5,12.5 L 2.5,12.5 M 5,12.5 L 14.5,12.5", false),
        [IconKind.Star] = ("M 8,0.8 L 10.2,5.4 L 15.2,6 L 11.5,9.5 L 12.5,14.5 L 8,12 L 3.5,14.5 L 4.5,9.5 L 0.8,6 L 5.8,5.4 Z", true),
        [IconKind.History] = ("M 2.2,8 A 5.8,5.8 0 1 0 4,3.8 M 1.5,1.8 L 1.5,5 L 4.7,5 M 8,4.5 L 8,8 L 10.5,9.8", false),
        [IconKind.Person] = ("M 11,4.8 A 3,3 0 1 1 5,4.8 A 3,3 0 1 1 11,4.8 Z M 2,14.5 C 2,11.2 4.7,9.6 8,9.6 C 11.3,9.6 14,11.2 14,14.5", false),
        [IconKind.Folder] = ("M 1.5,3 L 6,3 L 7.5,4.8 L 14.5,4.8 L 14.5,13.5 L 1.5,13.5 Z", false),
        [IconKind.Pin] = ("M 9.5,1.5 L 14.5,6.5 L 12.5,7 L 9.5,10 L 9.5,13 L 3,6.5 L 6,6.5 L 9,3.5 Z M 6.2,9.8 L 1.5,14.5", false),
        [IconKind.Hidden] = ("M 1,8 C 3,4.5 5.5,3 8,3 C 10.5,3 13,4.5 15,8 C 13,11.5 10.5,13 8,13 C 5.5,13 3,11.5 1,8 Z M 2,2 L 14,14", false),
        [IconKind.Check] = ("M 2.5,8.5 L 6,12 L 13.5,4", false),
        [IconKind.Refresh] = ("M 13.8,8 A 5.8,5.8 0 1 1 12,3.8 M 12.6,1 L 12.6,4.3 L 9.3,4.3", false),
        [IconKind.Paste] = ("M 5.5,2.5 L 2.5,2.5 L 2.5,14.5 L 13.5,14.5 L 13.5,2.5 L 10.5,2.5 M 5.5,1.2 L 10.5,1.2 L 10.5,3.8 L 5.5,3.8 Z", false),
        [IconKind.Copy] = ("M 5.5,5.5 L 14.5,5.5 L 14.5,14.5 L 5.5,14.5 Z M 3.5,10.5 L 1.5,10.5 L 1.5,1.5 L 10.5,1.5 L 10.5,3.5", false),
        [IconKind.Camera] = ("M 1.5,5 L 4.5,5 L 6,3 L 10,3 L 11.5,5 L 14.5,5 L 14.5,13.5 L 1.5,13.5 Z M 10.6,9 A 2.6,2.6 0 1 1 5.4,9 A 2.6,2.6 0 1 1 10.6,9 Z", false),
        [IconKind.Keyboard] = ("M 1,3.5 L 15,3.5 L 15,12.5 L 1,12.5 Z M 3.5,6.3 L 4,6.3 M 6,6.3 L 6.5,6.3 M 8.5,6.3 L 9,6.3 M 11,6.3 L 11.5,6.3 M 4.5,9.6 L 11.5,9.6", false),
        [IconKind.Drive] = ("M 1.5,9.5 L 3.5,3 L 12.5,3 L 14.5,9.5 L 14.5,13.5 L 1.5,13.5 Z M 1.5,9.5 L 14.5,9.5 M 11,11.5 L 12,11.5", false),
        [IconKind.Image] = ("M 1.5,2.5 L 14.5,2.5 L 14.5,13.5 L 1.5,13.5 Z M 1.5,11 L 5.5,7 L 9,10.5 L 11,8.5 L 14.5,12 M 11.2,5.5 A 1.2,1.2 0 1 1 8.8,5.5 A 1.2,1.2 0 1 1 11.2,5.5 Z", false),
        [IconKind.Video] = ("M 1.5,4 L 10.5,4 L 10.5,12 L 1.5,12 Z M 10.5,7 L 14.5,4.5 L 14.5,11.5 L 10.5,9", false),
        [IconKind.Audio] = ("M 6,12.5 A 2,2 0 1 1 2,12.5 A 2,2 0 1 1 6,12.5 Z M 6,12.5 L 6,2.5 L 14,1.5 L 14,11 M 14,11 A 2,2 0 1 1 10,11 A 2,2 0 1 1 14,11 Z", false),
        [IconKind.Archive] = ("M 1.5,2 L 14.5,2 L 14.5,5 L 1.5,5 Z M 2.5,5 L 2.5,14 L 13.5,14 L 13.5,5 M 6,8 L 10,8", false),
        [IconKind.Code] = ("M 5,4 L 1.5,8 L 5,12 M 11,4 L 14.5,8 L 11,12 M 9.5,2.5 L 6.5,13.5", false),
        [IconKind.Document] = ("M 3,1.5 L 10,1.5 L 13,4.5 L 13,14.5 L 3,14.5 Z M 10,1.5 L 10,4.5 L 13,4.5 M 5.5,8 L 10.5,8 M 5.5,10.5 L 10.5,10.5", false),
        [IconKind.FileGeneric] = ("M 3,1.5 L 10,1.5 L 13,4.5 L 13,14.5 L 3,14.5 Z M 10,1.5 L 10,4.5 L 13,4.5", false),
        [IconKind.Download] = ("M 8,1.5 L 8,11 M 3.8,6.8 L 8,11 L 12.2,6.8 M 2.5,14.2 L 13.5,14.2", false),
        [IconKind.NewFolder] = ("M 1.5,3 L 6,3 L 7.5,4.8 L 14.5,4.8 L 14.5,13.5 L 1.5,13.5 Z M 8,7 L 8,11.5 M 5.75,9.25 L 10.25,9.25", false),
        [IconKind.Devices] = ("M 1.5,2.5 L 11.5,2.5 L 11.5,9 L 1.5,9 Z M 4.5,12 L 8.5,12 M 6.5,9 L 6.5,12 M 10,6 L 14.5,6 L 14.5,14 L 10,14 Z", false),
        [IconKind.ArrowUp] = ("M 8,14 L 8,2.5 M 3,7.5 L 8,2.5 L 13,7.5", false),
        [IconKind.ArrowRight] = ("M 2,8 L 13.5,8 M 8.5,3 L 13.5,8 L 8.5,13", false),
        [IconKind.ArrowLeft] = ("M 14,8 L 2.5,8 M 7.5,3 L 2.5,8 L 7.5,13", false),
        [IconKind.Computer] = ("M 1.5,2.5 L 14.5,2.5 L 14.5,11 L 1.5,11 Z M 5,14 L 11,14 M 8,11 L 8,14", false),
        [IconKind.Error] = ("M 14.5,8 A 6.5,6.5 0 1 1 1.5,8 A 6.5,6.5 0 1 1 14.5,8 Z M 5.5,5.5 L 10.5,10.5 M 10.5,5.5 L 5.5,10.5", false),
        [IconKind.Warning] = ("M 8,1.5 L 15,14 L 1,14 Z M 8,6 L 8,9.5 M 8,11.5 L 8,12", false),
        [IconKind.Network] = ("M 14.5,8 A 6.5,6.5 0 1 1 1.5,8 A 6.5,6.5 0 1 1 14.5,8 Z M 1.5,8 L 14.5,8 M 8,1.5 C 4.5,5 4.5,11 8,14.5 M 8,1.5 C 11.5,5 11.5,11 8,14.5", false),
        [IconKind.BackToWindow] = ("M 5.5,1.5 L 5.5,5.5 L 1.5,5.5 M 10.5,1.5 L 10.5,5.5 L 14.5,5.5 M 14.5,10.5 L 10.5,10.5 L 10.5,14.5 M 5.5,14.5 L 5.5,10.5 L 1.5,10.5", false),
        [IconKind.FullScreen] = ("M 1.5,5.5 L 1.5,1.5 L 5.5,1.5 M 10.5,1.5 L 14.5,1.5 L 14.5,5.5 M 14.5,10.5 L 14.5,14.5 L 10.5,14.5 M 5.5,14.5 L 1.5,14.5 L 1.5,10.5", false),
    };
}
