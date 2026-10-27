using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
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
    private DateTime? _shownLastConnect;
    private string? _shownIp;
    private string _shownSessions = "";

    /// <summary>Mémorise l'état courant comme étant celui affiché.</summary>
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
            // Le libellé dépend aussi de l'état : « Connecté » quand en ligne.
            Notify(nameof(LastSeenLabel));
            // Les actions proposées au survol aussi : se connecter ou réveiller.
            Notify(nameof(RemoteActionsVisibility));
            Notify(nameof(WakeActionVisibility));
        }
        if (_shownLastConnect != Node.LastConnect) Notify(nameof(LastSeenLabel));
        if (NameChanged) Notify(nameof(Name));
        if (!string.Equals(_shownOsDescription, Node.OsDescription, StringComparison.Ordinal))
        {
            Notify(nameof(Subtitle));
            Notify(nameof(OsGlyph));
            Notify(nameof(OsGlyphColor));
            Notify(nameof(OsGlyphBackground));
        }
        if (!string.Equals(_shownIp, Node.Ip, StringComparison.Ordinal)) Notify(nameof(Subtitle));
        if (OnlineStateChanged || _shownSessions != SessionsSignature())
        {
            Notify(nameof(SessionLabel));
            Notify(nameof(SessionTooltip));
            Notify(nameof(SessionVisibility));
        }

        CaptureState();
    }

    /// <summary>Étoile affichée à droite : pleine si favori, creuse sinon.</summary>
    public string FavoriteGlyph => IsFavorite ? "★" : "☆";
    public string FavoriteColor => Palette.Adapt(IsFavorite ? "#FFB300" : "#C7C7CC");

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
    public string OsGlyphColor => Palette.Adapt(Node.OsFamily switch
    {
        OSFamily.Windows => "#0A84FF",
        OSFamily.Linux => "#F0883E",
        OSFamily.MacOS => "#8E8E93",
        OSFamily.Mobile => "#30B0C7",
        _ => "#8A8A8E",
    });

    /// <summary>
    /// Fond de la pastille, dans la même teinte que le pictogramme mais très
    /// délavée. Un gris identique pour tous obligeait à lire le pictogramme pour
    /// distinguer les familles ; la couleur du fond, elle, se saisit sans
    /// lecture, et reste assez pâle pour ne pas disputer l'attention au nom de
    /// la machine.
    /// </summary>
    public string OsGlyphBackground => Palette.Adapt(Node.OsFamily switch
    {
        OSFamily.Windows => "#E8F2FE",
        OSFamily.Linux => "#FDF0E6",
        OSFamily.MacOS => "#F0F0F2",
        OSFamily.Mobile => "#E3F5F8",
        _ => "#F0F0F2",
    });

    public string StatusBrush => Palette.Adapt(IsOnline ? "#34C759" : "#C7C7CC");
    public string StatusLabel => IsOnline ? "En ligne" : "Hors ligne";

    /// <summary>
    /// Dernière présence, en durée relative. Vide pour une machine en ligne :
    /// le badge « En ligne » le dit déjà, et le répéter alourdirait la ligne.
    /// </summary>
    public string LastSeenLabel => IsOnline ? "" : Node.LastSeenLabel;
    public string BadgeBackground => Palette.Adapt(IsOnline ? "#E4F8E8" : "#EFEFF4");
    public string BadgeForeground => Palette.Adapt(IsOnline ? "#248A3D" : "#8A8A8E");

    /// <summary>
    /// Système, puis adresse IP : celle-ci sert à reconnaître un poste d'un
    /// coup d'œil, ou à le joindre par d'autres moyens.
    /// </summary>
    public string Subtitle
    {
        get
        {
            var system = string.IsNullOrEmpty(Node.OsDescription) ? "Système non renseigné" : Node.OsDescription!;
            return string.IsNullOrEmpty(Node.Ip) ? system : $"{system} · {Node.Ip}";
        }
    }

    // MARK: - Utilisateurs MeshCentral connectés

    /// <summary>
    /// Nom de l'utilisateur connecté à MeshCentral avec cette application.
    /// Ses propres sessions s'affichent « vous » : on reconnaît d'un coup d'œil
    /// une machine déjà ouverte, et on distingue celles où un collègue travaille.
    /// </summary>
    public static string? CurrentUserName { get; set; }

    /// <summary>Utilisateurs ayant une session ouverte, chacun avec ses types de session.</summary>
    private List<(string Name, List<string> Kinds)> SessionUsers()
        => Node.Sessions
            .GroupBy(s => s.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: DisplayName(g.Key), Kinds: g.Select(s => s.KindLabel).Distinct().ToList()))
            // Les collègues avant soi : c'est leur présence qu'il importe de voir.
            .OrderBy(u => u.Name == "vous")
            .ThenBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string DisplayName(string user)
        => CurrentUserName is { } me && string.Equals(user, me, StringComparison.OrdinalIgnoreCase)
            ? "vous" : user;

    /// <summary>
    /// Premier utilisateur MeshCentral connecté, suivi du nombre des autres.
    /// On le regarde avant de prendre la main : un collègue est-il déjà dessus ?
    /// </summary>
    public string SessionLabel
    {
        get
        {
            var users = SessionUsers();
            if (users.Count == 0) return "";
            return users.Count > 1 ? $"{users[0].Name} +{users.Count - 1}" : users[0].Name;
        }
    }

    public string SessionTooltip
    {
        get
        {
            var users = SessionUsers();
            return users.Count == 0
                ? ""
                : "Connecté via MeshCentral :\n"
                  + string.Join("\n", users.Select(u => $"{u.Name} — {string.Join(", ", u.Kinds)}"));
        }
    }

    /// <summary>
    /// Seulement en ligne : les sessions d'une machine éteinte sont forcément
    /// refermées, même si le serveur n'en a pas encore reçu la fin.
    /// </summary>
    public Visibility SessionVisibility =>
        IsOnline && Node.Sessions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // MARK: - Actions au survol

    /// <summary>Bureau, terminal et fichiers passent par l'agent : machine joignable requise.</summary>
    public Visibility RemoteActionsVisibility =>
        Node is { IsOnline: true, HasAgent: true } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Une machine éteinte n'a qu'une action utile : la réveiller.</summary>
    public Visibility WakeActionVisibility =>
        IsOnline ? Visibility.Collapsed : Visibility.Visible;

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
