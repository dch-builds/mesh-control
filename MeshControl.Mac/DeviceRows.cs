using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Ligne d'appareil — portage de UI/DeviceRow.cs (version Windows) : mêmes
/// libellés, mêmes couleurs, même logique de présence. Seuls les types
/// d'affichage changent (pinceaux Avalonia, booléens de visibilité).
///
/// Mise à jour en place (INotifyPropertyChanged) : la liste n'est pas
/// reconstruite à chaque événement du serveur, le défilement est préservé.
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
    /// <summary>Section affichée : son dossier, ou Favoris / Récentes.</summary>
    public string GroupName { get => _groupName; set => Set(ref _groupName, value); }

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
    public bool IsOffline => !Node.IsOnline;

    // État affiché lors du dernier rafraîchissement (Node est modifié en place).
    private bool _shownOnline;
    private string _shownName = "";
    private string? _shownOsDescription;
    private DateTime? _shownLastConnect;
    private string? _shownIp;
    private string _shownSessions = "";

    public void CaptureState()
    {
        _shownOnline = Node.IsOnline;
        _shownName = Node.Name;
        _shownOsDescription = Node.OsDescription;
        _shownLastConnect = Node.LastConnect;
        _shownIp = Node.Ip;
        _shownSessions = SessionsSignature();
    }

    private string SessionsSignature() => string.Join("|", Node.Sessions.Select(s => $"{s.User}:{s.Kind}"));

    public bool OnlineStateChanged => _shownOnline != Node.IsOnline;
    public bool SessionsChanged => _shownSessions != SessionsSignature();
    public bool NameChanged => !string.Equals(_shownName, Node.Name, StringComparison.Ordinal);

    /// <summary>Rafraîchit les propriétés dérivées qui ont réellement changé.</summary>
    public void RefreshFromNode()
    {
        if (OnlineStateChanged)
        {
            Notify(nameof(IsOnline));
            Notify(nameof(IsOffline));
            Notify(nameof(RemoteActionsVisible));
            Notify(nameof(WakeActionVisible));
            Notify(nameof(IconOpacity));
            Notify(nameof(NameBrush));
            Notify(nameof(OsGlyphColor));
            Notify(nameof(OsGlyphBackground));
        }
        if (OnlineStateChanged || _shownLastConnect != Node.LastConnect || SessionsChanged)
        {
            Notify(nameof(StatusKind));
            Notify(nameof(IsStatusOnline));
            Notify(nameof(IsStatusSession));
            Notify(nameof(IsStatusInactive));
            Notify(nameof(StatusTooltip));
            Notify(nameof(PresenceText));
            Notify(nameof(PresenceShort));
            Notify(nameof(PresenceColor));
        }
        if (NameChanged) Notify(nameof(Name));
        if (!string.Equals(_shownOsDescription, Node.OsDescription, StringComparison.Ordinal))
        {
            Notify(nameof(Subtitle));
            Notify(nameof(OsDisplay));
            Notify(nameof(OsGlyph));
            Notify(nameof(OsGlyphColor));
            Notify(nameof(OsGlyphBackground));
        }
        if (!string.Equals(_shownIp, Node.Ip, StringComparison.Ordinal))
        {
            Notify(nameof(Subtitle));
            Notify(nameof(IpText));
        }
        if (OnlineStateChanged || SessionsChanged)
        {
            Notify(nameof(SessionLabel));
            Notify(nameof(SessionTooltip));
            Notify(nameof(SessionVisible));
            Notify(nameof(SessionInitials));
            Notify(nameof(SessionAvatarBackground));
            Notify(nameof(SessionAvatarForeground));
        }
        CaptureState();
    }

    // MARK: - Favori et système

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public IBrush FavoriteColor => Palette.Brush(IsFavorite ? "#FFB300" : "#C7C7CC");

    /// <summary>Symboles géométriques plutôt que logos (licence) ou emoji récents.</summary>
    public string OsGlyph => Node.OsFamily switch
    {
        OSFamily.Windows => "▤",
        OSFamily.Linux => "◈",
        OSFamily.MacOS => "◉",
        OSFamily.Mobile => "▯",
        _ => "▣",
    };

    public IBrush OsGlyphColor => Palette.Faded(Palette.Brush(Node.OsFamily switch
    {
        OSFamily.Windows => "#0A84FF",
        OSFamily.Linux => "#F0883E",
        OSFamily.MacOS => "#8E8E93",
        OSFamily.Mobile => "#30B0C7",
        _ => "#8A8A8E",
    }), IconOpacity);

    public IBrush OsGlyphBackground => Palette.Faded(Palette.Brush(Node.OsFamily switch
    {
        OSFamily.Windows => "#E8F2FE",
        OSFamily.Linux => "#FDF0E6",
        OSFamily.MacOS => "#F0F0F2",
        OSFamily.Mobile => "#E3F5F8",
        _ => "#F0F0F2",
    }), IconOpacity);

    /// <summary>
    /// Hors ligne, la pastille passe en retrait (couleurs à moitié transparentes,
    /// voir Palette.Faded) et le nom en gris.
    /// </summary>
    public double IconOpacity => IsOnline ? 1.0 : 0.5;
    public IBrush NameBrush => Palette.Resource(IsOnline ? "TextPrimaryBrush" : "TextSecondaryBrush");

    public string Subtitle
    {
        get
        {
            var system = string.IsNullOrEmpty(Node.OsDescription) ? "Système non renseigné" : Node.OsDescription!;
            return string.IsNullOrEmpty(Node.Ip) ? system : $"{system} · {Node.Ip}";
        }
    }

    // MARK: - Utilisateurs MeshCentral connectés

    /// <summary>Compte connecté avec cette application : ses sessions s'affichent « vous ».</summary>
    public static string? CurrentUserName { get; set; }

    private List<(string Name, List<string> Kinds)> SessionUsers()
        => Node.Sessions
            .GroupBy(s => s.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: DisplayName(g.Key), Kinds: g.Select(s => s.KindLabel).Distinct().ToList()))
            .OrderBy(u => u.Name == "vous")
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string DisplayName(string user)
        => CurrentUserName is { } me && string.Equals(user, me, StringComparison.OrdinalIgnoreCase)
            ? "vous" : user;

    public string SessionLabel
    {
        get
        {
            var users = SessionUsers();
            if (users.Count == 0) return "";
            return users.Count > 1 ? $"{users[0].Name} +{users.Count - 1}" : users[0].Name;
        }
    }

    public string SessionInitials
    {
        get
        {
            var users = SessionUsers();
            if (users.Count == 0) return "";
            var initials = InitialsOf(users[0].Name == "vous" ? CurrentUserName ?? "vous" : users[0].Name);
            return users.Count > 1 ? $"{initials}+{users.Count - 1}" : initials;
        }
    }

    private static string InitialsOf(string name)
    {
        var parts = name.Split(['.', '_', '-', ' ', '@'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2) return $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}";
        var single = parts.Length == 1 ? parts[0] : name;
        return single.Length >= 2 ? single[..2].ToUpperInvariant() : single.ToUpperInvariant();
    }

    private bool SessionIsMine => SessionUsers() is { Count: > 0 } users && users[0].Name == "vous" && users.Count == 1;

    public IBrush SessionAvatarBackground => Palette.Brush(SessionIsMine ? "#E8F2FE" : "#FFF4E5");
    public IBrush SessionAvatarForeground => Palette.Brush(SessionIsMine ? "#0A84FF" : "#B25E09");

    public string? SessionTooltip
    {
        get
        {
            var users = SessionUsers();
            return users.Count == 0
                ? null
                : "Connecté via MeshCentral :\n"
                  + string.Join("\n", users.Select(u => $"{u.Name} — {string.Join(", ", u.Kinds)}"));
        }
    }

    public bool SessionVisible => IsOnline && Node.Sessions.Count > 0;

    // MARK: - Actions au survol

    public bool RemoteActionsVisible => Node is { IsOnline: true, HasAgent: true };
    public bool WakeActionVisible => !IsOnline;

    // MARK: - État et présence

    private static readonly TimeSpan InactiveAfter = TimeSpan.FromDays(30);

    public string StatusKind => IsOnline
        ? (Node.Sessions.Count > 0 ? "Session" : "Online")
        : Node.LastConnect is { } seen && DateTime.Now - seen > InactiveAfter ? "Inactive" : "Offline";

    public bool IsStatusOnline => StatusKind == "Online";
    public bool IsStatusSession => StatusKind == "Session";
    public bool IsStatusInactive => StatusKind == "Inactive";

    public string StatusTooltip => StatusKind switch
    {
        "Session" => "En ligne, un utilisateur MeshCentral est connecté",
        "Online" => "En ligne",
        "Inactive" => $"Inactive : hors ligne {Node.LastSeenLabel}",
        _ => $"Hors ligne, vue {Node.LastSeenLabel}",
    };

    public string PresenceText => IsOnline ? "en ligne" : Node.LastSeenLabel;

    public string PresenceShort
    {
        get
        {
            if (IsOnline) return "en ligne";
            if (Node.LastConnect is not { } seen) return "—";
            var span = DateTime.Now - seen;
            if (span.TotalHours < 1) return $"{Math.Max(1, (int)span.TotalMinutes)} min";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours} h";
            if (span.TotalDays < 31) return $"{(int)span.TotalDays} j";
            if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} mois";
            return $"{(int)(span.TotalDays / 365)} an{((int)(span.TotalDays / 365) > 1 ? "s" : "")}";
        }
    }

    /// <summary>La présence vieillit en couleur : vert, gris, orange au-delà de 30 j, pâle au-delà de 6 mois.</summary>
    public IBrush PresenceColor
    {
        get
        {
            if (IsOnline) return Palette.Brush("#248A3D");
            if (Node.LastConnect is not { } seen) return Palette.Brush("#8A8A8E");
            var days = (DateTime.Now - seen).TotalDays;
            return Palette.Brush(days > 180 ? "#C7C7CC" : days > InactiveAfter.TotalDays ? "#B25E09" : "#8A8A8E");
        }
    }

    public string OsDisplay
    {
        get
        {
            var os = Node.OsDescription;
            if (string.IsNullOrEmpty(os)) return "—";
            return os.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) ? os[10..] : os;
        }
    }

    public string IpText => Node.Ip ?? "";

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

