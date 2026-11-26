using System.Text.Json;

namespace MeshControl.Core;

/// <summary>
/// Numéros d'usage relais (paramètre p=). SSH a son propre endpoint.
/// Valeurs reprises du menu terminal de l'interface web MeshCentral
/// (termShellContextMenu / termShellContextMenuLinux).
/// </summary>
public enum RelayProtocol
{
    /// <summary>Shell administrateur : SYSTEM sous Windows, root sous Linux.</summary>
    Terminal = 1,
    Desktop = 2,
    Files = 5,
    /// <summary>PowerShell avec les droits de l'agent (SYSTEM).</summary>
    PowerShell = 6,
    /// <summary>Shell dans le contexte de l'utilisateur connecté.</summary>
    UserShell = 8,
    /// <summary>PowerShell dans le contexte de l'utilisateur connecté.</summary>
    UserPowerShell = 9,
    /// <summary>
    /// Login Shell : l'agent lance /bin/login, qui demande identifiant et mot de
    /// passe directement dans la console (Linux uniquement).
    /// </summary>
    LoginShell = 100,
}

public enum PowerActionType
{
    PowerOff = 2,
    Reboot = 3,
    Sleep = 4,
}

public enum OSFamily { Windows, Linux, MacOS, Mobile, Other }

/// <summary>
/// Masque de bits des protocoles autorisés par un partage. Attention : ces
/// valeurs diffèrent de <see cref="RelayProtocol"/>, utilisé pour les tunnels.
/// </summary>
[Flags]
public enum SharedFeatures
{
    Terminal = 1,
    Desktop = 2,
    Files = 4,
    Http = 8,
    Https = 16,
}

/// <summary>Un lien de partage public vers un appareil.</summary>
public sealed class DeviceShare
{
    public string PublicId { get; init; } = "";
    public string Url { get; init; } = "";
    public string GuestName { get; init; } = "";
    public int Protocols { get; init; }
    public DateTime? ExpireTime { get; init; }

    /// <summary>Description lisible des accès accordés.</summary>
    public string FeaturesText
    {
        get
        {
            var parts = new List<string>();
            if ((Protocols & (int)SharedFeatures.Desktop) != 0) parts.Add("Bureau");
            if ((Protocols & (int)SharedFeatures.Terminal) != 0) parts.Add("Terminal");
            if ((Protocols & (int)SharedFeatures.Files) != 0) parts.Add("Fichiers");
            return parts.Count > 0 ? string.Join(" + ", parts) : "—";
        }
    }

    public string ExpiryText => ExpireTime is { } e
        ? $"expire le {e:dd/MM/yyyy à HH:mm}"
        : "sans échéance connue";
}

/// <summary>Un groupe d'appareils ("mesh").</summary>
public sealed class Mesh
{
    public string Id { get; init; } = "";       // "mesh/<domain>/<hash>"
    public string Name { get; set; } = "";
    public string? Desc { get; set; }
    public int MType { get; set; }               // 1=AMT, 2=agent, 3=local, 4=IP-KVM

