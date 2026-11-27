using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using MeshControl.Core;

namespace MeshControl.Ssh;

public enum SshSessionState
{
    Connecting,
    NeedsCredentials,
    Authenticating,
    Connected,
    Closed,
}

/// <summary>
/// SSH via le relais sshterminalrelay de MeshCentral : c'est le *serveur* qui
/// exécute le client SSH ; nous n'échangeons que des messages JSON de contrôle et
/// du texte terminal préfixé par « ~ ».
/// </summary>
public sealed class SshSession : IDisposable
{
    public SshSessionState State { get; private set; } = SshSessionState.Connecting;

    /// <summary>Vrai quand le serveur ne demande que la phrase de passe d'une clé stockée.</summary>
    public bool KeyPassOnly { get; private set; }

    public string? CloseReason { get; private set; }
    public string? AuthError { get; private set; }

    public int Cols { get; private set; } = 80;
    public int Rows { get; private set; } = 24;

    /// <summary>Sortie du terminal. Les octets reçus avant abonnement sont tamponnés.</summary>
    public event Action<string>? Output;
    public event Action? StateChanged;

    private readonly StringBuilder _pendingOutput = new();
    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;
    private readonly Dispatcher _ui;
    private MeshWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private bool _stopped;

    private readonly Channel<string> _sendQueue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public SshSession(MeshServerConnection connection, MeshNode node, Dispatcher ui)
    {
        _connection = connection;
        _node = node;
        _ui = ui;
    }

    private void SetState(SshSessionState state)
    {
        State = state;
        _ui.BeginInvoke(() => StateChanged?.Invoke());
    }

    // MARK: - Cycle de vie

    public async Task StartAsync()
    {
        try
        {
            var socket = await _connection.OpenSshTunnelAsync(_node.Id).ConfigureAwait(false);
            if (_stopped) { socket.Close(); socket.Dispose(); return; }
            _socket = socket;
            _cts = new CancellationTokenSource();
            StartSenderLoop(socket, _cts.Token);
            StartReceiveLoop(socket, _cts.Token);
        }
        catch (Exception ex)
        {
            if (_stopped) return;
            CloseReason = ex.Message;
            SetState(SshSessionState.Closed);
        }
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try { _cts?.Cancel(); } catch { }
        _sendQueue.Writer.TryComplete();
        _socket?.Close();
        _socket?.Dispose();
        _socket = null;
        if (State != SshSessionState.Closed) SetState(SshSessionState.Closed);
    }

    // MARK: - Authentification

    public void SubmitCredentials(string username, string password, bool remember)
    {
        SetState(SshSessionState.Authenticating);
        SendJson(new
        {
            action = "sshauth",
            username,
            password,
            keep = remember ? 1 : 0,
            cols = Cols, rows = Rows,
            width = Cols * 8, height = Rows * 16,
        });
    }

    /// <summary>
    /// L'appareil possède une clé privée stockée côté serveur ; seule sa phrase de
    /// passe est requise. On envoie « sshkeyauth » et non « sshauth », qui
    /// écraserait la clé par une tentative mot de passe.
    /// </summary>
    public void SubmitKeyPassphrase(string keyPass)
    {
        SetState(SshSessionState.Authenticating);
        SendJson(new
        {
            action = "sshkeyauth",
            keypass = keyPass,
            cols = Cols, rows = Rows,
            width = Cols * 8, height = Rows * 16,
        });
    }

    private void SendAutoAuth()
    {
        SetState(SshSessionState.Authenticating);
        SendJson(new
        {
            action = "sshautoauth",
            cols = Cols, rows = Rows,
            width = Cols * 8, height = Rows * 16,
        });
    }

    // MARK: - Entrées/sorties terminal

