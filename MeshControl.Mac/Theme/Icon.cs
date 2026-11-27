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
        [IconKind.Search] = ("M 11.5,6.5 A 5,5 0 1 1 1.5,6.5 A 5,5 0 1 1 11.5,6.5 Z M 10.1,10.1 L 14.5,14.5", false),
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
        [IconKind.FullScreen] = ("M 1.5,5.5 L 1.5,1.5 L 5.5,1.5 M 10.5,1.5 L 14.5,1.5 L 14.5,5.5 M 14.5,10.5 L 14.5,14.5 L 10.5,14.5 M 5.5,14.5 L 1.5,14.5 L 1.5,10.5", false),
    };
}
