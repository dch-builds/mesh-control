using System.Text.Json;
using Avalonia.Threading;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Réglages de la version macOS. Rien de secret ici : le mot de passe et le
/// cookie de second facteur vont dans le Trousseau (voir <see cref="Keychain"/>).
///
/// Emplacement : ~/Library/Application Support/Mesh Control/settings-mac.json
/// (sous Windows, pendant le développement : %LOCALAPPDATA%\Mesh Control\).
/// </summary>
public sealed class MacSettings : ICertificatePinStore
{
    public string Host { get; set; } = "";
    public string Username { get; set; } = "";
    public bool AllowSelfSigned { get; set; }
    /// <summary>« Rester connecté sur ce poste » : mot de passe gardé au Trousseau.</summary>
    public bool RememberPassword { get; set; } = true;

    // Mêmes réglages d'affichage que la version Windows (Core/AppSettings.cs).

    /// <summary>Densité de la liste : 0 compact, 1 normal, 2 confortable.</summary>
    public int ListDensity { get; set; } = 1;

    /// <summary>Barre latérale des dossiers (sinon : liste en colonnes).</summary>
    public bool FolderView { get; set; } = true;

    /// <summary>Tri : 0 nom, 1 en ligne d'abord, 2 dernière présence, 3 système.</summary>
    public int DeviceSort { get; set; }

    /// <summary>Thème sombre, lu au démarrage.</summary>
    public bool DarkMode { get; set; }

    public string? SelectedFolder { get; set; }
    public List<string> PinnedFolders { get; set; } = new();
    public List<string> HiddenFolders { get; set; } = new();
    public double FolderPaneWidth { get; set; } = 238;
    public List<string> FavoriteNodeIds { get; set; } = new();
    public List<string> RecentNodeIds { get; set; } = new();

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

    /// <summary>Qualité d'image par défaut du bureau à distance (voir DesktopQuality).</summary>
    public int DesktopQuality { get; set; } = 1;

    /// <summary>Taille de police du terminal, retenue au zoom.</summary>
    public int TerminalFontSize { get; set; } = 13;

    /// <summary>Bureau à distance : presse-papiers synchronisé avec la machine (préférence de travail).</summary>
    public bool DesktopClipboardSync { get; set; }

    /// <summary>Sessions (bureau, terminal, fichiers) regroupées en onglets dans une seule fenêtre.</summary>
    public bool UseTabbedSessions { get; set; } = true;

    /// <summary>Bureau à distance : échelle de l'image à l'ouverture (100, 75, 50 ou 33 %).</summary>
    public int DefaultDesktopScale { get; set; } = 100;

    /// <summary>Bureau à distance : écran ajusté à la fenêtre à l'ouverture.</summary>
    public bool DefaultDesktopFit { get; set; } = true;

    /// <summary>Ouverture des sessions : 0 fenêtre habituelle, 1 agrandie, 2 plein écran.</summary>
    public int DesktopOpenMode { get; set; }

    /// <summary>Raccourci global de la recherche rapide.</summary>
    public bool QuickSearchEnabled { get; set; } = true;
    public string QuickSearchHotkey { get; set; } = "Ctrl+Alt+W";

    /// <summary>Journal détaillé : échanges de protocole compris.</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>Nom du jeton de connexion de ce poste (le jeton lui-même est dans le Trousseau).</summary>
    public string? LoginTokenName { get; set; }

    /// <summary>Taille de la vignette du bureau ancré dans un terminal.</summary>
    public double DesktopDockWidth { get; set; } = 520;
    public double DesktopDockHeight { get; set; } = 330;

    /// <summary>La fermeture de la fenêtre garde l'application active dans la barre des menus.</summary>
    public bool KeepInMenuBar { get; set; } = true;

    /// <summary>Démarrer réduit dans la barre des menus, connexion en tâche de fond.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>Rechercher les mises à jour au démarrage.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Version proposée puis refusée : on ne réinsiste pas à chaque démarrage.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Commandes ajoutées par l'utilisateur au menu « Commandes » du terminal.</summary>
    public List<CustomTerminalCommand> CustomTerminalCommands { get; set; } = new();

    /// <summary>Fenêtre Fichiers : volet de ce poste affiché à côté de la machine.</summary>
    public bool FilesDualPane { get; set; }

    /// <summary>Dernier dossier ouvert dans le volet local de la fenêtre Fichiers.</summary>
    public string? FilesLocalFolder { get; set; }

    /// <summary>Envoi vers plusieurs machines : dernières destinations, par famille de système.</summary>
    public string? TransferWindowsFolder { get; set; }
    public string? TransferUnixFolder { get; set; }

    /// <summary>Images par seconde par défaut du bureau à distance.</summary>
    public int DesktopFrameRate { get; set; } = 10;

    /// <summary>Empreintes des certificats auto-signés retenus, par hôte.</summary>
    public Dictionary<string, string> PinnedCertificates { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mesh Control", "settings-mac.json");

    public static MacSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<MacSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warning($"Réglages illisibles, valeurs par défaut : {ex.Message}", "Réglages");
        }
        return new();
    }

    /// <summary>Thread de l'interface uniquement.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning($"Enregistrement des réglages impossible : {ex.Message}", "Réglages");
        }
    }

    // MARK: - ICertificatePinStore

    IEnumerable<KeyValuePair<string, string>> ICertificatePinStore.Load() => PinnedCertificates.ToList();

    /// <summary>
    /// Appelé depuis le fil de la connexion TLS : la modification et
    /// l'enregistrement se font sur le thread de l'interface.
    /// </summary>
    void ICertificatePinStore.Save(string host, string? fingerprint)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (fingerprint is null) PinnedCertificates.Remove(host);
            else PinnedCertificates[host] = fingerprint;
            Save();
        });
    }
}
