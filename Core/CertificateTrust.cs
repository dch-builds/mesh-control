using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MeshControl.Core;

/// <summary>
/// Certificats auto-signés : confiance au premier usage.
///
/// Accepter « n'importe quel certificat » pour un serveur auto-signé livrait
/// les identifiants au premier intermédiaire venu — Wi-Fi public, DNS
/// détourné — : mot de passe, cookie de second facteur et jeton permanent
/// partent à chaque connexion. Désormais, le certificat présenté la première
/// fois est retenu (son empreinte SHA-256), et un certificat différent est
/// ensuite refusé. Un certificat valide pour une autorité reconnue reste
/// accepté normalement, sans rien retenir.
///
/// Pour accepter un nouveau certificat (renouvelé côté serveur), il suffit de
/// décocher l'option dans les réglages, d'enregistrer, puis de la recocher :
/// voir <see cref="Forget"/>.
/// </summary>
public static class CertificateTrust
{
    /// <summary>Empreintes retenues, par hôte (« hôte » ou « hôte:port »).</summary>
    private static readonly ConcurrentDictionary<string, string> Pins =
        new(StringComparer.OrdinalIgnoreCase);

    private static ICertificatePinStore? _store;

    /// <summary>Un certificat a été refusé parce qu'il ne correspond plus.</summary>
    public static bool HasRejectedChange { get; private set; }

    /// <summary>
    /// À appeler au démarrage : les empreintes sont relues du stockage, et y
    /// seront enregistrées.
    /// </summary>
    public static void Initialize(ICertificatePinStore store)
    {
        _store = store;
        Pins.Clear();
        foreach (var (host, fingerprint) in store.Load())
            Pins[host] = fingerprint;
    }

    /// <summary>Rappel de validation pour un WebSocket (ClientWebSocket).</summary>
    public static RemoteCertificateValidationCallback WebSocketCallback(string host) =>
        (_, certificate, _, errors) => Validate(host, certificate, errors);

    /// <summary>Rappel de validation pour un HttpClientHandler.</summary>
    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>
        HttpCallback(string host) =>
        (_, certificate, _, errors) => Validate(host, certificate, errors);

    /// <summary>
    /// Oublie le certificat retenu pour cet hôte : le prochain présenté sera
    /// retenu à sa place.
    /// </summary>
    public static void Forget(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return;
        var key = Key(host);
        HasRejectedChange = false;
        if (!Pins.TryRemove(key, out _)) return;

        Log.Info($"{key} : certificat retenu oublié.", "Sécurité");
        _store?.Save(key, null);
    }

    private static bool Validate(string host, X509Certificate? certificate, SslPolicyErrors errors)
    {
        // Certificat reconnu par le système : rien de particulier à faire.
        if (errors == SslPolicyErrors.None) return true;
        if (certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            return false;

        var key = Key(host);
        var fingerprint = Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256));

        if (Pins.TryGetValue(key, out var pinned))
        {
            if (string.Equals(pinned, fingerprint, StringComparison.OrdinalIgnoreCase)) return true;

            HasRejectedChange = true;
            Log.Warning($"{key} : le certificat présenté ({Short(fingerprint)}) n'est pas celui "
                        + $"retenu ({Short(pinned)}). Connexion refusée.", "Sécurité");
            return false;
        }

        // Premier contact : on retient ce certificat. GetOrAdd : deux
        // connexions simultanées ne retiennent pas deux certificats différents.
        var retained = Pins.GetOrAdd(key, fingerprint);
        if (!string.Equals(retained, fingerprint, StringComparison.OrdinalIgnoreCase)) return false;

        Log.Info($"{key} : certificat auto-signé retenu ({Short(fingerprint)}).", "Sécurité");
        _store?.Save(key, fingerprint);
        return true;
    }

    private static string Key(string host) => host.Trim().ToLowerInvariant();

    private static string Short(string fingerprint) =>
        fingerprint.Length > 16 ? fingerprint[..16] + "…" : fingerprint;
}
