using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshControl.Core;

/// <summary>
/// Réglages persistants de l'application (serveur, identifiants).
///
/// Le mot de passe est chiffré avec DPAPI (ProtectedData, portée utilisateur) :
/// c'est l'équivalent Windows d'un trousseau de clés — le fichier n'est déchiffrable
/// que par la session Windows qui l'a écrit. On ne stocke jamais de mot de passe
/// en clair sur le disque.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Serveur MeshCentral par défaut de l'entreprise.</summary>
    public const string DefaultHost = "meshcentral.example.com";

    public string Host { get; set; } = DefaultHost;
    public string Username { get; set; } = "";
    public bool AllowSelfSigned { get; set; }
    public bool RememberCredentials { get; set; } = true;

    /// <summary>Lanceur rapide : raccourci global, au format « Ctrl+Alt+W ».</summary>
    public string QuickSearchHotkey { get; set; } = "Ctrl+Alt+W";

    /// <summary>Active le raccourci global (désactivable en cas de conflit).</summary>
    public bool QuickSearchEnabled { get; set; } = true;

    /// <summary>Réduire dans la zone de notification au lieu de quitter.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Démarrer réduit dans la zone de notification (utile au démarrage auto).</summary>
    public bool StartMinimized { get; set; }

    /// <summary>Journal détaillé (inclut les événements de protocole).</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Densité de la liste : 0 compact, 1 normal, 2 confortable. Sur un parc
    /// important, le mode compact fait tenir davantage de machines à l'écran.
    /// </summary>
    public int ListDensity { get; set; } = 1;

    /// <summary>Rechercher les mises à jour au démarrage.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Version dont l'utilisateur a refusé l'installation. On ne la repropose
    /// plus au démarrage, sinon « Plus tard » réapparaîtrait à chaque lancement.
    /// La recherche manuelle l'ignore.
    /// </summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Qualité d'image par défaut du bureau à distance (0 basse, 1 équilibrée, 2 haute).</summary>
    public int DefaultDesktopQuality { get; set; } = 1;

    /// <summary>Cadence par défaut du bureau à distance, en images par seconde.</summary>
    public int DefaultDesktopFrameRate { get; set; } = 10;

    /// <summary>
    /// Échelle d'encodage par défaut du bureau à distance, en pourcentage.
    /// Réduire fait encoder moins de pixels à la source : c'est le levier le plus
    /// efficace sur la bande passante, au prix de la netteté.
    /// </summary>
    public int DefaultDesktopScale { get; set; } = 100;

    /// <summary>Ouvrir le bureau à distance ajusté à la fenêtre.</summary>
    public bool DefaultDesktopFit { get; set; } = true;

    /// <summary>
    /// Regrouper les sessions dans une fenêtre à onglets. Sinon, chaque session
    /// ouvre sa propre fenêtre (comportement historique).
    /// </summary>
    public bool UseTabbedSessions { get; set; } = true;

    /// <summary>Identifiants des machines épinglées en favoris.</summary>
    public List<string> FavoriteNodeIds { get; set; } = new();

    /// <summary>
    /// Machines ouvertes récemment, de la plus récente à la plus ancienne.
    /// </summary>
    public List<string> RecentNodeIds { get; set; } = new();

    /// <summary>Dossiers repliés, mémorisés d'une session à l'autre.</summary>
    public List<string> CollapsedGroups { get; set; } = new();

    public bool IsFavorite(string nodeId) => FavoriteNodeIds.Contains(nodeId);

    public void ToggleFavorite(string nodeId)
    {
        if (!FavoriteNodeIds.Remove(nodeId)) FavoriteNodeIds.Add(nodeId);
    }

    /// <summary>Place une machine en tête des récentes (10 conservées).</summary>
    public void RememberRecent(string nodeId)
    {
        RecentNodeIds.Remove(nodeId);
        RecentNodeIds.Insert(0, nodeId);
        if (RecentNodeIds.Count > 10) RecentNodeIds.RemoveRange(10, RecentNodeIds.Count - 10);
    }


    /// <summary>Mot de passe chiffré DPAPI, encodé en base64. Jamais en clair.</summary>
    public string? ProtectedPassword { get; set; }

    [JsonIgnore]
    public string Password
    {
        get => Unprotect(ProtectedPassword);
        set => ProtectedPassword = string.IsNullOrEmpty(value) ? null : Protect(value);
    }

    // MARK: - Persistance

    private static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Mesh Control");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings(); // fichier corrompu : on repart des valeurs par défaut
        }
    }

    /// <summary>
    /// Options de sérialisation, créées une seule fois.
    ///
    /// System.Text.Json met en cache les métadonnées d'un type PAR instance
    /// d'options : en créer une à chaque enregistrement annulait ce cache et
    /// refaisait l'analyse du type à chaque appel. Or Save() est appelé souvent —
    /// repli d'un dossier, ajout d'un favori, ouverture d'une machine.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    // Écriture différée : le contenu à écrire, et le minuteur qui déclenche
    // l'écriture réelle. Un seul fichier, donc un seul état partagé.
    private static readonly object WriteLock = new();
    private static string? _pendingJson;
    private static System.Threading.Timer? _writeTimer;

    /// <summary>Délai de regroupement des écritures.</summary>
    private const int WriteDelayMs = 500;

    /// <summary>
    /// Enregistre les réglages, en différant l'écriture disque.
    ///
    /// La sérialisation reste sur le fil appelant : elle est rapide, et surtout
    /// elle lit l'objet pendant que son propriétaire ne le modifie pas. La
    /// sérialiser en tâche de fond exposerait les collections (favoris, dossiers
    /// repliés) à une modification concurrente. Seule l'écriture disque, elle
    /// vraiment lente, part en arrière-plan — et les appels rapprochés sont
    /// regroupés en une seule écriture.
    /// </summary>
    public void Save()
    {
        string json;
        try { json = JsonSerializer.Serialize(this, SerializerOptions); }
        catch { return; }

        lock (WriteLock)
        {
            _pendingJson = json;   // le dernier état l'emporte
            _writeTimer ??= new System.Threading.Timer(_ => FlushToDisk(), null,
                                                       System.Threading.Timeout.Infinite,
                                                       System.Threading.Timeout.Infinite);
            _writeTimer.Change(WriteDelayMs, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>
    /// Écrit sans attendre. À utiliser à l'arrêt de l'application et pour les
    /// opérations sensibles (effacement des identifiants), qui ne doivent pas
    /// dépendre d'un minuteur susceptible de ne jamais se déclencher.
    /// </summary>
    public void SaveNow()
    {
        Save();
        FlushToDisk();
    }

    /// <summary>Écrit le contenu en attente, s'il y en a un.</summary>
    public static void FlushToDisk()
    {
        string? json;
        lock (WriteLock)
        {
            json = _pendingJson;
            _pendingJson = null;
            _writeTimer?.Change(System.Threading.Timeout.Infinite,
                                System.Threading.Timeout.Infinite);
        }

        if (json is null) return;
        try { File.WriteAllText(SettingsPath, json); }
        catch { /* échec d'écriture : non bloquant */ }
    }

    /// <summary>Efface les identifiants mémorisés (utilisateur + mot de passe).</summary>
    public void ClearCredentials()
    {
        Username = "";
        ProtectedPassword = null;
        // Écriture immédiate : le mot de passe ne doit pas rester sur le disque
        // parce qu'un minuteur n'a pas eu le temps de se déclencher.
        SaveNow();
    }

    // MARK: - Raccourci clavier

    /// <summary>
    /// Analyse une chaîne « Ctrl+Alt+W » en (modificateurs, touche).
    /// Renvoie null si la chaîne est invalide ou sans modificateur — un raccourci
    /// global sans modificateur capturerait la touche dans toutes les applications.
    /// </summary>
    public static (System.Windows.Input.ModifierKeys Modifiers, System.Windows.Input.Key Key)?
        ParseHotkey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;

        var mods = System.Windows.Input.ModifierKeys.None;
        System.Windows.Input.Key? key = null;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= System.Windows.Input.ModifierKeys.Control; break;
                case "alt": mods |= System.Windows.Input.ModifierKeys.Alt; break;
                case "shift" or "maj": mods |= System.Windows.Input.ModifierKeys.Shift; break;
                case "win" or "windows": mods |= System.Windows.Input.ModifierKeys.Windows; break;
                default:
                    // Noms francisés produits par FormatHotkey, puis repli sur le
                    // nom interne de WPF (compatibilité avec les réglages existants).
                    if (ParseFriendlyKeyName(part) is { } friendly) key = friendly;
                    else if (Enum.TryParse<System.Windows.Input.Key>(part, ignoreCase: true, out var k))
                        key = k;
                    break;
            }
        }

        if (key is null || mods == System.Windows.Input.ModifierKeys.None) return null;
        return (mods, key.Value);
    }

    /// <summary>Inverse de <c>FriendlyKeyName</c>, pour relire un raccourci enregistré.</summary>
    private static System.Windows.Input.Key? ParseFriendlyKeyName(string name) => name switch
    {
        "²" => System.Windows.Input.Key.OemQuotes,
        "-" => System.Windows.Input.Key.OemMinus,
        "+" => System.Windows.Input.Key.OemPlus,
        "," => System.Windows.Input.Key.OemComma,
        "." => System.Windows.Input.Key.OemPeriod,
        "/" => System.Windows.Input.Key.OemQuestion,
        ";" => System.Windows.Input.Key.OemSemicolon,
        "[" => System.Windows.Input.Key.OemOpenBrackets,
        "]" => System.Windows.Input.Key.OemCloseBrackets,
        "\\" => System.Windows.Input.Key.OemBackslash,
        "Espace" => System.Windows.Input.Key.Space,
        "Entrée" => System.Windows.Input.Key.Return,
        "Échap" => System.Windows.Input.Key.Escape,
        "Retour" => System.Windows.Input.Key.Back,
        "Suppr" => System.Windows.Input.Key.Delete,
        "Inser" => System.Windows.Input.Key.Insert,
        "Début" => System.Windows.Input.Key.Home,
        "Fin" => System.Windows.Input.Key.End,
        "Page↑" => System.Windows.Input.Key.PageUp,
        "Page↓" => System.Windows.Input.Key.PageDown,
        "↑" => System.Windows.Input.Key.Up,
        "↓" => System.Windows.Input.Key.Down,
        "←" => System.Windows.Input.Key.Left,
        "→" => System.Windows.Input.Key.Right,
        _ when name.Length == 1 && char.IsAsciiDigit(name[0])
            => System.Windows.Input.Key.D0 + (name[0] - '0'),
        _ => null,
    };

    /// <summary>Met en forme un raccourci sous la forme « Ctrl+Alt+W ».</summary>
    public static string FormatHotkey(System.Windows.Input.ModifierKeys modifiers,
                                     System.Windows.Input.Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) parts.Add("Maj");
        if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(FriendlyKeyName(key));
        return string.Join('+', parts);
    }

    /// <summary>
    /// Nom lisible d'une touche. WPF nomme les touches de ponctuation « OemQuotes »,
    /// « OemMinus »… ce qui est incompréhensible dans une interface.
    /// </summary>
    private static string FriendlyKeyName(System.Windows.Input.Key key) => key switch
    {
        System.Windows.Input.Key.OemQuotes => "²",
        System.Windows.Input.Key.OemTilde => "²",
        System.Windows.Input.Key.OemMinus => "-",
        System.Windows.Input.Key.OemPlus => "+",
        System.Windows.Input.Key.OemComma => ",",
        System.Windows.Input.Key.OemPeriod => ".",
        System.Windows.Input.Key.OemQuestion => "/",
        System.Windows.Input.Key.OemSemicolon => ";",
        System.Windows.Input.Key.OemOpenBrackets => "[",
        System.Windows.Input.Key.OemCloseBrackets => "]",
        System.Windows.Input.Key.OemBackslash or System.Windows.Input.Key.OemPipe => "\\",
        System.Windows.Input.Key.Space => "Espace",
        System.Windows.Input.Key.Return => "Entrée",
        System.Windows.Input.Key.Escape => "Échap",
        System.Windows.Input.Key.Back => "Retour",
        System.Windows.Input.Key.Delete => "Suppr",
        System.Windows.Input.Key.Insert => "Inser",
        System.Windows.Input.Key.Home => "Début",
        System.Windows.Input.Key.End => "Fin",
        System.Windows.Input.Key.PageUp => "Page↑",
        System.Windows.Input.Key.PageDown => "Page↓",
        System.Windows.Input.Key.Up => "↑",
        System.Windows.Input.Key.Down => "↓",
        System.Windows.Input.Key.Left => "←",
        System.Windows.Input.Key.Right => "→",
        >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9
            => ((int)(key - System.Windows.Input.Key.D0)).ToString(),
        _ => key.ToString(),
    };

    // MARK: - Chiffrement DPAPI

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MeshControl.v1");

    private static string Protect(string plain)
    {
        try
        {
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return "";
        }
    }

    private static string Unprotect(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(encoded), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return ""; // chiffré sur une autre machine/session : illisible ici
        }
    }
}
