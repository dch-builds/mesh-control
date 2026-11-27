namespace MeshControl.Core;

/// <summary>
/// Stockage des empreintes de certificats retenues (voir
/// <see cref="CertificateTrust"/>). Fourni par chaque application : les
/// réglages de la version Windows, ceux de la version macOS.
/// </summary>
public interface ICertificatePinStore
{
    /// <summary>Empreintes enregistrées, par hôte normalisé.</summary>
    IEnumerable<KeyValuePair<string, string>> Load();

    /// <summary>
    /// Retient — ou oublie, si <paramref name="fingerprint"/> est null —
    /// l'empreinte d'un hôte. Peut être appelé depuis le fil de la connexion
    /// TLS : à l'implémentation de se ramener sur le thread qui possède ses
    /// réglages.
    /// </summary>
    void Save(string host, string? fingerprint);
}
