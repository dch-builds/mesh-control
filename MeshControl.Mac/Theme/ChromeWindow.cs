using Avalonia.Controls;
using Avalonia.Platform;

namespace MeshControl.Mac;

/// <summary>
/// Fenêtre de l'application, à barre de titre intégrée (voir <see cref="TitleBar"/>),
/// comme ChromeWindow dans la version Windows : la barre système laisse place
/// à la nôtre, pour que chaque fenêtre forme un tout.
/// </summary>
public class ChromeWindow : Window
{
    // Les styles et le thème visent Window : sans cela, une sous-classe n'en
    // hériterait pas.
    protected override Type StyleKeyOverride => typeof(Window);

    public ChromeWindow()
    {
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = TitleBar.BarHeight;
        // macOS garde ses boutons natifs ; ailleurs, seule la bordure (ombre,
        // redimensionnement) reste au système, la barre dessine ses boutons.
        WindowDecorations = OperatingSystem.IsMacOS()
            ? WindowDecorations.Full
            : WindowDecorations.BorderOnly;

        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://MeshControl.Mac/Assets/app.png"))); }
        catch { /* icône absente : celle par défaut */ }
    }
}
