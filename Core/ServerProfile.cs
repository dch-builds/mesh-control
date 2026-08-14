using System.Text.Json.Serialization;

namespace MeshControl.Core;

/// <summary>
/// Un serveur MeshCentral enregistré.
/// Le mot de passe n'est pas stocké ici ; il est fourni à la connexion.
/// </summary>
public sealed class ServerProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = "";
    public string Host { get; set; } = "";            // hostname[:port], sans schéma
    public string Username { get; set; } = "";
    public bool AllowSelfSigned { get; set; }
    public string UrlKey { get; set; } = "";          // clé d'accès de domaine (?key=), rare
    public bool AutoConnect { get; set; }

    /// <summary>URL de base https://host, nettoyée des schémas éventuels.</summary>
    [JsonIgnore]
    public Uri? BaseUri
    {
        get
        {
            var text = Host.Trim();
            text = text.Replace("https://", "").Replace("wss://", "");
            if (text.EndsWith("/")) text = text[..^1];
            if (string.IsNullOrEmpty(text)) return null;
            return Uri.TryCreate($"https://{text}", UriKind.Absolute, out var uri) ? uri : null;
        }
    }

    /// <summary>Construit une URL wss:// vers un chemin donné, avec query optionnelle.</summary>
    public Uri? WebSocketUri(string path, IEnumerable<KeyValuePair<string, string>>? query = null)
    {
        var baseUri = BaseUri;
        if (baseUri is null) return null;

        var items = new List<KeyValuePair<string, string>>();
        if (query is not null) items.AddRange(query);
        if (!string.IsNullOrEmpty(UrlKey))
            items.Add(new("key", UrlKey));

        var builder = new UriBuilder(baseUri)
        {
            Scheme = "wss",
            Path = "/" + path,
            Port = baseUri.IsDefaultPort ? -1 : baseUri.Port
        };
        if (items.Count > 0)
        {
            var qs = string.Join("&", items.Select(kv =>
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
            builder.Query = qs;
        }
        return builder.Uri;
    }
}