/// <summary>En-tête de dossier dans la liste (portage de UI/GroupHeaderRow.cs).</summary>
public sealed class GroupHeaderRow(string name) : INotifyPropertyChanged
{
    public string Name { get; } = name;

    private int _count;
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            Notify(nameof(Count));
            Notify(nameof(CountLabel));
        }
    }

    public string CountLabel => $" ({Count})";

    private bool _isCollapsed;
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (_isCollapsed == value) return;
            _isCollapsed = value;
            Notify(nameof(IsCollapsed));
            Notify(nameof(Chevron));
        }
    }

    public IconKind Chevron => IsCollapsed ? IconKind.ChevronRight : IconKind.ChevronDown;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Entrée du volet des dossiers (portage de UI/FolderRow.cs).</summary>
public sealed class FolderRow : INotifyPropertyChanged
{
    public const string AllKey = "\u0000all";
    public const string HiddenKey = "\u0000hidden";
    public const string RecentKey = "\u0000recent";
    public const string SessionKey = "\u0000session";
    public const string FavoritesKey = "★  Favoris";

    public static FolderRow Section(string title)
        => new("\u0000section:" + title, title, IconKind.None, "#8A8A8E", 0, 0) { IsSection = true };

    public FolderRow(string key, string name, IconKind glyph, string glyphColor,
                     int total, int online, bool isPinned = false)
    {
        Key = key;
        Name = name;
        Glyph = glyph;
        _glyphColor = Palette.Brush(glyphColor);
        IsPinned = isPinned;
        _total = total;
        _matching = online;
    }

