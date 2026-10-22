namespace MeshControl.UI;

/// <summary>
/// Entrée du volet des dossiers, dans la vue à deux volets.
///
/// Immuable : le volet est reconstruit quand la composition du parc change, et
/// la sélection est rétablie par <see cref="Key"/>. Une entrée ne se met donc
/// jamais à jour en place, contrairement à <see cref="DeviceRow"/> dont l'état
/// en ligne varie en continu.
/// </summary>
public sealed class FolderRow
{
    /// <summary>Clé spéciale du volet : toutes les machines, sans filtre.</summary>
    public const string AllKey = "\u0000all";

    public FolderRow(string key, string name, string glyph, string glyphColor,
                     int total, int online)
    {
        Key = key;
        Name = name;
        Glyph = glyph;
        GlyphColor = glyphColor;
        Total = total;
        Online = online;
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
    public int Total { get; }
    public int Online { get; }

    /// <summary>
    /// Compteur affiché à droite de l'entrée. On montre le nombre de machines
    /// en ligne plutôt que le total : c'est ce qu'on cherche en ouvrant un
    /// dossier, et le total figure déjà dans l'info-bulle.
    /// </summary>
    public string Counter => Online.ToString();

    public string Tooltip => Total == Online
        ? $"{Total} machine(s), toutes en ligne"
        : $"{Online} en ligne sur {Total}";

    /// <summary>Grisé quand le dossier n'a aucune machine connectée.</summary>
    public double CounterOpacity => Online > 0 ? 1.0 : 0.45;
}
