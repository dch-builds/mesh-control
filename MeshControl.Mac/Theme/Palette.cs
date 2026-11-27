using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace MeshControl.Mac;

/// <summary>
/// Palette de l'application, reprise à l'identique de la version Windows
/// (UI/Palette.Light.xaml et UI/Palette.Dark.xaml) : mêmes clés, mêmes
/// couleurs. Les styles et les fenêtres n'emploient que ces clés.
///
/// Comme sous Windows, le thème se choisit au démarrage (réglage « Thème
/// sombre ») ; le changer demande un redémarrage.
/// </summary>
public static class Palette
{
    public static bool IsDark { get; private set; }

    /// <summary>Clé, couleur claire, couleur sombre.</summary>
    private static readonly (string Key, string Light, string Dark)[] Brushes =
    [
        ("AccentBrush", "#0A84FF", "#0A84FF"),
        ("AccentSoftBrush", "#E8F2FE", "#16304D"),
        ("AccentSoftHoverBrush", "#D6E8FD", "#1E3F66"),

        ("WindowBackgroundBrush", "#F2F2F7", "#1C1C1E"),
        ("CardBrush", "#FFFFFF", "#2C2C2E"),
        ("SeparatorBrush", "#E5E5EA", "#3A3A3C"),
        ("SurfaceAltBrush", "#FAFAFC", "#262628"),

        ("TextPrimaryBrush", "#1C1C1E", "#F2F2F7"),
        ("TextSecondaryBrush", "#8A8A8E", "#98989D"),
        ("PlaceholderBrush", "#AEAEB2", "#6C6C70"),

        ("OnlineBrush", "#34C759", "#30D158"),
        ("OnlineSoftBrush", "#E4F8E8", "#1D3B24"),
        ("OfflineBrush", "#C7C7CC", "#636366"),
        ("DangerBrush", "#FF3B30", "#FF453A"),
        ("DangerSoftBrush", "#FDE8E7", "#3F1F1D"),

        ("ControlBrush", "#FFFFFF", "#3A3A3C"),
        ("ControlHoverBrush", "#EFEFF4", "#48484A"),
        ("SubtleFillBrush", "#F2F2F7", "#3A3A3C"),
        ("SubtleFillHoverBrush", "#E5E5EA", "#48484A"),
        ("ChipBrush", "#F7F7F9", "#3A3A3C"),
        ("SearchHighlightBrush", "#FFE58F", "#7A6417"),
        ("RowHoverBrush", "#EFEFF4", "#3A3A3C"),

        ("ScrollThumbBrush", "#C7C7CC", "#5A5A5E"),
        ("ScrollThumbHoverBrush", "#A8A8AE", "#7C7C80"),

        ("WindowButtonHoverBrush", "#EBEBF0", "#3A3A3C"),
        ("WindowButtonPressedBrush", "#DEDEE4", "#48484A"),

        ("DropZoneBrush", "#EBF4FF", "#13283F"),
        ("DropZoneSubtleBrush", "#F7F9FC", "#242A33"),
        ("DropZoneStrokeBrush", "#C7D7EE", "#3B4E68"),

        ("InfoTextBrush", "#0A5BC4", "#64B5FF"),
        ("WarningSoftBrush", "#FFF4E5", "#3D2E14"),
        ("WarningTextBrush", "#B25E09", "#FFB340"),

        ("FolderSoftBrush", "#FEF3DC", "#3D3115"),
        ("FolderBrush", "#E89B0C", "#FFB340"),
    ];

    /// <summary>
    /// Installe la palette dans les ressources de l'application, avant toute
    /// fenêtre. Le thème Fluent suit la même variante, pour que les contrôles
    /// laissés à Avalonia (menus, listes déroulantes) restent assortis.
    /// </summary>
    public static void Apply(Application app, bool dark)
    {
        IsDark = dark;
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

        var resources = app.Resources;
        foreach (var (key, light, darkHex) in Brushes)
            resources[key] = new SolidColorBrush(Color.Parse(dark ? darkHex : light));

        resources["AccentColor"] = Color.Parse("#0A84FF");
        // Équivalent du DropShadowEffect de WPF (flou 10, décalage 1, opacité
        // 0,5) : le flou d'Avalonia est plus étalé à valeur égale, d'où un
        // rayon plus court et une teinte un peu plus soutenue.
        resources["CardShadow"] = BoxShadows.Parse(dark ? "0 1 6 0 #66000000" : "0 1 6 0 #2C000000");
        ApplyDensity(app, 1);
    }

