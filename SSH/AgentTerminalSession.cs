using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using MeshControl.Core;

namespace MeshControl.Ssh;

/// <summary>Type de shell demandé à l'agent.</summary>
public enum ShellKind
{
    /// <summary>Shell par défaut de la machine (cmd.exe sous Windows, sh sous Linux).</summary>
    Default,
    /// <summary>PowerShell, avec les droits de l'agent (SYSTEM sous Windows).</summary>
    PowerShell,
    /// <summary>Shell dans le contexte de l'utilisateur connecté.</summary>
    UserShell,
    /// <summary>PowerShell dans le contexte de l'utilisateur connecté (Windows).</summary>
    UserPowerShell,
    /// <summary>
    /// Linux : l'agent lance /bin/login, qui demande identifiant et mot de passe
    /// directement dans la console — exactement comme une connexion sur console
    /// physique.
    /// </summary>
    LoginShell,
}

/// <summary>
/// Terminal passant par l'**agent** MeshCentral (relais meshrelay p=1/6/8), et non
/// par SSH. Avantage décisif : aucune configuration serveur n'est requise
/// (contrairement à SSH qui exige "ssh": true), et cela fonctionne sur Windows
/// comme sur Linux. C'est ce que propose l'interface web sous « Terminal ».
///
/// Le protocole est plus simple que SSH : après le handshake du tunnel, les
/// données du shell circulent en clair dans les deux sens, sans authentification
/// (les droits viennent de l'agent) ni préfixe.
/// </summary>
public sealed class AgentTerminalSession : IDisposable
{
    public SshSessionState State { get; private set; } = SshSessionState.Connecting;
    public string? CloseReason { get; private set; }
    public ShellKind Shell { get; }

    public int Cols { get; private set; } = 80;
    public int Rows { get; private set; } = 24;

    public event Action<string>? Output;
    public event Action? StateChanged;

    private readonly StringBuilder _pendingOutput = new();
    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;
    private readonly Dispatcher _ui;
    private MeshWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private bool _stopped;

    /// <summary>
    /// File d'envoi. Sur un tunnel meshrelay, la saisie clavier circule en trames
    /// BINAIRES, mais les messages du canal de contrôle (redimensionnement, pong)
    /// doivent partir en TEXTE : envoyés en binaire, l'agent les prendrait pour
    /// des frappes et les écrirait dans le shell.
    /// </summary>
    private readonly record struct Outbound(byte[] Data, bool IsText);
    private readonly Channel<Outbound> _sendQueue =
        Channel.CreateUnbounded<Outbound>(new UnboundedChannelOptions { SingleReader = true });

    public AgentTerminalSession(MeshServerConnection connection, MeshNode node,
                                ShellKind shell, Dispatcher ui)
    {
        _connection = connection;
        _node = node;
        _ui = ui;
        Shell = shell;
    }

    private RelayProtocol Protocol => Shell switch
    {
        ShellKind.PowerShell => RelayProtocol.PowerShell,
        ShellKind.UserShell => RelayProtocol.UserShell,
        ShellKind.UserPowerShell => RelayProtocol.UserPowerShell,
        ShellKind.LoginShell => RelayProtocol.LoginShell,
        _ => RelayProtocol.Terminal,
    };

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
            // OpenTunnelAsync gère déjà : demande de tunnel à l'agent, attente du
            // 'c'/'cr', puis envoi du numéro de protocole.
            var socket = await _connection.OpenTunnelAsync(_node.Id, Protocol)
                                          .ConfigureAwait(false);
            if (_stopped) { socket.Close(); socket.Dispose(); return; }

            _socket = socket;
            _cts = new CancellationTokenSource();
            StartSenderLoop(socket, _cts.Token);
            StartReceiveLoop(socket, _cts.Token);

            // Le shell est actif immédiatement. En mode Login Shell, c'est
            // /bin/login côté machine qui demandera identifiant et mot de passe
            // dans la console : rien à faire de notre côté.
            SetState(SshSessionState.Connected);
            SendResize(Cols, Rows);
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

    // MARK: - Entrées/sorties

    public void SendInput(string text)
    {
        if (State != SshSessionState.Connected || string.IsNullOrEmpty(text)) return;
        // Saisie clavier : trame binaire.
        _sendQueue.Writer.TryWrite(new Outbound(Encoding.UTF8.GetBytes(text), IsText: false));
    }

    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0 || (cols == Cols && rows == Rows)) return;
        Cols = cols;
        Rows = rows;
        if (State == SshSessionState.Connected) SendResize(cols, rows);
    }

    /// <summary>
    /// Informe l'agent des dimensions du terminal. Le message passe par le canal
    /// de contrôle du relais, comme le fait le client web.
    /// </summary>
    private void SendResize(int cols, int rows)
    {
        var json = JsonSerializer.Serialize(new
        {
            ctrlChannel = "102938",
            type = "termsize",
            cols,
            rows,
        });
        // Message de contrôle : trame TEXTE, sinon l'agent l'écrirait dans le shell.
        _sendQueue.Writer.TryWrite(new Outbound(Encoding.UTF8.GetBytes(json), IsText: true));
    }

    private void StartSenderLoop(MeshWebSocket socket, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        if (item.IsText)
                            await socket.SendAsync(Encoding.UTF8.GetString(item.Data), ct)
                                        .ConfigureAwait(false);
                        else
                            await socket.SendAsync(item.Data, ct).ConfigureAwait(false);
                    }
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
                        CloseReason = "La session terminal s'est terminée.";
                        SetState(SshSessionState.Closed);
                    }
                    return;
                }

                try { Handle(message); }
                catch (Exception ex)
                {
                    Log.Debug($"Trame ignorée : {ex.Message}", "Terminal");
                }
            }
        }, ct);
    }

    private void Handle(WsMessage message)
    {
        // L'agent peut envoyer la sortie du shell en texte ou en binaire selon la
        // plateforme : les deux sont du contenu terminal.
        string text = message switch
        {
            WsMessage.Text(var t) => t,
            WsMessage.Binary(var b) => Encoding.UTF8.GetString(b),
            _ => "",
        };
        if (text.Length == 0) return;

        // Trames du canal de contrôle : JSON à ne pas afficher dans le terminal.
        if (text[0] == '{' && text.Contains("ctrlChannel"))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("type", out var t)
                    && t.GetString() == "ping")
                {
                    var pong = JsonSerializer.Serialize(
                        new { ctrlChannel = "102938", type = "pong" });
                    // Contrôle : trame texte (voir SendResize).
                    _sendQueue.Writer.TryWrite(
                        new Outbound(Encoding.UTF8.GetBytes(pong), IsText: true));
                }
                return;
            }
            catch
            {
                // JSON mal formé : ce n'était pas du contrôle, on l'affiche.
            }
        }

        _ui.BeginInvoke(() =>
        {
            if (Output is not null) Output.Invoke(text);
            else _pendingOutput.Append(text);
        });
    }

    public string DrainPendingOutput()
    {
        var text = _pendingOutput.ToString();
        _pendingOutput.Clear();
        return text;
    }


    public void Dispose() => Stop();
}
