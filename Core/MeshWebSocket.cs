using System.IO;
using System.Net.WebSockets;
using System.Text;

namespace MeshControl.Core;

/// <summary>Un message reçu d'un websocket MeshCentral : texte ou binaire.</summary>
public abstract record WsMessage
{
    public sealed record Text(string Value) : WsMessage;
    public sealed record Binary(byte[] Value) : WsMessage;
}

/// <summary>
/// Fin wrapper autour de ClientWebSocket : en-têtes personnalisés, confiance
/// optionnelle des certificats auto-signés, et réception asynchrone.
///
/// </summary>
public sealed class MeshWebSocket : IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly Uri _uri;
    private readonly CancellationTokenSource _cts = new();

    // Taille max d'un message reçu (les tuiles KVM jumbo peuvent être volumineuses).
    private const int ReceiveBufferSize = 64 * 1024;

    public MeshWebSocket(Uri uri, IReadOnlyDictionary<string, string>? headers, bool allowSelfSigned)
    {
        _uri = uri;

        foreach (var (k, v) in headers ?? new Dictionary<string, string>())
            _socket.Options.SetRequestHeader(k, v);

        // MeshCentral impose un contrôle d'origine côté navigateur ; on n'envoie
        // pas d'en-tête Origin.
        if (allowSelfSigned)
        {
            _socket.Options.RemoteCertificateValidationCallback =
                (_, _, _, _) => true;
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _socket.ConnectAsync(_uri, linked.Token).ConfigureAwait(false);
    }

    public async Task SendAsync(string text, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
                     .ConfigureAwait(false);
    }

    public async Task SendAsync(byte[] data, CancellationToken ct = default)
    {
        await _socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, ct)
                     .ConfigureAwait(false);
    }

    /// <summary>
    /// Reçoit un message complet (réassemble les fragments d'un même message
    /// websocket). Renvoie du texte ou du binaire selon le type de trame.
    /// </summary>
    public async Task<WsMessage> ReceiveAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var buffer = new byte[ReceiveBufferSize];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, linked.Token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException(
                    WebSocketError.ConnectionClosedPrematurely,
                    _socket.CloseStatusDescription ?? "Connexion fermée par le serveur.");
            }
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return result.MessageType == WebSocketMessageType.Text
            ? new WsMessage.Text(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length))
            : new WsMessage.Binary(ms.ToArray());
    }

    public void Close()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try
        {
            // Le serveur a souvent déjà coupé — refus d'authentification, second
            // facteur réclamé — sans que l'état ait quitté Open : la fermeture
            // échoue alors, et son échec doit être lu.
            if (_socket.State == WebSocketState.Open)
                _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                       .Forget();
        }
        catch { /* ignore */ }
    }

    public void Dispose()
    {
        Close();
        _socket.Dispose();
        _cts.Dispose();
    }
}
