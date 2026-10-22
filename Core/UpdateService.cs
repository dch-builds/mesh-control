using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace MeshControl.Core;

/// <summary>Une version disponible sur le dépôt.</summary>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string AssetName,
    string AssetApiUrl,
    long AssetSize,
    string? Notes);

/// <summary>
/// Vérifie si une version plus récente est publiée, la télécharge et lance
/// l'installeur.
///
/// Le dépôt étant privé, l'API GitHub exige un jeton (voir UpdateSecrets et
/// l'avertissement de sécurité qui l'accompagne). Sans jeton configuré, la
/// vérification est simplement désactivée.
/// </summary>
public static class UpdateService
{
    /// <summary>Version en cours d'exécution, lue sur l'assembly.</summary>
    public static Version CurrentVersion
    {
        get
        {
            var raw = Assembly.GetExecutingAssembly().GetName().Version;
            // Normalise en 3 composantes : l'assembly porte « 1.1.4.0 » alors que
            // les tags s'écrivent « v1.1.4 ».
            return raw is null ? new Version(0, 0, 0)
                              : new Version(raw.Major, raw.Minor, raw.Build);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mesh-Control-Updater");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", UpdateSecrets.Token);
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    /// <summary>
    /// Interroge la dernière version publiée. Renvoie null s'il n'y a rien de
    /// plus récent, si la vérification est désactivée, ou en cas d'échec réseau —
    /// une mise à jour indisponible ne doit jamais gêner l'utilisation.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (!UpdateSecrets.IsConfigured) return null;

        try
        {
            using var client = CreateClient();
            var url = $"https://api.github.com/repos/{UpdateSecrets.Repository}/releases/latest";
            using var response = await client.GetAsync(url, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning($"Vérification des mises à jour : HTTP {(int)response.StatusCode}",
                    "Mise à jour");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Les brouillons ne doivent pas être proposés.
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                return null;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag is null || ParseVersion(tag) is not { } version) return null;
            if (version <= CurrentVersion) return null;

            // On cherche l'installeur parmi les fichiers joints.
            if (!root.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array) return null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                // L'URL d'API (et non browser_download_url) : elle accepte le
                // jeton, ce qui est indispensable sur un dépôt privé.
                var apiUrl = asset.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (apiUrl is null) continue;

                long size = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

                Log.Info($"Version {version} disponible (actuelle : {CurrentVersion})",
                    "Mise à jour");
                return new UpdateInfo(version, tag, name, apiUrl, size, notes);
            }

            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            // Pas de réseau, serveur injoignable : sans conséquence.
            Log.Warning($"Vérification des mises à jour impossible : {ex.Message}",
                "Mise à jour");
            return null;
        }
    }

    /// <summary>« v1.1.4 » ou « 1.1.4 » vers un objet Version.</summary>
    private static Version? ParseVersion(string tag)
    {
        var text = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(text, out var v)
            ? new Version(v.Major, v.Minor, Math.Max(0, v.Build))
            : null;
    }

    /// <summary>
    /// Télécharge l'installeur dans le dossier temporaire. <paramref name="progress"/>
    /// reçoit une fraction entre 0 et 1.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateInfo update,
                                                   IProgress<double>? progress = null,
                                                   CancellationToken ct = default)
    {
        using var client = CreateClient();
        // L'API renvoie le binaire seulement avec cet Accept ; sinon elle
        // répondrait du JSON décrivant le fichier.
        var request = new HttpRequestMessage(HttpMethod.Get, update.AssetApiUrl);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/octet-stream");

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var dir = Path.Combine(Path.GetTempPath(), "Mesh Control Update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, update.AssetName);

        long total = response.Content.Headers.ContentLength ?? update.AssetSize;
        long read = 0;

        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = File.Create(path))
        {
            var buffer = new byte[81920];
            int count;
            while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                read += count;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        // Contrôle d'intégrité minimal : une coupure en cours de route donnerait
        // un installeur tronqué, qui échouerait de façon obscure.
        if (total > 0 && read != total)
        {
            try { File.Delete(path); } catch { }
            throw new IOException(
                $"Téléchargement incomplet ({read} octets sur {total}).");
        }

        Log.Info($"Installeur téléchargé : {path} ({read / 1024 / 1024} Mo)", "Mise à jour");
        return path;
    }

    /// <summary>
    /// Lance l'installeur en mode silencieux et rend la main : l'appelant doit
    /// fermer l'application, l'installeur ne pouvant pas remplacer des fichiers
    /// en cours d'utilisation.
    ///
    /// L'installeur relance l'application lui-même (section [Run] du script Inno).
    /// </summary>
    public static void LaunchInstaller(string installerPath)
    {
        // /SILENT plutôt que /VERYSILENT : la barre de progression reste visible,
        // l'utilisateur voit que quelque chose se passe pendant le remplacement.
        // /NOCANCEL évite un abandon au milieu du remplacement des fichiers.
        var info = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true,
            Arguments = "/SILENT /NOCANCEL /NORESTART /SUPPRESSMSGBOXES",
        };

        Process.Start(info);
        Log.Info("Installeur lancé, fermeture de l'application.", "Mise à jour");
    }
}