    public static Mesh? From(JsonElement json)
    {
        if (!json.TryGetProperty("_id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return null;
        if (!json.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            return null;
        // Groupe supprimé : MeshCentral le garde, marqué « deleted », et
        // l'interface web l'ignore. Ses machines ne doivent pas reparaître ici.
        if (json.TryGetProperty("deleted", out var deleted)
            && deleted.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined))
            return null;
        return new Mesh
        {
            Id = idEl.GetString()!,
            Name = nameEl.GetString()!,
            Desc = json.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
            MType = json.TryGetProperty("mtype", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 2,
        };
    }
}

/// <summary>Une session MeshCentral ouverte sur une machine.</summary>
/// <param name="User">
/// Utilisateur tel que l'agent le rapporte : son nom, ou son identifiant
/// complet (« user/domaine/nom ») selon la version du serveur.
/// </param>
/// <param name="Kind">kvm, terminal ou files.</param>
public sealed record RemoteSession(string User, string Kind)
{
    /// <summary>Nom affichable : le dernier segment d'un identifiant complet.</summary>
    public string UserName => User.Contains('/') ? User[(User.LastIndexOf('/') + 1)..] : User;

    public string KindLabel => Kind switch
    {
        "kvm" => "bureau à distance",
        "terminal" => "terminal",
        "files" => "fichiers",
        _ => Kind,
    };
}

/// <summary>Un appareil géré ("node").</summary>
public sealed class MeshNode
{
    public string Id { get; init; } = "";       // "node/<domain>/<hash>"
    public string MeshId { get; set; } = "";
    public string Name { get; set; } = "Unknown";
    public string? RealName { get; set; }
    public string? Host { get; set; }
    public int Icon { get; set; } = 1;
    public int Conn { get; set; }                // bitmask : 1 agent, 2 CIRA, 4 AMT, 8 relay, 16 MQTT
    public int Pwr { get; set; }
    public string? OsDescription { get; set; }
    public string? Ip { get; set; }
    public int MType { get; set; } = 3;
    public string? Desc { get; set; }
    public int? SshPort { get; set; }
    public int? RdpPort { get; set; }

    /// <summary>
    /// Sessions MeshCentral ouvertes sur la machine — bureau, terminal,
    /// fichiers… — avec l'utilisateur qui les tient. L'agent les rapporte au
    /// serveur, qui les joint à l'inventaire puis diffuse chaque changement.
    /// </summary>
    public IReadOnlyList<RemoteSession> Sessions { get; set; } = [];

    /// <summary>
    /// Types de session tenus par un utilisateur MeshCentral. L'objet
    /// « sessions » porte aussi d'autres catégories — l'état de MeshCentral
    /// Assistant (« app »), la messagerie… — dont les clés ne sont pas des
    /// utilisateurs : les lire comme tels affichait « state » sur les postes
    /// équipés de l'Assistant.
    /// </summary>
    private static readonly HashSet<string> UserSessionKinds = ["kvm", "terminal", "files"];

    /// <summary>
    /// Lit l'objet « sessions » : { kvm: { "&lt;utilisateur&gt;": nombre }, terminal: {…}, … }.
    /// </summary>
    public static List<RemoteSession> ParseSessions(JsonElement sessions)
    {
        var list = new List<RemoteSession>();
        if (sessions.ValueKind != JsonValueKind.Object) return list;

        foreach (var kind in sessions.EnumerateObject())
        {
            if (!UserSessionKinds.Contains(kind.Name) || kind.Value.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var user in kind.Value.EnumerateObject())
            {
                // Une session d'utilisateur se compte : toute autre valeur
                // n'en est pas une, et zéro ne laisse que la trace d'une
                // session refermée.
                if (user.Value.ValueKind != JsonValueKind.Number || user.Value.GetInt32() <= 0) continue;
                list.Add(new RemoteSession(user.Name, kind.Name));
            }
        }
        return list;
    }

    /// <summary>
    /// Date de dernière connexion. Absente de la réponse « nodes » : le serveur
    /// la conserve dans une collection séparée, interrogée par l'action
    /// « lastconnects » et fusionnée ici — comme le fait le client web.
    /// Null pour une machine jamais vue depuis l'installation du serveur.
    /// </summary>
    public DateTime? LastConnect { get; set; }

    /// <summary>
    /// Libellé de dernière présence. Une machine en ligne l'est « maintenant » :
    /// afficher une date passée dans ce cas serait trompeur.
    /// </summary>
    public string LastSeenLabel
    {
        get
        {
            if (IsOnline) return "Connecté";
            if (LastConnect is not { } seen) return "—";

            var span = DateTime.Now - seen;
            if (span.TotalMinutes < 1) return "à l'instant";
            if (span.TotalMinutes < 60) return $"il y a {(int)span.TotalMinutes} min";
            if (span.TotalHours < 24) return $"il y a {(int)span.TotalHours} h";
            if (span.TotalDays < 31) return $"il y a {(int)span.TotalDays} j";
            if (span.TotalDays < 365) return $"il y a {(int)(span.TotalDays / 30)} mois";
            return $"il y a {(int)(span.TotalDays / 365)} an(s)";
        }
    }

    public bool IsOnline => (Conn & 1) != 0 || (Conn & 8) != 0;
    public bool HasAgent => (Conn & 1) != 0;

    private static int? IntOrNull(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static string? StrOrNull(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;


    public static MeshNode? From(JsonElement json, string meshId)
    {
        if (!json.TryGetProperty("_id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return null;
        var node = new MeshNode
        {
            Id = idEl.GetString()!,
            MeshId = meshId,
            Name = StrOrNull(json, "name") ?? "Unknown",
            RealName = StrOrNull(json, "rname"),
            Host = StrOrNull(json, "host"),
            Icon = IntOrNull(json, "icon") ?? 1,
            Conn = IntOrNull(json, "conn") ?? 0,
            Pwr = IntOrNull(json, "pwr") ?? 0,
            OsDescription = StrOrNull(json, "osdesc"),
            Ip = StrOrNull(json, "ip"),
            MType = IntOrNull(json, "mtype") ?? 3,
            Desc = StrOrNull(json, "desc"),
            SshPort = IntOrNull(json, "sshport"),
            RdpPort = IntOrNull(json, "rdpport"),
            Sessions = json.TryGetProperty("sessions", out var sessions) ? ParseSessions(sessions) : [],
        };
        return node;
    }

    /// <summary>
    /// Reprend l'état d'une fiche fraîchement reçue, sans changer d'instance.
    ///
    /// L'inventaire « nodes » renvoie la fiche complète, sauf la date de dernière
    /// connexion qui vient de « lastconnects » : elle est conservée si la
    /// nouvelle fiche ne la porte pas.
    /// </summary>
    public void Adopt(MeshNode fresh)
    {
        MeshId = fresh.MeshId;
        Name = fresh.Name;
        RealName = fresh.RealName;
        Host = fresh.Host;
        Icon = fresh.Icon;
        Conn = fresh.Conn;
        Pwr = fresh.Pwr;
        OsDescription = fresh.OsDescription;
        Ip = fresh.Ip;
        MType = fresh.MType;
        Desc = fresh.Desc;
        SshPort = fresh.SshPort;
        RdpPort = fresh.RdpPort;
        Sessions = fresh.Sessions;
        LastConnect = fresh.LastConnect ?? LastConnect;
    }

    /// <summary>Fusionne un node partiel reçu via un événement changenode.</summary>
    public void Merge(JsonElement json)
    {
        if (StrOrNull(json, "name") is { } n) Name = n;
        if (StrOrNull(json, "rname") is { } rn) RealName = rn;
        if (StrOrNull(json, "host") is { } h) Host = h;
        if (IntOrNull(json, "icon") is { } ic) Icon = ic;
        if (IntOrNull(json, "conn") is { } c) Conn = c;
        if (IntOrNull(json, "pwr") is { } p) Pwr = p;
        if (StrOrNull(json, "osdesc") is { } os) OsDescription = os;
        if (StrOrNull(json, "ip") is { } ip) Ip = ip;
        if (StrOrNull(json, "desc") is { } d) Desc = d;
        if (IntOrNull(json, "sshport") is { } sp) SshPort = sp;

        // Les sessions ont leur propre événement (« devicesessions ») : une
        // fiche qui ne les porte pas ne dit rien à leur sujet.
        if (json.TryGetProperty("sessions", out var sessions)) Sessions = ParseSessions(sessions);
    }

    /// <summary>Famille d'OS déduite de la description serveur, sinon du nom.</summary>
    public OSFamily OsFamily
    {
        get
        {
            // Lu pour chaque machine à chaque filtrage et à chaque tri : le
            // résultat est gardé tant que la description et le nom n'ont pas
            // changé, au lieu de refaire minuscules et recherches à chaque fois.
            var description = OsDescription;
            var name = Name;
            if (_osFamilyCache is { } cached
                && ReferenceEquals(cached.Description, description) && ReferenceEquals(cached.Name, name))
                return cached.Family;

            var family = ComputeOsFamily(description, name);
            _osFamilyCache = (description, name, family);
            return family;
        }
    }

    private (string? Description, string Name, OSFamily Family)? _osFamilyCache;

    private static OSFamily ComputeOsFamily(string? osDescription, string name)
    {
        {
            var os = osDescription?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(os))
            {
                if (os.Contains("windows")) return OSFamily.Windows;
                if (os.Contains("macos") || os.Contains("mac os") || os.Contains("os x") || os.Contains("darwin"))
                    return OSFamily.MacOS;
                if (os.Contains("android") || os.Contains("ios") || os.Contains("iphone") || os.Contains("ipad"))
                    return OSFamily.Mobile;
                if (os.Contains("linux") || os.Contains("ubuntu") || os.Contains("debian") || os.Contains("fedora")
                    || os.Contains("centos") || os.Contains("raspbian") || os.Contains("alpine") || os.Contains("bsd"))
                    return OSFamily.Linux;
            }
            var hint = (string.IsNullOrEmpty(osDescription) ? name : osDescription!).ToLowerInvariant();
            if (hint.Contains("iphone") || hint.Contains("ipad") || hint.Contains("ipod") || hint.Contains("android"))
                return OSFamily.Mobile;
            return OSFamily.Other;
        }
    }
}

public sealed class ServerInfo
{
    public string? Name { get; init; }
    public string? Domain { get; init; }
    public int Features { get; init; }
    public int Features2 { get; init; }

    public static ServerInfo From(JsonElement json) => new()
    {
        Name = json.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
        Domain = json.TryGetProperty("domain", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
        Features = json.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0,
        Features2 = json.TryGetProperty("features2", out var f2) && f2.ValueKind == JsonValueKind.Number ? f2.GetInt32() : 0,
    };
}

public sealed class UserInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? RealName { get; init; }
    public string? Email { get; init; }
    public int SiteAdmin { get; init; }

    public static UserInfo? From(JsonElement json)
    {
        if (!json.TryGetProperty("_id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
        if (!json.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return null;
        return new UserInfo
        {
            Id = idEl.GetString()!,
            Name = nameEl.GetString()!,
            RealName = json.TryGetProperty("realname", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            Email = json.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
            SiteAdmin = json.TryGetProperty("siteadmin", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0,
        };
    }
}
