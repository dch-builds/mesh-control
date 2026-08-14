using System.ComponentModel;
using System.Runtime.CompilerServices;
using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>
/// Ligne d'appareil affichée dans la liste.
///
/// Implémente INotifyPropertyChanged : la liste étant mise à jour en place
/// (et non reconstruite) à chaque événement serveur, seules les valeurs
/// réellement modifiées sont redessinées. Sans cela, la position de défilement
/// et l'état des dossiers seraient perdus en continu.
/// </summary>
public sealed class DeviceRow : INotifyPropertyChanged
{
    public DeviceRow(MeshNode node, string groupName, bool isFavorite = false)
    {
        Node = node;
        RealGroupName = groupName;
        _groupName = groupName;
        _isFavorite = isFavorite;
        CaptureState();
    }

    public MeshNode Node { get; }

    /// <summary>Dossier réel de la machine (conservé même en section virtuelle).</summary>
    public string RealGroupName { get; }

    private string _groupName;
    /// <summary>
    /// Nom du groupe affiché. Les favoris et les machines récentes sont placés
    /// dans des sections virtuelles, en tête de liste.
    /// </summary>
    public string GroupName
    {
        get => _groupName;
        set => Set(ref _groupName, value);
    }

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (Set(ref _isFavorite, value))
            {
                Notify(nameof(FavoriteGlyph));
                Notify(nameof(FavoriteColor));
            }
        }
    }

    public string Name => Node.Name;
    public bool IsOnline => Node.IsOnline;

    // État affiché lors du dernier rafraîchissement. Indispensable : Node est
    // modifié en place par la connexion, donc comparer Node à lui-même ne
    // révélerait jamais de changement.
    private bool _shownOnline;
    private string _shownName = "";
    private string? _shownOsDescription;

    /// <summary>Mémorise l'état courant comme étant celui affiché.</summary>
    public void CaptureState()
    {
        _shownOnline = Node.IsOnline;
        _shownName = Node.Name;
        _shownOsDescription = Node.OsDescription;
    }

    /// <summary>Vrai si l'état en ligne a changé depuis le dernier affichage.</summary>
    public bool OnlineStateChanged => _shownOnline != Node.IsOnline;

    /// <summary>Vrai si le nom a changé (le tri en dépend).</summary>
    public bool NameChanged => !string.Equals(_shownName, Node.Name, StringComparison.Ordinal);

    /// <summary>
    /// Rafraîchit les propriétés dérivées de l'état du nœud, si elles ont
    /// réellement changé. Notifier inutilement ferait travailler WPF pour rien
    /// à chaque événement serveur.
    /// </summary>
    public void RefreshFromNode()
    {
        if (OnlineStateChanged)
        {
            Notify(nameof(IsOnline));
            Notify(nameof(StatusBrush));
            Notify(nameof(StatusLabel));
            Notify(nameof(BadgeBackground));
            Notify(nameof(BadgeForeground));
        }
        if (NameChanged) Notify(nameof(Name));
        if (!string.Equals(_shownOsDescription, Node.OsDescription, StringComparison.Ordinal))
        {
            Notify(nameof(Subtitle));
            Notify(nameof(OsGlyph));
            Notify(nameof(OsGlyphColor));
        }

        CaptureState();
    }

    /// <summary>Étoile affichée à droite : pleine si favori, creuse sinon.</summary>
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string FavoriteColor => IsFavorite ? "#FFB300" : "#C7C7CC";

    /// <summary>Nom du dossier préfixé de son pictogramme, pour l'affichage.</summary>
    public string GroupLabel => $"📁 {RealGroupName}";

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

    // MARK: - Notification de changement

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
}