    /// <summary>Envoie une saisie clavier au shell distant (préfixe « ~ »).</summary>
    public void SendInput(string text)
    {
        if (State != SshSessionState.Connected || string.IsNullOrEmpty(text)) return;
        _sendQueue.Writer.TryWrite("~" + text);
    }

    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0 || (cols == Cols && rows == Rows)) return;
        Cols = cols;
        Rows = rows;
        if (State != SshSessionState.Connected) return;
        SendJson(new
        {
            action = "resize",
            cols, rows,
            width = cols * 8, height = rows * 16,
        });
    }

    // MARK: - Réception

    private void StartSenderLoop(MeshWebSocket socket, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var text in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try { await socket.SendAsync(text, ct).ConfigureAwait(false); }
                    catch { break; }
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private void StartReceiveLoop(MeshWebSocket socket, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                WsMessage message;
                try { message = await socket.ReceiveAsync(ct).ConfigureAwait(false); }
                catch
                {
                    if (State != SshSessionState.Closed)
                    {
                        CloseReason = "La session SSH s'est terminée.";
                        SetState(SshSessionState.Closed);
                    }
                    return;
                }

                if (message is WsMessage.Text(var text))
                {
                    try { Handle(text); }
                    catch (Exception ex)
                    {
                        Log.Debug($"Trame ignorée : {ex.Message}", "SSH");
                    }
                }
            }
        }, ct);
    }

    private void Handle(string text)
    {
        // Le serveur signale un terminal prêt par le seul caractère « c ».
        if (text is "c" or "cr")
        {
            SetState(SshSessionState.Connected);
            // Le terminal peut avoir été redimensionné pendant l'authentification.
            SendJson(new
            {
                action = "resize",
                cols = Cols, rows = Rows,
                width = Cols * 8, height = Rows * 16,
            });
            return;
        }

        // Données terminal : tout ce qui commence par « ~ ».
        if (text.StartsWith('~'))
        {
            QueueOutput(text[1..]);
            return;
        }

        if (!text.StartsWith('{')) return;

        JsonElement json;
        try
        {
            using var doc = JsonDocument.Parse(text);
            json = doc.RootElement.Clone();
        }
        catch { return; }

        // Canal de contrôle : répondre au ping pour maintenir la session.
        if (json.TryGetProperty("ctrlChannel", out _))
        {
            if (json.TryGetProperty("type", out var t) && t.GetString() == "ping")
                SendJson(new { ctrlChannel = "102938", type = "pong" });
            return;
        }

        var action = json.TryGetProperty("action", out var a) ? a.GetString() : null;
        switch (action)
        {
            case "sshauth":
                KeyPassOnly = json.TryGetProperty("askkeypass", out var k)
                              && k.ValueKind == JsonValueKind.True;
                AuthError = null;
                SetState(SshSessionState.NeedsCredentials);
                break;

            case "sshautoauth":
                // Des identifiants sont déjà stockés côté serveur : rien à saisir.
                SendAutoAuth();
                break;

            case "autherror":
                KeyPassOnly = false;
                AuthError = "Authentification refusée — vérifiez l'identifiant et le mot de passe.";
                SetState(SshSessionState.NeedsCredentials);
                break;

            case "sessiontimeout":
                CloseReason = "Délai dépassé. Le service SSH est-il actif sur la machine ?";
                SetState(SshSessionState.Closed);
                break;

            case "sessionerror":
            case "connectionerror":
                CloseReason = "Le serveur n'a pas pu joindre la machine en SSH.";
                SetState(SshSessionState.Closed);
                break;
        }
    }

    /// <summary>Vide le tampon de sortie accumulé avant l'abonnement à Output.</summary>
    public string DrainPendingOutput()
    {
        var text = _pendingOutput.ToString();
        _pendingOutput.Clear();
        return text;
    }

    private void SendJson(object obj)
        => _sendQueue.Writer.TryWrite(JsonSerializer.Serialize(obj));

    public void Dispose() => Stop();

    // MARK: - Remontée de la sortie vers l'interface

    /// <summary>Sortie reçue en attente d'affichage, et verrou qui la protège.</summary>
    private readonly System.Text.StringBuilder _outbox = new();
    private bool _outboxScheduled;

    /// <summary>
    /// Ajoute de la sortie à afficher. Une seule mise à jour de l'interface est
    /// en file à la fois : les trames qui arrivent entre-temps la rejoignent.
    /// Une tâche par trame remplissait la file du thread d'interface pendant un
    /// gros affichage (cat, find /) : le terminal paraissait figé et Ctrl+C
    /// n'était traité qu'une fois la file vidée.
    /// </summary>
    private void QueueOutput(string text)
    {
        lock (_outbox)
        {
            _outbox.Append(text);
            if (_outboxScheduled) return;
            _outboxScheduled = true;
        }
        _ui.BeginInvoke(FlushOutput);
    }

    private void FlushOutput()
    {
        string text;
        lock (_outbox)
        {
            text = _outbox.ToString();
            _outbox.Clear();
            _outboxScheduled = false;
        }
        if (text.Length == 0) return;
        if (Output is not null) Output.Invoke(text);
        else _pendingOutput.Append(text);
    }
}
