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
