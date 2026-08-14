using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>Ligne d'appareil affichée dans la liste (adaptateur pour le binding WPF).</summary>
public sealed class DeviceRow(MeshNode node, string groupName, bool isFavorite = false)
{
    public MeshNode Node { get; } = node;

    /// <summary>
    /// Nom du groupe affiché. Les favoris et les machines récentes sont placés
    /// dans des sections virtuelles, en tête de liste.
    /// </summary>
    public string GroupName { get; set; } = groupName;

    /// <summary>Dossier réel de la machine (conservé même en section virtuelle).</summary>
    public string RealGroupName { get; } = groupName;

    public bool IsFavorite { get; set; } = isFavorite;

    /// <summary>Étoile affichée à droite : pleine si favori, creuse au survol.</summary>
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string FavoriteColor => IsFavorite ? "#FFB300" : "#C7C7CC";

    /// <summary>Nom du dossier préfixé de son pictogramme, pour l'affichage.</summary>
    public string GroupLabel => $"📁 {RealGroupName}";

    public string Name => Node.Name;
    public bool IsOnline => Node.IsOnline;

    /// <summary>
    /// Pictogramme selon la famille d'OS. On utilise des symboles Unicode
    /// géométriques (et non des emoji récents comme 🪟, mal rendus par les polices
    /// WPF par défaut) : ils s'affichent correctement partout et évitent
    /// d'embarquer des logos sous licence.
    /// </summary>
    public string OsGlyph => Node.OsFamily switch
    {
        OSFamily.Windows => "▤",
        OSFamily.Linux => "◈",
        OSFamily.MacOS => "◉",
        OSFamily.Mobile => "▯",
        _ => "▣",
    };

    /// <summary>Teinte de la pastille d'icône, pour distinguer les OS d'un coup d'œil.</summary>
    public string OsGlyphColor => Node.OsFamily switch
    {
        OSFamily.Windows => "#0A84FF",
        OSFamily.Linux => "#F0883E",
        OSFamily.MacOS => "#8E8E93",
        OSFamily.Mobile => "#30B0C7",
        _ => "#8A8A8E",
    };

    public string StatusBrush => IsOnline ? "#34C759" : "#C7C7CC";
    public string StatusLabel => IsOnline ? "En ligne" : "Hors ligne";
    public string BadgeBackground => IsOnline ? "#E4F8E8" : "#EFEFF4";
    public string BadgeForeground => IsOnline ? "#248A3D" : "#8A8A8E";

    public string Subtitle =>
        string.IsNullOrEmpty(Node.OsDescription) ? "Système non renseigné" : Node.OsDescription!;
}