    public bool IsSection { get; private init; }
    public bool IsEntry => !IsSection;
    public string Key { get; }
    public string Name { get; }
    public IconKind Glyph { get; }
    private readonly IBrush _glyphColor;
    public bool IsPinned { get; }

    public bool IsRealFolder => !Key.StartsWith('\u0000') && Key != FavoritesKey;

    private int _total;
    private int _matching;
    private bool _filtered;
    public int Total => _total;
    public int Matching => _matching;
    public bool IsFiltered => _filtered;

    public void SetCounts(int total, int matching, bool filtered)
    {
        if (total == _total && matching == _matching && filtered == _filtered) return;
        _total = total;
        _matching = matching;
        _filtered = filtered;
        foreach (var name in new[] { nameof(Total), nameof(Matching), nameof(TotalLabel), nameof(IsFiltered),
                                     nameof(Tooltip), nameof(CountColor), nameof(ContentOpacity),
                                     nameof(GlyphBrush), nameof(NameBrush), nameof(TotalBrush), nameof(PillBrush) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public string TotalLabel => $" / {Total}";

    public string Tooltip => Total == 0
        ? $"{Name} : aucune machine"
        : IsFiltered
            ? $"{Name} : {Matching} sur {Total} répondent aux filtres"
            : Total == Matching
                ? $"{Name} : {Total} machine(s), toutes en ligne"
                : $"{Name} : {Matching} en ligne sur {Total}";

    /// <summary>Dossier sans aucune machine comptée : en retrait.</summary>
    public double ContentOpacity => IsSection || Matching > 0 ? 1.0 : 0.5;

    // Retrait obtenu par la couleur et non par Opacity, qui rendrait le texte
    // dans un calque intermédiaire, flou (voir Palette.Faded).
    public IBrush CountColor => Palette.Faded(
        Palette.Brush(Matching == 0 ? "#8A8A8E" : IsFiltered ? "#0A84FF" : "#248A3D"), ContentOpacity);
    public IBrush GlyphBrush => Palette.Faded(_glyphColor, ContentOpacity);
    public IBrush NameBrush => Palette.Faded(
        Palette.Resource(IsSection ? "TextSecondaryBrush" : "TextPrimaryBrush"), ContentOpacity);
    public IBrush TotalBrush => Palette.Faded(Palette.Resource("TextSecondaryBrush"), ContentOpacity);
    public IBrush PillBrush => Palette.Faded(Palette.Resource("SubtleFillBrush"), ContentOpacity);

    public event PropertyChangedEventHandler? PropertyChanged;
}
