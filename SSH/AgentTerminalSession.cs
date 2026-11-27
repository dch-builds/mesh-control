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
    /// Décodeur conservé d'une trame à l'autre : un caractère accentué coupé
    /// entre deux trames serait sinon affiché comme deux caractères illisibles.
    /// </summary>
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();

    /// <summary>Le shell a-t-il déjà envoyé quelque chose ?</summary>
    private volatile bool _receivedOutput;

    /// <summary>
    /// Délai au-delà duquel un terminal resté vide reçoit une indication, plutôt
    /// que de laisser croire à une fenêtre figée.
    /// </summary>
    private static readonly TimeSpan SilenceHintDelay = TimeSpan.FromSeconds(6);

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
            _ = HintIfSilentAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            if (_stopped) return;
            CloseReason = ex.Message;
            SetState(SshSessionState.Closed);
        }
    }

    /// <summary>
    /// Terminal toujours vide après quelques secondes : on le dit, et l'on
    /// indique le geste qui débloque le plus souvent la situation.
    /// </summary>
    private async Task HintIfSilentAsync(CancellationToken ct)
    {
        try { await Task.Delay(SilenceHintDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        if (_receivedOutput || _stopped || State != SshSessionState.Connected) return;
        Log.Info($"{_node.Name} : aucune sortie du terminal après {SilenceHintDelay.TotalSeconds:0} s.", "Terminal");
        EmitLocal("Aucune sortie de la machine pour l'instant. Appuyez sur Entrée pour "
                  + "réafficher l'invite, ou changez de shell.");
    }

    /// <summary>
    /// Message de l'application (et non du shell), affiché en gris dans le
    /// terminal pour ne pas être confondu avec la sortie distante.
    /// </summary>
    private void EmitLocal(string message) =>
        _ui.BeginInvoke(() =>
        {
            var text = $"\u001B[90m» {message}\u001B[0m\r\n";
            if (Output is not null) Output.Invoke(text);
            else _pendingOutput.Append(text);
        });

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
            WsMessage.Binary(var b) => DecodeUtf8(b),
            _ => "",
        };
        if (text.Length == 0) return;

        // Trames du canal de contrôle : JSON à ne pas afficher dans le terminal.
        if (text[0] == '{' && text.Contains("ctrlChannel"))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "console")
                {
                    // Message de l'agent ou du serveur : attente de l'accord de
                    // l'utilisateur, refus, aucun utilisateur connecté… Ignoré
                    // jusqu'ici, il laissait le terminal noir sans explication.
                    if (doc.RootElement.TryGetProperty("msg", out var m)
                        && m.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(m.GetString()))
                    {
                        var raw = m.GetString()!;
                        Log.Info($"{_node.Name} : message de l'agent « {raw} ».", "Terminal");
                        EmitLocal(TranslateConsoleMessage(raw));
                    }
                }
                else if (type == "ping")
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

        _receivedOutput = true;
        QueueOutput(text);
    }

    private string DecodeUtf8(byte[] bytes)
    {
        var chars = new char[_utf8.GetCharCount(bytes, 0, bytes.Length, flush: false)];
        var count = _utf8.GetChars(bytes, 0, bytes.Length, chars, 0, flush: false);
        return new string(chars, 0, count);
    }

    /// <summary>Messages connus de l'agent, en français ; les autres tels quels.</summary>
    private static string TranslateConsoleMessage(string message) => message.Trim() switch
    {
        "Waiting for user to grant access..." =>
            "En attente de l'accord de l'utilisateur de la machine…",
        "Denied" => "Accès refusé par l'utilisateur de la machine.",
        "Timeout" => "L'utilisateur de la machine n'a pas répondu à temps.",
        _ => $"Agent : {message}",
    };

    public string DrainPendingOutput()
    {
        var text = _pendingOutput.ToString();
        _pendingOutput.Clear();
        return text;
    }


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