    /// <summary>
    /// Densité de la liste des machines : 0 compact, 1 normal, 2 confortable.
    /// Mêmes valeurs que la version Windows (MainWindow.ApplyListDensity).
    /// </summary>
    public static void ApplyDensity(Application app, int density)
    {
        var (padding, icon, nameSize) = density switch
        {
            0 => (new Thickness(8, 4, 8, 4), 26.0, 12.0),
            2 => (new Thickness(12, 13, 12, 13), 42.0, 14.0),
            _ => (new Thickness(10, 9, 10, 9), 36.0, 13.0),
        };
        app.Resources["DeviceRowPadding"] = padding;
        app.Resources["DeviceIconSize"] = icon;
        app.Resources["DeviceNameSize"] = nameSize;
    }

    /// <summary>
    /// Équivalent sombre d'une couleur de l'interface claire — même table que
    /// la version Windows, pour les badges et pastilles calculés dans le code.
    /// </summary>
    public static string Adapt(string hex)
        => IsDark && DarkEquivalents.TryGetValue(hex, out var dark) ? dark : hex;

    private static readonly Dictionary<string, IBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pinceau (partagé) d'une couleur claire, adaptée au thème.</summary>
    public static IBrush Brush(string hex)
    {
        var adapted = Adapt(hex);
        if (!BrushCache.TryGetValue(adapted, out var brush))
            BrushCache[adapted] = brush = new ImmutableSolidColorBrush(Color.Parse(adapted));
        return brush;
    }

    /// <summary>
    /// Pinceau atténué : même couleur, alpha réduit. Remplace Opacity sur les
    /// textes et pictogrammes, qu'Avalonia rendrait sinon dans un calque
    /// intermédiaire, au prix d'un texte flou.
    /// </summary>
    public static IBrush Faded(IBrush brush, double opacity)
    {
        if (opacity >= 1 || brush is not ISolidColorBrush solid) return brush;
        var c = solid.Color;
        return new ImmutableSolidColorBrush(Color.FromArgb((byte)(c.A * opacity * solid.Opacity), c.R, c.G, c.B));
    }

    /// <summary>Pinceau d'une clé de la palette (AccentBrush, TextSecondaryBrush…).</summary>
    public static IBrush Resource(string key) =>
        Application.Current?.Resources.TryGetResource(key, null, out var value) == true && value is IBrush brush
            ? brush
            : Avalonia.Media.Brushes.Transparent;

    private static readonly Dictionary<string, string> DarkEquivalents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["#E4F8E8"] = "#1D3B24",
        ["#EFEFF4"] = "#3A3A3C",
        ["#FFF4E5"] = "#3D2E14",
        ["#FDE8E7"] = "#3F1F1D",
        ["#E8F2FE"] = "#16304D",
        ["#F0F0F2"] = "#3A3A3C",
        ["#E3F5F8"] = "#163640",
        ["#FDF0E6"] = "#3B2A1C",
        ["#FEF3DC"] = "#3D3115",
        ["#ECECFB"] = "#27264A",
        ["#F5EAFB"] = "#33213F",
        ["#F4EEE6"] = "#352D24",
        ["#FFE8ED"] = "#3F1D26",

        ["#248A3D"] = "#4CD964",
        ["#B25E09"] = "#FFB340",
        ["#C0392B"] = "#FF6961",
        ["#8A8A8E"] = "#98989D",
        ["#8E8E93"] = "#98989D",
        ["#636366"] = "#AEAEB2",
        ["#C7C7CC"] = "#636366",
        ["#34C759"] = "#30D158",
        ["#5856D6"] = "#7D7AFF",
        ["#AF52DE"] = "#BF5AF2",
        ["#FF2D55"] = "#FF375F",
        ["#A2845E"] = "#AC8E68",
        ["#E89B0C"] = "#FFB340",
        ["#E5383B"] = "#FF453A",
    };
}
