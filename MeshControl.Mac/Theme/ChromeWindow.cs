using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Templates;
using Avalonia.Platform;
using Avalonia.Styling;

namespace MeshControl.Mac;

/// <summary>
/// Fenêtre de l'application, à barre de titre intégrée (voir <see cref="TitleBar"/>),
/// comme ChromeWindow dans la version Windows : la barre système laisse place
/// à la nôtre, pour que chaque fenêtre forme un tout.
/// </summary>
public class ChromeWindow : Window
{
    /// <summary>
    /// macOS garde ses boutons natifs ; ailleurs, seule la bordure (ombre,
    /// redimensionnement) reste au système, la barre dessine ses boutons.
    ///
    /// En plein écran sous Windows, plus aucune décoration : Avalonia y
    /// dessinerait sinon sa propre bande de titre, blanche, par-dessus
    /// l'image et la barre flottante. Sous macOS, le plein écran natif masque
    /// lui-même la barre de titre.
    /// </summary>
    private static WindowDecorations NormalDecorations =>
        OperatingSystem.IsMacOS() ? WindowDecorations.Full : WindowDecorations.BorderOnly;

    // Les styles et le thème visent Window : sans cela, une sous-classe n'en
    // hériterait pas.
    protected override Type StyleKeyOverride => typeof(Window);

    public ChromeWindow()
    {
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = TitleBar.BarHeight;
        WindowDecorations = NormalDecorations;

        // Aucune décoration dessinée par Avalonia : la barre de titre et la
        // barre flottante du plein écran sont les nôtres. Sans cela, Avalonia
        // révèle sa propre bande de titre, blanche, quand la souris touche le
        // bord haut en plein écran — par-dessus notre barre flottante.
        if (!OperatingSystem.IsMacOS()) WindowDecorationsTheme = NoDrawnDecorations;

        // Rendu du texte : l'ajustement par défaut d'Avalonia est laissé tel
        // quel, c'est le plus proche de ClearType sous WPF (comparé au pixel :
        // sans ajustement, ou avec un ajustement léger, le texte est plus fin).

        // Plein écran : plus de zone de titre réservée (le bord haut doit
        // atteindre l'image, où s'ouvre la barre flottante), ni de décoration
        // sous Windows (voir NormalDecorations).
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            bool full = WindowState == WindowState.FullScreen;
            ExtendClientAreaTitleBarHeightHint = full ? 0 : TitleBar.BarHeight;
            if (!OperatingSystem.IsMacOS())
                WindowDecorations = full ? WindowDecorations.None : NormalDecorations;
        };

        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MeshControl.Mac/Assets/app.png"))); }
        catch { /* icône absente : celle par défaut */ }
    }

    private static readonly ControlTheme NoDrawnDecorations = new(typeof(WindowDrawnDecorations))
    {
        Setters = { new Setter(WindowDrawnDecorations.TemplateProperty, new EmptyDecorationsTemplate()) },
    };

    /// <summary>Gabarit de décorations sans bande de titre, cadre ni barre révélée.</summary>
    private sealed class EmptyDecorationsTemplate : IWindowDrawnDecorationsTemplate
    {
        public TemplateResult<WindowDrawnDecorationsContent> Build() =>
            new(new WindowDrawnDecorationsContent(), new NameScope());

        object? ITemplate.Build() => Build();
    }
}
