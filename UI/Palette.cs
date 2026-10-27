using System.Windows;

namespace MeshControl.UI;

/// <summary>
/// Thème clair ou sombre de l'application.
///
/// Le thème se choisit au démarrage, avant toute fenêtre : les styles
/// référencent la palette en StaticResource, résolue une fois pour toutes au
/// chargement. Changer de thème demande donc un redémarrage — un choix assumé,
/// pour un réglage qu'on ne touche guère, plutôt que de rendre dynamique chaque
/// couleur de l'interface.
/// </summary>
public static class Palette
{
    public static bool IsDark { get; private set; }

    /// <summary>
    /// Charge la palette puis le thème dans les ressources de l'application.
    /// L'ordre compte : les styles de Theme.xaml trouvent leurs couleurs dans
    /// la palette déjà en place.
    /// </summary>
    public static void Apply(Application app, bool dark)
    {
        IsDark = dark;
        var resources = app.Resources.MergedDictionaries;
        resources.Add(Load(dark ? "UI/Palette.Dark.xaml" : "UI/Palette.Light.xaml"));
        resources.Add(Load("UI/Theme.xaml"));
    }

    private static ResourceDictionary Load(string path)
        => new() { Source = new Uri($"/Mesh Control;component/{path}", UriKind.Relative) };

    /// <summary>
    /// Équivalent sombre d'une couleur de l'interface claire.
    ///
    /// Les badges, pastilles et icônes calculent leurs couleurs dans le code,
    /// en teintes claires. Plutôt que de dédoubler chacune de ces valeurs, on
    /// les traduit ici : les fonds pâles deviennent des fonds sombres teintés,
    /// les textes foncés s'éclaircissent pour rester lisibles. Une couleur
    /// absente de la table vaut pour les deux thèmes.
    /// </summary>
    public static string Adapt(string hex)
        => IsDark && DarkEquivalents.TryGetValue(hex, out var dark) ? dark : hex;

    private static readonly Dictionary<string, string> DarkEquivalents = new(StringComparer.OrdinalIgnoreCase)
    {
        // Fonds pâles de badges et de pastilles
        ["#E4F8E8"] = "#1D3B24",   // vert : en ligne, réussi
        ["#EFEFF4"] = "#3A3A3C",   // gris : neutre, hors ligne
        ["#FFF4E5"] = "#3D2E14",   // orange : avertissement
        ["#FDE8E7"] = "#3F1F1D",   // rouge : échec
        ["#E8F2FE"] = "#16304D",   // bleu : en cours, Windows
        ["#F0F0F2"] = "#3A3A3C",   // gris de pastille
        ["#E3F5F8"] = "#163640",   // cyan
        ["#FDF0E6"] = "#3B2A1C",   // orange de pastille (Linux)
        ["#FEF3DC"] = "#3D3115",   // dossier
        ["#ECECFB"] = "#27264A",   // indigo
        ["#F5EAFB"] = "#33213F",   // violet
        ["#F4EEE6"] = "#352D24",   // brun
        ["#FFE8ED"] = "#3F1D26",   // rose

        // Textes et pictogrammes foncés
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
