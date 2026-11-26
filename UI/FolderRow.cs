using System.ComponentModel;

namespace MeshControl.UI;

/// <summary>
/// Entrée du volet des dossiers, dans la vue à deux volets.
///
/// Le volet est reconstruit quand la composition du parc change, et la
/// sélection est rétablie par <see cref="Key"/>. Entre deux reconstructions,
/// seuls les compteurs bougent : ils se mettent à jour en place (voir
/// <see cref="SetCounts"/>), à chaque événement du serveur, sans toucher à la
/// sélection ni au défilement.
/// </summary>
public sealed class FolderRow : INotifyPropertyChanged
{
    /// <summary>Clé spéciale du volet : toutes les machines, sans filtre.</summary>
    public const string AllKey = "\u0000all";

    /// <summary>Clé spéciale du volet : les machines des dossiers masqués.</summary>
    public const string HiddenKey = "\u0000hidden";

    public FolderRow(string key, string name, string glyph, string glyphColor,
                     int total, int online, bool isPinned = false)
    {
        Key = key;
        Name = name;
        Glyph = glyph;
        GlyphColor = Palette.Adapt(glyphColor);
        IsPinned = isPinned;
        _total = total;
        _matching = online;
    }

    /// <summary>
    /// Identifie l'entrée pour le filtrage et la mémorisation. Vaut le nom du
    /// dossier pour un dossier réel, et une clé réservée pour les entrées
    /// spéciales — les clés réservées commencent par un caractère nul, qu'aucun
    /// nom de dossier ne peut porter.
    /// </summary>
    public string Key { get; }

    public string Name { get; }
    public string Glyph { get; }
    public string GlyphColor { get; }
    public bool IsPinned { get; }

    /// <summary>Dossier réel de MeshCentral, par opposition aux entrées spéciales.</summary>
    public bool IsRealFolder => !Key.StartsWith('\u0000') && Key != MainWindowKeys.Favorites;

    private int _total;
    private int _matching;
    private bool _filtered;
    public int Total => _total;

    /// <summary>
    /// Machines comptées : celles en ligne sans filtre, ou celles qui répondent
    /// aux pastilles actives (Windows, hors ligne…).
    /// </summary>
    public int Matching => _matching;

    /// <summary>Vrai quand des pastilles de filtre sont actives.</summary>
    public bool IsFiltered => _filtered;

    /// <summary>Met à jour les compteurs, en ne notifiant que s'ils ont changé.</summary>
    public void SetCounts(int total, int matching, bool filtered)
    {
        if (total == _total && matching == _matching && filtered == _filtered) return;
        _total = total;
        _matching = matching;
        _filtered = filtered;
        foreach (var name in new[] { nameof(Total), nameof(Matching), nameof(IsFiltered), nameof(Counter),
                                     nameof(Tooltip), nameof(CountColor), nameof(ContentOpacity) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// « comptées / total » : en ligne sur total sans filtre, sinon ce que la
    /// liste montrera en ouvrant le dossier.
    /// </summary>
    public string Counter => $"{Matching} / {Total}";

    public string Tooltip => Total == 0
        ? $"{Name} : aucune machine"
        : IsFiltered
            ? $"{Name} : {Matching} sur {Total} répondent aux filtres"
            : Total == Matching
                ? $"{Name} : {Total} machine(s), toutes en ligne"
                : $"{Name} : {Matching} en ligne sur {Total}";

    /// <summary>
    /// Couleur du nombre compté : vert pour les machines en ligne, bleu pour
    /// celles qui répondent aux filtres, gris quand il n'y en a aucune.
    /// </summary>
    public string CountColor => Palette.Adapt(Matching == 0 ? "#8A8A8E" : IsFiltered ? "#0A84FF" : "#248A3D");

    /// <summary>Dossier sans aucune machine comptée : en retrait, comme les lignes hors ligne.</summary>
    public double ContentOpacity => Matching > 0 ? 1.0 : 0.5;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Clés partagées entre la fenêtre principale et le volet des dossiers.</summary>
internal static class MainWindowKeys
{
    /// <summary>Clé de la section Favoris, qui sert aussi de nom de groupe dans la liste.</summary>
    public const string Favorites = "★  Favoris";
}
