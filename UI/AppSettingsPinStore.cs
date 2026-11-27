using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>
/// Empreintes de certificats conservées dans les réglages de l'application
/// Windows (voir <see cref="CertificateTrust"/>).
/// </summary>
public sealed class AppSettingsPinStore(AppSettings settings) : ICertificatePinStore
{
    public IEnumerable<KeyValuePair<string, string>> Load() => settings.PinnedCertificates.ToList();

    /// <summary>
    /// Les réglages sont modifiés sur le thread de l'interface, qui est aussi
    /// celui qui les sérialise : jamais depuis le thread de la connexion TLS.
    /// </summary>
    public void Save(string host, string? fingerprint)
    {
        var dispatcher = Application.Current?.Dispatcher;

        void Apply()
        {
            if (fingerprint is null) settings.PinnedCertificates.Remove(host);
            else settings.PinnedCertificates[host] = fingerprint;
            settings.SaveNow();
        }

        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else dispatcher.BeginInvoke(Apply);
    }
}
