using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using MeshControl.Core;

namespace MeshControl.Desktop;

/// <summary>Numéros de commandes KVM (sous-ensemble utilisé par ce client).</summary>
internal static class Kvm
{
    public const ushort Key = 1;
    public const ushort Mouse = 2;
    public const ushort Tile = 3;
    public const ushort Compression = 5;
    public const ushort Refresh = 6;
    public const ushort Screen = 7;
    public const ushort Pause = 8;
    public const ushort CtrlAltDel = 10;
    public const ushort GetDisplays = 11;
    public const ushort SetDisplay = 12;
    public const ushort TouchInit = 14;
    public const ushort Message = 17;
    public const ushort Jumbo = 27;
    public const ushort Alert = 65;
    public const ushort UnicodeKey = 85;
    public const ushort InputLock = 87;
    public const ushort DisplayGeometry = 82;
}

/// <summary>
/// Un écran de la machine distante. La géométrie (commande 82) n'est pas
/// toujours envoyée par l'agent : x/y/w/h valent alors 0 et seul l'identifiant
/// est exploitable.
/// </summary>
public sealed record RemoteDisplay(int Id, int X, int Y, int Width, int Height)
{
    /// <summary>Identifiant conventionnel de la vue « tous les écrans ».</summary>
    public const int AllDisplays = 65535;

    public bool HasGeometry => Width > 0 && Height > 0;

    /// <summary>Libellé court : « Écran 1 » ou « Écran 1 — 1920×1080 ».</summary>
    public string Label => HasGeometry
        ? $"Écran {Id} — {Width}×{Height}"
        : $"Écran {Id}";
}

public enum MouseButton { Left, Right, Middle }

public enum KeyAction : byte { Down = 0, Up = 1, ExtendedUp = 3, ExtendedDown = 4 }

/// <summary>
/// Qualité d'image. Deux modes s'ajoutent aux trois niveaux :
/// <see cref="Troubleshoot"/> pour les liaisons très lentes (compression
/// maximale, image réduite, cadence plafonnée), et <see cref="Fluid"/> pour
/// suivre une vidéo (cadence maximale, bonne qualité). Voir
/// DesktopSession.SendCompressionSettings.
/// </summary>
public enum DesktopQuality { Low, Balanced, High, Troubleshoot, Fluid }

public enum DesktopSessionState { Connecting, Connected, Reconnecting, Closed }

/// <summary>
/// Une session de bureau à distance : détient le socket relais, décode le flux
/// KVM en bitmap et encode les événements d'entrée.
/// </summary>
public sealed class DesktopSession : IDisposable
{
    public DesktopSessionState State { get; private set; } = DesktopSessionState.Connecting;
    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }
    public IReadOnlyList<int> Displays => _displays;
    public int SelectedDisplay { get; private set; }

    /// <summary>
    /// Écrans détectés, avec leur géométrie quand l'agent la fournit. Vide tant
    /// que la réponse à GetDisplays n'est pas arrivée.
    /// </summary>
    public IReadOnlyList<RemoteDisplay> DisplayList => _displayList;

    /// <summary>Vrai si la machine expose plus d'un écran.</summary>
    public bool HasMultipleDisplays => _displays.Count > 1;
    public string? RemoteMessage { get; private set; }
    public long TilesReceived { get; private set; }

    /// <summary>
    /// Latence aller-retour avec l'agent, en millisecondes. Null tant que la
    /// première réponse « rtt » n'est pas revenue.
    /// </summary>
    public int? LatencyMs { get; private set; }

    /// <summary>Octets reçus depuis le début de la session (flux d'images).</summary>
    public long BytesReceived { get; private set; }

    /// <summary>
    /// Débit instantané en Ko/s, mesuré sur la dernière fenêtre d'une seconde.
    /// </summary>
    public double ThroughputKbps { get; private set; }

    private long _windowBytes;
    private DateTime _windowStart = DateTime.UtcNow;
    public string? CloseReason { get; private set; }

    public KVMFramebuffer Framebuffer { get; } = new();

    private DesktopQuality _quality = DesktopQuality.Balanced;
    public DesktopQuality Quality
    {
        get => _quality;
        set
        {
            if (value == _quality) return;
            var scaleBefore = EffectiveScalePercent;
            var wasTroubleshoot = IsTroubleshootMode;
            _quality = value;

            // Une image nette en cours n'a plus de sens hors du mode dépannage.
            Interlocked.Increment(ref _sharpGeneration);
            _sharpActive = false;

            // Écran unique d'abord : l'échelle se calcule sur sa largeur.
            if (IsTroubleshootMode && !wasTroubleshoot)
            {
                EnsureSingleDisplay();
                RecomputeTroubleshootScale();
            }
            else if (!IsTroubleshootMode && wasTroubleshoot)
            {
                RestoreDisplayAfterTroubleshoot();
            }

            SendCompressionSettings();
            // Le mode dépannage réduit l'échelle : l'agent renvoie alors une
            // nouvelle taille d'écran, et l'image doit être reconstruite.
            if (EffectiveScalePercent != scaleBefore) SendCommand(Kvm.Refresh);

            ProbeNativeWidthLater();
        }
    }

    /// <summary>Vrai en mode dépannage (liaison très lente).</summary>
    public bool IsTroubleshootMode => _quality == DesktopQuality.Troubleshoot;

    /// <summary>Vrai en mode fluide (vidéo).</summary>
    public bool IsFluidMode => _quality == DesktopQuality.Fluid;

    /// <summary>
    /// Largeur visée en mode dépannage : assez pour lire une fenêtre, assez
    /// peu pour que chaque image reste légère. Un écran 1080p passe ainsi à
    /// 66 %, un écran 4K à 33 %.
    /// </summary>
    private const int TroubleshootTargetWidth = 1280;

    /// <summary>Échelle maximale en mode dépannage, quel que soit l'écran.</summary>
    private const int TroubleshootMaxScale = 66;

    /// <summary>Échelle du mode dépannage tant que la taille réelle de l'écran est inconnue.</summary>
    private const int TroubleshootDefaultScale = 50;

    /// <summary>Cadence plafonnée en mode dépannage, en images par seconde.</summary>
    private const int TroubleshootMaxFrameRate = 5;

    /// <summary>Qualité JPEG de l'image nette demandée en mode dépannage.</summary>
    private const byte SharpQuality = 60;

    /// <summary>Qualité JPEG du mode fluide : nette sans alourdir chaque trame.</summary>
    private const byte FluidQuality = 65;

    /// <summary>Intervalle entre trames du mode fluide : 25 ms, soit 40 img/s.</summary>
    private const ushort FluidFrameTimerMs = 25;

    /// <summary>
    /// Échelle calculée pour le mode dépannage d'après la largeur réelle de
    /// l'écran affiché. Ne bouge que par paliers de 5 points, pour qu'une
    /// estimation légèrement différente ne relance pas un rafraîchissement.
    /// </summary>
    private int _troubleshootScale = TroubleshootDefaultScale;

    /// <summary>
    /// Échelle réellement demandée à l'agent : celle choisie, ou celle du mode
    /// dépannage si elle est plus petite. Le choix de l'utilisateur est
    /// conservé et revient quand on quitte ce mode.
    /// </summary>
    public int EffectiveScalePercent => IsTroubleshootMode
        ? Math.Min(_scalePercent, _troubleshootScale)
        : _scalePercent;

    /// <summary>Cadence réellement demandée à l'agent, en images par seconde.</summary>
    public int EffectiveFrameRate
    {
        get
        {
            var timer = EffectiveFrameTimerMs;
            return timer > 0 ? 1000 / timer : 10;
        }
    }

    private ushort EffectiveFrameTimerMs => _quality switch
    {
        // Une trame toutes les 200 ms au plus : moins de trames, c'est autant
        // de tuiles en moins sur une liaison saturée.
        DesktopQuality.Troubleshoot => Math.Max(_frameTimerMs, (ushort)(1000 / TroubleshootMaxFrameRate)),
        DesktopQuality.Fluid => Math.Min(_frameTimerMs, FluidFrameTimerMs),
        _ => _frameTimerMs,
    };

    /// <summary>
    /// Intervalle entre deux trames côté agent, en millisecondes (100 ms = 10 img/s).
    /// Plus la valeur est basse, plus l'image est fluide — au prix de la bande passante.
    /// </summary>
    private ushort _frameTimerMs = 100;

    /// <summary>
    /// Cadences proposées, en images par seconde. Plafonnées à 40 : au-delà,
    /// l'agent et le relais suivent rarement, et le surcroît de fluidité ne se
    /// voit guère sur un bureau — seulement sur la bande passante.
    /// </summary>
    public static IReadOnlyList<int> FrameRates { get; } = new[] { 5, 10, 15, 20, 25, 30, 40 };

    /// <summary>
    /// Cadence proposée la plus proche, la plus basse en cas d'égalité. Ramène
    /// les réglages d'une version antérieure (45 ou 60 img/s) sur la liste.
    /// </summary>
    public static int NearestFrameRate(int fps) => FrameRates.MinBy(r => Math.Abs(r - fps));

    public int FrameRate
    {
        get => _frameTimerMs > 0 ? 1000 / _frameTimerMs : 10;
        set
        {
            var fps = Math.Clamp(value, 1, FrameRates[^1]);
            _frameTimerMs = (ushort)(1000 / fps);
            SendCompressionSettings();
        }
    }

    // Événements pour l'UI (déclenchés sur le thread UI).
    public event Action? FrameUpdated;
    public event Action? ScreenSizeChanged;
    public event Action? StateChanged;

    /// <summary>
    /// La liste des écrans a changé : arrivée de la réponse GetDisplays, ou de
    /// leur géométrie (envoyée séparément par l'agent, souvent après).
    /// </summary>
    public event Action? DisplaysChanged;

    private readonly MeshServerConnection _connection;
    private readonly MeshNode _node;
    private readonly Dispatcher _uiDispatcher;
    private MeshWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _rttTask;
    private Task? _senderTask;
    private readonly List<int> _displays = new();

    /// <summary>
    /// Écrans avec géométrie. Reconstruite depuis _displays à chaque réponse
    /// GetDisplays, puis enrichie quand la commande 82 arrive.
    /// </summary>
    private List<RemoteDisplay> _displayList = new();

    // File d'envoi ordonnée : les événements d'entrée doivent rester dans l'ordre.
    // On distingue trames binaires (commandes KVM) et texte (contrôle JSON) :
    // MeshCentral attend les trames de contrôle en TEXTE, pas en binaire.
    private readonly record struct Outbound(byte[] Data, bool IsText);

    // Recréée à chaque (re)connexion : Stop() complète définitivement le writer,
    // une file complétée refuserait toute écriture après reconnexion.
    private Channel<Outbound> _sendQueue = NewSendQueue();

    private static Channel<Outbound> NewSendQueue() =>
        Channel.CreateUnbounded<Outbound>(new UnboundedChannelOptions { SingleReader = true });

    private bool _stopped;

    public DesktopSession(MeshServerConnection connection, MeshNode node, Dispatcher uiDispatcher)
    {
        _connection = connection;
        _node = node;
        _uiDispatcher = uiDispatcher;
    }

    private void SetState(DesktopSessionState s)
    {
        State = s;
        // BeginInvoke : le fil de fond n'a pas besoin d'attendre que l'interface
        // ait fini de se mettre a jour.
        _uiDispatcher.BeginInvoke(() => StateChanged?.Invoke());
    }

    // MARK: - Cycle de vie

    public async Task StartAsync()
    {
        try
        {
            var socket = await _connection.OpenTunnelAsync(_node.Id, RelayProtocol.Desktop)
                                          .ConfigureAwait(false);
            if (_stopped) { socket.Close(); socket.Dispose(); return; }
            _socket = socket;
            _cts = new CancellationTokenSource();
            // Voir la reconnexion : les premières tailles annoncées peuvent
            // être à l'échelle par défaut de l'agent.
            _scalingChangedAt = Stopwatch.GetTimestamp();
            SetState(DesktopSessionState.Connected);
            StartSenderLoop(socket, _cts.Token);
            StartReceiveLoop(socket, _cts.Token);
            StartRtt(_cts.Token);
        }
        catch (Exception ex)
        {
            if (_stopped) return;
            CloseReason = ex.Message;
            // Première connexion : on tente aussi de reprendre. La machine peut
            // être en cours de démarrage au moment de l'ouverture.
            if (!TryBeginReconnect()) SetState(DesktopSessionState.Closed);
        }
    }

    // MARK: - Reconnexion automatique

    /// <summary>
    /// Nombre de tentatives déjà faites pour la coupure en cours. Remis à zéro
    /// dès qu'une connexion aboutit.
    /// </summary>
    private int _reconnectAttempt;

    /// <summary>Vrai pendant qu'une boucle de reconnexion est active.</summary>
    private bool _reconnecting;

    /// <summary>Désactive la reprise automatique (fermeture volontaire).</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Tentatives maximum avant d'abandonner.</summary>
    private const int MaxReconnectAttempts = 12;

    /// <summary>Tentative courante, pour l'affichage (1 = premiere).</summary>
    public int ReconnectAttempt => _reconnectAttempt;
    public int ReconnectAttemptsMax => MaxReconnectAttempts;

    /// <summary>
    /// Lance la boucle de reprise si elle est permise. Renvoie false quand la
    /// session doit rester fermée : arrêt volontaire, reprise désactivée, ou
    /// tentatives épuisées.
    /// </summary>
    private bool TryBeginReconnect()
    {
        if (_stopped || !AutoReconnect) return false;
        if (_reconnecting) return true;           // deja en cours
        if (_reconnectAttempt >= MaxReconnectAttempts) return false;

        _reconnecting = true;
        _ = ReconnectLoopAsync();
        return true;
    }

    /// <summary>
    /// Retente la connexion avec un délai croissant. Un poste qui redémarre met
    /// souvent une à deux minutes à revenir : l'attente monte jusqu'à 15 s pour
    /// couvrir ce cas sans marteler le serveur.
    /// </summary>
    private async Task ReconnectLoopAsync()
    {
        try
        {
            while (!_stopped && AutoReconnect && _reconnectAttempt < MaxReconnectAttempts)
            {
                _reconnectAttempt++;
                SetState(DesktopSessionState.Reconnecting);

                // 2, 3, 5, 8, 12, puis 15 s.
                int delay = _reconnectAttempt switch
                {
                    1 => 2000,
                    2 => 3000,
                    3 => 5000,
                    4 => 8000,
                    5 => 12000,
                    _ => 15000,
                };
                await Task.Delay(delay).ConfigureAwait(false);
                if (_stopped || !AutoReconnect) return;

                try
                {
                    var socket = await _connection
                        .OpenTunnelAsync(_node.Id, RelayProtocol.Desktop)
                        .ConfigureAwait(false);

                    if (_stopped) { socket.Close(); socket.Dispose(); return; }

                    // Les boucles du tunnel précédent (envoi, mesure du délai)
                    // sont arrêtées et leurs ressources libérées : sans cela,
                    // la boucle de mesure tournait indéfiniment, même après la
                    // fermeture, et retenait la session et son image en mémoire.
                    var previousCts = _cts;
                    var previousSocket = _socket;
                    try { previousCts?.Cancel(); } catch { }
                    previousCts?.Dispose();
                    try { previousSocket?.Dispose(); } catch { }

                    // Nouvelle file : celle de la session précédente a été
                    // complétée, elle rejetterait toute écriture.
                    _sendQueue = NewSendQueue();
                    _socket = socket;
                    _cts = new CancellationTokenSource();

                    _reconnectAttempt = 0;
                    CloseReason = null;
                    SetState(DesktopSessionState.Connected);

                    StartSenderLoop(socket, _cts.Token);
                    StartReceiveLoop(socket, _cts.Token);
                    StartRtt(_cts.Token);

                    // La résolution a pu changer pendant la coupure ; les réglages
                    // de compression ne survivent pas au nouveau tunnel. Les
                    // premières tailles annoncées peuvent être à l'échelle par
                    // défaut de l'agent : elles ne servent pas d'estimation.
                    _scalingChangedAt = Stopwatch.GetTimestamp();
                    SendCompressionSettings();
                    SendCommand(Kvm.Refresh);
                    return;
                }
                catch (Exception ex)
                {
                    CloseReason = ex.Message;   // on repart pour un tour
                }
            }

            // Tentatives épuisées : la session reste fermée.
            if (!_stopped)
            {
                CloseReason = "Reconnexion impossible après "
                            + $"{MaxReconnectAttempts} tentatives.";
                SetState(DesktopSessionState.Closed);
            }
        }
        finally
        {
            _reconnecting = false;
        }
    }

    /// <summary>Relance immédiatement une tentative (bouton « Reconnecter »).</summary>
    public void RetryNow()
    {
        if (_stopped) return;
        _reconnectAttempt = 0;
        AutoReconnect = true;
        TryBeginReconnect();
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        // Fermeture volontaire : surtout ne pas relancer de reconnexion.
        AutoReconnect = false;
        SendControl(new { ctrlChannel = "102938", type = "close" });
        try { _cts?.Cancel(); } catch { }
        _sendQueue.Writer.TryComplete();
        _socket?.Close();
        _socket?.Dispose();
        _socket = null;
        DiscardPendingUpdates();
        if (State != DesktopSessionState.Closed)
        {
            CloseReason ??= null;
            SetState(DesktopSessionState.Closed);
        }
    }

    /// <summary>Pause/reprise du flux (au passage en arrière-plan).</summary>
    public void SetPaused(bool paused)
    {
        SendCommand(Kvm.Pause, new byte[] { (byte)(paused ? 1 : 0) });
        if (!paused) SendCommand(Kvm.Refresh);
    }

    // MARK: - Envoi

    private void StartSenderLoop(MeshWebSocket socket, CancellationToken ct)
    {
        _senderTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        if (item.IsText)
                            await socket.SendAsync(Encoding.UTF8.GetString(item.Data), ct).ConfigureAwait(false);
                        else
                            await socket.SendAsync(item.Data, ct).ConfigureAwait(false);
                    }
                    catch { break; }
                }
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    private void SendCommand(ushort command, byte[]? payload = null)
    {
        // Un déplacement encore en attente part d'abord : un clic ou une frappe
        // ne doit pas devancer le mouvement qui amène le curseur à sa place.
        FlushPendingMove();
        EnqueueCommand(command, payload);
    }

    private void EnqueueCommand(ushort command, byte[]? payload = null)
    {
        payload ??= Array.Empty<byte>();
        var data = new byte[payload.Length + 4];
        data[0] = (byte)(command >> 8);
        data[1] = (byte)(command & 0xFF);
        int total = payload.Length + 4;
        data[2] = (byte)(total >> 8);
        data[3] = (byte)(total & 0xFF);
        Array.Copy(payload, 0, data, 4, payload.Length);
        _sendQueue.Writer.TryWrite(new Outbound(data, IsText: false)); // commande KVM : binaire
    }

    private void SendControl(object obj)
    {
        var text = JsonSerializer.Serialize(obj);
        // Trames de contrôle en TEXTE (le serveur les distingue du flux binaire KVM).
        _sendQueue.Writer.TryWrite(new Outbound(Encoding.UTF8.GetBytes(text), IsText: true));
    }

    private void StartRtt(CancellationToken ct)
    {
        _rttTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                SendControl(new { ctrlChannel = 102938, type = "rtt",
                                  time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                // Toutes les 5 s : assez souvent pour repérer une liaison
                // saturée en une dizaine de secondes, pour un coût négligeable.
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
                catch { return; }
            }
        }, ct);
    }

    // MARK: - Encodage des entrées

    // MARK: - Déplacements regroupés

    /// <summary>
    /// Intervalle minimal entre deux déplacements transmis, soit une soixantaine
    /// par seconde — au-delà de la cadence maximale de l'image, pour que le
    /// curseur distant ne soit jamais en retard sur elle. Une souris rapide produit
    /// plusieurs centaines d'événements par seconde : les relayer tous chargeait
    /// le relais et l'agent, et le curseur distant prenait du retard.
    /// </summary>
    private const double MouseMoveIntervalMs = 16;

    private readonly object _moveLock = new();
    private bool _movePending;
    private bool _moveFlushScheduled;
    private ushort _pendingMoveX, _pendingMoveY;
    private long _lastMoveSent;

    /// <summary>
    /// Déplacement de souris. Transmis aussitôt si le précédent est assez
    /// ancien ; sinon seule la dernière position est retenue, et part à la fin
    /// de l'intervalle — ou plus tôt, devant le prochain clic ou la prochaine
    /// frappe (voir <see cref="SendCommand"/>).
    /// </summary>
    public void SendMouseMove(int x, int y)
    {
        double wait;
        lock (_moveLock)
        {
            _pendingMoveX = (ushort)Math.Max(0, Math.Min(x, ScreenWidth));
            _pendingMoveY = (ushort)Math.Max(0, Math.Min(y, ScreenHeight));
            _lastMouseX = _pendingMoveX;
            _lastMouseY = _pendingMoveY;
            _movePending = true;

            // Un envoi est déjà programmé : il prendra cette position.
            if (_moveFlushScheduled) return;

            wait = MouseMoveIntervalMs - Stopwatch.GetElapsedTime(_lastMoveSent).TotalMilliseconds;
            if (wait <= 0)
            {
                FlushMoveLocked();
                return;
            }
            _moveFlushScheduled = true;
        }
        _ = FlushMoveLaterAsync(wait);
    }

    private async Task FlushMoveLaterAsync(double delayMs)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(delayMs)).ConfigureAwait(false);
        lock (_moveLock)
        {
            _moveFlushScheduled = false;
            FlushMoveLocked();
        }
    }

    private void FlushPendingMove()
    {
        lock (_moveLock) FlushMoveLocked();
    }

    /// <summary>Transmet le déplacement en attente, s'il y en a un. Sous _moveLock.</summary>
    private void FlushMoveLocked()
    {
        if (!_movePending) return;
        _movePending = false;
        _lastMoveSent = Stopwatch.GetTimestamp();
        EnqueueCommand(Kvm.Mouse, MousePayload(0, _pendingMoveX, _pendingMoveY));
    }

    private static byte[] MousePayload(byte button, ushort x, ushort y) =>
        new byte[] { 0, button, (byte)(x >> 8), (byte)x, (byte)(y >> 8), (byte)y };

    public void SendMouseButton(MouseButton button, bool down, int x, int y)
        => SendMouse(down ? DownFlag(button) : UpFlag(button), x, y);

    public void SendClick(MouseButton button, int x, int y)
    {
        SendMouse(0, x, y);
        SendMouse(DownFlag(button), x, y);
        SendMouse(UpFlag(button), x, y);
    }

    public void SendDoubleClick(int x, int y)
    {
        SendClick(MouseButton.Left, x, y);
        SendMouse(0x88, x, y);
    }

    private static byte DownFlag(MouseButton b) => b switch
    {
        MouseButton.Left => 0x02,
        MouseButton.Right => 0x08,
        MouseButton.Middle => 0x20,
        _ => 0x02,
    };
    private static byte UpFlag(MouseButton b) => (byte)(DownFlag(b) << 1);

    // Dernière position transmise : un relâchement de secours doit se faire là
    // où le curseur distant se trouve, et non le téléporter dans un coin.
    private ushort _lastMouseX, _lastMouseY;

    private void SendMouse(byte button, int x, int y)
    {
        var cx = (ushort)Math.Max(0, Math.Min(x, ScreenWidth));
        var cy = (ushort)Math.Max(0, Math.Min(y, ScreenHeight));
        _lastMouseX = cx;
        _lastMouseY = cy;
        SendCommand(Kvm.Mouse, MousePayload(button, cx, cy));
    }

    /// <summary>
    /// Relâche les trois boutons à la dernière position connue. Filet de
    /// sécurité quand le relâchement réel ne nous parvient pas — capture de
    /// souris perdue, fenêtre désactivée en plein glisser : la machine distante
    /// garderait sinon le bouton enfoncé, et tout ce qu'elle touche suivrait le
    /// curseur jusqu'au prochain clic.
    /// </summary>
    public void ReleaseMouseButtons()
    {
        foreach (var button in new[] { MouseButton.Left, MouseButton.Right, MouseButton.Middle })
            SendMouse(UpFlag(button), _lastMouseX, _lastMouseY);
    }

    /// <summary>Molette : delta positif = vers le haut. Multiple de 120.</summary>
    public void SendScroll(int delta, int x, int y)
    {
        var cx = (ushort)Math.Max(0, Math.Min(x, ScreenWidth));
        var cy = (ushort)Math.Max(0, Math.Min(y, ScreenHeight));
        var payload = new List<byte> { 0, 0 };
        payload.AppendBE(cx);
        payload.AppendBE(cy);
        payload.AppendBE(unchecked((ushort)(short)Math.Clamp(delta, short.MinValue, short.MaxValue)));
        SendCommand(Kvm.Mouse, payload.ToArray());
    }

    /// <summary>Caractère imprimable via le chemin unicode (indépendant du clavier).</summary>
    public void SendUnicode(int codepoint)
    {
        foreach (byte action in new byte[] { 0, 1 })
        {
            var payload = new List<byte> { action };
            payload.AppendBE((ushort)Math.Clamp(codepoint, 0, ushort.MaxValue));
            SendCommand(Kvm.UnicodeKey, payload.ToArray());
        }
    }

    /// <summary>Touche virtuelle (codes VK Windows).</summary>
    public void SendKey(byte keyCode, KeyAction action)
        => SendCommand(Kvm.Key, new byte[] { (byte)action, keyCode });

    public void SendKeyTap(byte keyCode, bool extended = false)
    {
        SendKey(keyCode, extended ? KeyAction.ExtendedDown : KeyAction.Down);
        SendKey(keyCode, extended ? KeyAction.ExtendedUp : KeyAction.Up);
    }

    /// <summary>
    /// Envoie un texte caractère par caractère via le chemin unicode. C'est la
    /// méthode fiable pour « coller » : elle ne dépend pas de la disposition
    /// clavier de la machine distante, donc les accents passent correctement.
    /// </summary>
    public async Task SendTextAsync(string text, CancellationToken ct = default)
    {
        // Aucun modificateur ne doit être actif : sinon chaque caractère serait
        // interprété comme un raccourci par la machine distante.
        ReleaseModifiers();

        foreach (var rune in text.EnumerateRunes())
        {
            if (ct.IsCancellationRequested) return;

            // Les sauts de ligne n'ont pas de codepoint exploitable côté agent :
            // on les traduit en appui sur Entrée (VK 13).
            if (rune.Value is '\n')
            {
                SendKeyTap(13);
            }
            else if (rune.Value is '\r')
            {
                continue;   // ignoré : \r\n ne doit produire qu'un seul Entrée
            }
            else if (rune.Value is '\t')
            {
                SendKeyTap(9);
            }
            else if (rune.Value <= 0xFFFF)
            {
                SendUnicode(rune.Value);
            }
            // Au-delà du plan multilingue de base (emoji…), la commande unicode
            // du protocole ne transporte qu'un u16 : ces caractères sont ignorés.

            // Petite pause : sans elle, l'agent perd des caractères sur les
            // textes longs (la file d'entrée distante sature).
            await Task.Delay(6, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Envoie une combinaison de touches (modificateurs maintenus pendant l'appui).
    /// Les codes sont des VK Windows ; l'agent Linux les traduit de son côté.
    /// </summary>
    public void SendChord(IReadOnlyList<byte> modifiers, byte key, bool extendedKey = false)
    {
        foreach (var m in modifiers) SendKey(m, KeyAction.Down);
        SendKey(key, extendedKey ? KeyAction.ExtendedDown : KeyAction.Down);
        SendKey(key, extendedKey ? KeyAction.ExtendedUp : KeyAction.Up);
        // Relâchement en ordre inverse, comme le ferait un clavier physique.
        for (int i = modifiers.Count - 1; i >= 0; i--) SendKey(modifiers[i], KeyAction.Up);
        // Filet de sécurité : si un modificateur restait enfoncé côté distant,
        // toutes les frappes suivantes deviendraient des raccourcis.
        ReleaseModifiers();
    }

    /// <summary>
    /// Force le relâchement des modificateurs (Maj, Ctrl, Alt, Win gauche/droite)
    /// sur la machine distante. Le client web fait la même chose après connexion :
    /// un modificateur resté « enfoncé » rend le clavier distant inutilisable.
    /// </summary>
    public void ReleaseModifiers()
    {
        foreach (byte vk in new byte[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            SendKey(vk, KeyAction.Up);
    }

    public void SendCtrlAltDel() => SendCommand(Kvm.CtrlAltDel);
    public void RequestRefresh() => SendCommand(Kvm.Refresh);

    // MARK: - Mode dépannage : image nette à la demande

    private bool _sharpActive;
    private int _sharpGeneration;
    private long _lastTileAt;

    /// <summary>Vrai pendant la réception d'une image nette.</summary>
    public bool IsSharpFrameActive => _sharpActive;

    /// <summary>
    /// Mode dépannage : demande une image complète en bonne qualité, puis
    /// revient à la compression maximale. L'agent n'envoyant ensuite que les
    /// zones qui changent, le reste de l'écran demeure net, et le texte
    /// lisible, jusqu'à ce qu'il change. Hors de ce mode, simple
    /// rafraîchissement.
    /// </summary>
    public void RequestSharpFrame()
    {
        if (!IsTroubleshootMode)
        {
            RequestRefresh();
            return;
        }

        var generation = Interlocked.Increment(ref _sharpGeneration);
        _sharpActive = true;
        SendCompressionSettings();
        SendCommand(Kvm.Refresh);
        _ = EndSharpFrameAsync(generation);
    }

    /// <summary>
    /// Rétablit la compression maximale une fois l'image nette reçue : quand
    /// les tuiles cessent d'arriver, l'agent a fini de l'encoder. Si l'écran
    /// change sans cesse (vidéo, animation), les tuiles ne s'arrêtent jamais :
    /// on revient alors au bout de 8 secondes.
    /// </summary>
    private async Task EndSharpFrameAsync(int generation)
    {
        var start = Stopwatch.GetTimestamp();
        var tilesAtStart = TilesReceived;
        try
        {
            await Task.Delay(500).ConfigureAwait(false);
            while (Volatile.Read(ref _sharpGeneration) == generation && !_stopped)
            {
                var quietMs = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastTileAt)).TotalMilliseconds;
                if (Stopwatch.GetElapsedTime(start).TotalSeconds > 8
                    || (TilesReceived > tilesAtStart && quietMs > 400))
                    break;
                await Task.Delay(100).ConfigureAwait(false);
            }
        }
        catch { /* arrêt : rien à rétablir */ }

        if (Volatile.Read(ref _sharpGeneration) != generation || _stopped) return;
        _sharpActive = false;
        SendCompressionSettings();
    }

    // MARK: - Mode dépannage : échelle adaptée et écran unique

    /// <summary>Délai pendant lequel une taille d'écran annoncée peut encore refléter l'ancienne échelle.</summary>
    private const double ScalingSettleMs = 1500;

    /// <summary>Dernière échelle envoyée à l'agent (1024 = 100 %), et quand elle a changé.</summary>
    private ushort _scalingSent = 1024;
    private long _scalingChangedAt = Stopwatch.GetTimestamp();

    /// <summary>Largeur réelle de l'image affichée, déduite d'une taille d'écran annoncée. 0 si inconnue.</summary>
    private int _estimatedNativeWidth;

    private int _probeScheduled;

    /// <summary>
    /// Largeur réelle de ce que l'agent capture : d'après la géométrie des
    /// écrans quand l'agent la fournit (Windows), sinon d'après la dernière
    /// taille d'écran annoncée. 0 si inconnue.
    /// </summary>
    private int NativeWidth()
    {
        var screens = _displayList
            .Where(d => d.HasGeometry && d.Id != RemoteDisplay.AllDisplays)
            .ToList();
        if (screens.Count > 0)
        {
            if (SelectedDisplay == RemoteDisplay.AllDisplays)
                return screens.Max(d => d.X + d.Width) - screens.Min(d => d.X);
            if (screens.FirstOrDefault(d => d.Id == SelectedDisplay) is { } current)
                return current.Width;
            if (screens.Count == 1)
                return screens[0].Width;
        }
        return _estimatedNativeWidth;
    }

    /// <summary>
    /// Recalcule l'échelle du mode dépannage. Si elle change, l'agent reçoit
    /// la nouvelle échelle et renvoie une image complète.
    /// </summary>
    private void UpdateTroubleshootScale()
    {
        var before = EffectiveScalePercent;
        if (!RecomputeTroubleshootScale())
        {
            ProbeNativeWidthLater();
            return;
        }
        if (!IsTroubleshootMode || EffectiveScalePercent == before) return;

        SendCompressionSettings();
        SendCommand(Kvm.Refresh);
    }

    /// <summary>
    /// Met à jour l'échelle du mode dépannage sans rien envoyer. Renvoie faux
    /// si la largeur réelle est inconnue.
    /// </summary>
    private bool RecomputeTroubleshootScale()
    {
        var native = NativeWidth();
        if (native <= 0) return false;

        var fit = Math.Clamp(TroubleshootTargetWidth * 100 / native, 25, TroubleshootMaxScale);
        if (Math.Abs(fit - _troubleshootScale) >= 5) _troubleshootScale = fit;
        return true;
    }

    /// <summary>
    /// Taille réelle encore inconnue en mode dépannage : une fois l'échelle
    /// stabilisée, un rafraîchissement fait annoncer à l'agent une taille
    /// d'écran fiable, d'où l'on déduit la largeur réelle.
    /// </summary>
    private void ProbeNativeWidthLater()
    {
        if (!IsTroubleshootMode || NativeWidth() > 0) return;
        if (Interlocked.Exchange(ref _probeScheduled, 1) != 0) return;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(ScalingSettleMs + 500)).ConfigureAwait(false); }
            finally { Volatile.Write(ref _probeScheduled, 0); }
            if (IsTroubleshootMode && !_stopped && State == DesktopSessionState.Connected
                && NativeWidth() <= 0)
                SendCommand(Kvm.Refresh);
        });
    }

    /// <summary>
    /// Écran affiché avant que le mode dépannage ne passe à un écran unique ;
    /// rétabli en quittant le mode, sauf si l'utilisateur a choisi un écran
    /// entre-temps.
    /// </summary>
    private int? _displayBeforeTroubleshoot;

    /// <summary>
    /// En mode dépannage, la vue « tous les écrans » d'un poste à plusieurs
    /// moniteurs est remplacée par le premier écran : une image deux ou trois
    /// fois plus large, c'est autant de données en plus.
    /// </summary>
    private void EnsureSingleDisplay()
    {
        if (!IsTroubleshootMode || SelectedDisplay != RemoteDisplay.AllDisplays) return;

        var real = _displays.Where(id => id != RemoteDisplay.AllDisplays).ToList();
        if (real.Count < 2) return;

        _displayBeforeTroubleshoot = RemoteDisplay.AllDisplays;
        ApplyDisplay(real.Min());
        _uiDispatcher.BeginInvoke(() => DisplaysChanged?.Invoke());
    }

    private void RestoreDisplayAfterTroubleshoot()
    {
        if (_displayBeforeTroubleshoot is not { } previous) return;
        _displayBeforeTroubleshoot = null;
        ApplyDisplay(previous);
        _uiDispatcher.BeginInvoke(() => DisplaysChanged?.Invoke());
    }

    // MARK: - Liaison lente

    /// <summary>
    /// Latence jugée trop forte pour un affichage normal. Le message « rtt »
    /// passe dans le même tunnel que les images : quand la liaison sature, il
    /// attend derrière elles, et sa latence grimpe bien au-delà de celle du
    /// réseau. C'est précisément ce qu'on cherche à repérer.
    /// </summary>
    private const int SlowLinkLatencyMs = 1000;

    private int _slowSamples;
    private bool _slowLinkSignaled;

    /// <summary>
    /// Deux mesures de latence d'affilée au-dessus du seuil, hors mode
    /// dépannage. Signalé une seule fois par session.
    /// </summary>
    public event Action? SlowLinkDetected;

    private void TrackLatency(int latencyMs)
    {
        if (IsTroubleshootMode || _slowLinkSignaled)
        {
            _slowSamples = 0;
            return;
        }

        _slowSamples = latencyMs >= SlowLinkLatencyMs ? _slowSamples + 1 : 0;
        if (_slowSamples < 2) return;

        _slowLinkSignaled = true;
        _uiDispatcher.BeginInvoke(() => SlowLinkDetected?.Invoke());
    }

    /// <summary>
    /// Horodatage du dernier choix d'écran fait par l'utilisateur. Changer
    /// d'écran modifie la résolution, ce qui relance un GetDisplays dont la
    /// réponse peut encore porter l'ancien « selected » ; sans cette garde, le
    /// sélecteur reculerait alors que l'image affiche déjà le bon écran.
    /// </summary>
    private DateTime _displayChosenAt = DateTime.MinValue;

    public void SelectDisplay(int display)
    {
        // Choix explicite : il l'emporte sur l'écran que le mode dépannage
        // aurait rétabli en sortant.
        _displayBeforeTroubleshoot = null;
        ApplyDisplay(display);
    }

    private void ApplyDisplay(int display)
    {
        SelectedDisplay = display;
        _displayChosenAt = DateTime.UtcNow;

        var payload = new List<byte>();
        payload.AppendBE((ushort)display);
        SendCommand(Kvm.SetDisplay, payload.ToArray());
        SendCommand(Kvm.Refresh);
    }

    /// <summary>
    /// Échelle d'encodage envoyée à l'agent, en pourcentage (100 = taille réelle).
    /// C'est le levier le plus efficace sur la bande passante : à 50 %, l'agent
    /// encode quatre fois moins de pixels, à la source.
    ///
    /// L'agent réduit l'image ET le repère de coordonnées : les tuiles arrivent
    /// aux dimensions réduites et la souris doit être exprimée dans ce même
    /// repère. C'est ScreenWidth/Height qui suivent, la conversion se faisant
    /// naturellement côté interface.
    /// </summary>
    private int _scalePercent = 100;
    public int ScalePercent
    {
        get => _scalePercent;
        set
        {
            var clamped = Math.Clamp(value, 25, 100);
            if (clamped == _scalePercent) return;
            var effectiveBefore = EffectiveScalePercent;
            _scalePercent = clamped;
            if (EffectiveScalePercent == effectiveBefore) return;
            SendCompressionSettings();
            // L'agent renvoie un nouveau « screen size » à l'échelle demandée ;
            // ce rafraîchissement force la reconstruction complète de l'image.
            SendCommand(Kvm.Refresh);
        }
    }

    private void SendCompressionSettings()
    {
        // La qualité (compression JPEG) et le taux de rafraîchissement sont
        // réglables indépendamment : on peut vouloir une image nette peu
        // rafraîchie, ou l'inverse selon la bande passante.
        // Mode dépannage : qualité JPEG 5, le plancher utile (en dessous, le
        // texte devient illisible sans réel gain de taille). L'agent ne sait
        // pas envoyer en niveaux de gris : la capture est toujours en couleur,
        // seuls la compression, l'échelle et la cadence réduisent le débit.
        // Mode fluide : qualité 65 à 40 img/s, pour suivre une vidéo sans
        // flou marqué. Image nette (mode dépannage) : qualité 60, le temps
        // d'une image complète.
        byte quality = _quality switch
        {
            DesktopQuality.Troubleshoot => _sharpActive ? SharpQuality : (byte)5,
            DesktopQuality.Fluid => FluidQuality,
            DesktopQuality.Low => 15,
            DesktopQuality.High => 80,
            _ => 50,
        };

        // 1024 = 100 %. L'agent attend cette unité, pas un pourcentage.
        ushort scaling = (ushort)(1024 * EffectiveScalePercent / 100);
        if (scaling != _scalingSent)
        {
            _scalingSent = scaling;
            _scalingChangedAt = Stopwatch.GetTimestamp();
        }

        var frameTimer = EffectiveFrameTimerMs;

        // Type 1 = JPEG. Le type 4 (WebP) existe côté protocole mais aucun agent
        // MeshCentral ne l'implémente : Linux et macOS ignorent le champ, et
        // Windows n'a pas de branche pour 4 et reste sur JPEG. L'envoyer serait
        // sans effet.
        var payload = new List<byte> { 1, quality };
        payload.AppendBE(scaling);
        payload.AppendBE(frameTimer);
        SendCommand(Kvm.Compression, payload.ToArray());
    }

    // MARK: - Réception / décodage

    private void StartReceiveLoop(MeshWebSocket socket, CancellationToken ct)
    {
        _receiveTask = Task.Run(async () =>
        {
            var buffer = new List<byte>();
            while (!ct.IsCancellationRequested)
            {
                WsMessage message;
                try { message = await socket.ReceiveAsync(ct).ConfigureAwait(false); }
                catch
                {
                    if (State != DesktopSessionState.Closed)
                    {
                        CloseReason = "La session de bureau à distance s'est terminée.";
                        // Coupure non demandée (redémarrage du poste, réseau) :
                        // on tente de reprendre plutôt que de rester fermé.
                        if (!TryBeginReconnect()) SetState(DesktopSessionState.Closed);
                    }
                    return;
                }

                // Protection globale : un défaut de décodage ne doit jamais
                // terminer le processus. On journalise et on continue.
                try
                {
                    switch (message)
                    {
                        case WsMessage.Text(var text):
                            HandleControl(text);
                            break;
                        case WsMessage.Binary(var data):
                            BytesReceived += data.Length;
                            AccumulateThroughput(data.Length);
                            buffer.AddRange(data);
                            DrainCommands(buffer);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"Décodage ignoré : {ex.Message}", "KVM");
                }
            }
        }, ct);
    }

    /// <summary>
    /// Débit sur une fenêtre glissante d'une seconde. Un cumul depuis le début de
    /// session donnerait une moyenne qui masquerait les ralentissements ponctuels.
    /// </summary>
    private void AccumulateThroughput(int bytes)
    {
        _windowBytes += bytes;
        var span = (DateTime.UtcNow - _windowStart).TotalSeconds;
        if (span >= 1.0)
        {
            ThroughputKbps = _windowBytes / span / 1024.0;
            _windowBytes = 0;
            _windowStart = DateTime.UtcNow;
        }
    }

    private void HandleControl(string text)
    {
        if (text.Length == 0 || text[0] != '{') return;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            if (type == "ping")
                SendControl(new { ctrlChannel = "102938", type = "pong" });
            else if (type == "rtt" && root.TryGetProperty("time", out var sent)
                     && sent.ValueKind == JsonValueKind.Number)
            {
                // L'agent renvoie l'horodatage que nous avions émis : la
                // différence donne l'aller-retour complet.
                var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sent.GetInt64();
                if (elapsed >= 0 && elapsed < 60000)
                {
                    LatencyMs = (int)elapsed;
                    TrackLatency((int)elapsed);
                }
            }
            else if (type == "console" && root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var msg = m.GetString();
                RemoteMessage = string.IsNullOrEmpty(msg) ? null : msg;
            }
        }
        catch { /* trame de contrôle illisible : ignorée */ }
    }

    // MARK: - Application des mises à jour sur le thread UI

    /// <summary>
    /// Mise à jour en attente pour le thread UI : une tuile décodée, ou un
    /// changement de résolution quand elle ne porte pas de tuile.
    /// </summary>
    private readonly record struct PendingUpdate(DecodedTile Tile, int ResizeWidth, int ResizeHeight)
    {
        // Sur la tuile et non sur les dimensions : une résolution nulle
        // annoncée par l'agent ne doit pas passer pour une tuile sans pixels.
        public bool IsResize => Tile.Pixels is null;
    }

    private readonly ConcurrentQueue<PendingUpdate> _pendingUpdates = new();

    /// <summary>1 tant qu'un passage sur le thread UI est déjà programmé.</summary>
    private int _flushScheduled;

    /// <summary>Lot de tuiles en cours d'application (thread UI uniquement).</summary>
    private readonly List<DecodedTile> _tileBatch = new();

    /// <summary>
    /// Met une mise à jour en file et programme au besoin un passage sur le
    /// thread UI. Un seul passage à la fois : les tuiles arrivées entre-temps
    /// sont appliquées ensemble, au lieu d'une opération du dispatcher chacune
    /// — sous un rafraîchissement complet, c'étaient des centaines d'opérations
    /// qui retardaient le reste de l'interface, saisie comprise.
    /// </summary>
    private void PostUpdate(PendingUpdate update)
    {
        _pendingUpdates.Enqueue(update);
        if (Interlocked.Exchange(ref _flushScheduled, 1) != 0) return;

        // Si l'UI n'exécute jamais ce passage (arrêt du dispatcher à la
        // fermeture), les tampons loués sont rendus ici.
        var operation = _uiDispatcher.BeginInvoke(() => FlushUpdates());
        operation.Aborted += (_, _) => DiscardPendingUpdates();
    }

    private void FlushUpdates()
    {
        // Remis à zéro AVANT de vider : une tuile arrivée pendant le vidage
        // programme ainsi un nouveau passage au lieu de rester en file.
        Volatile.Write(ref _flushScheduled, 0);

        bool resized = false;
        bool drawn = false;
        while (_pendingUpdates.TryDequeue(out var update))
        {
            if (!update.IsResize)
            {
                _tileBatch.Add(update.Tile);
                continue;
            }

            drawn |= ApplyTileBatch();
            try
            {
                Framebuffer.Resize(update.ResizeWidth, update.ResizeHeight);
                resized = true;
            }
            catch { /* dimensions aberrantes : ignorées, un prochain screen-size corrigera */ }
        }
        drawn |= ApplyTileBatch();

        if (resized) ScreenSizeChanged?.Invoke();
        if (drawn) FrameUpdated?.Invoke();
    }

    /// <summary>Applique le lot accumulé ; renvoie vrai s'il contenait des tuiles.</summary>
    private bool ApplyTileBatch()
    {
        if (_tileBatch.Count == 0) return false;
        try { Framebuffer.Apply(_tileBatch); }
        catch { /* tuiles ignorées : les suivantes rafraîchiront la zone */ }
        finally { _tileBatch.Clear(); }
        return true;
    }

    /// <summary>Vide la file sans rien dessiner, en rendant les tampons loués.</summary>
    private void DiscardPendingUpdates()
    {
        while (_pendingUpdates.TryDequeue(out var update))
            if (!update.IsResize) ArrayPool<byte>.Shared.Return(update.Tile.Pixels);
    }

    /// <summary>
    /// Plus grande trame « jumbo » acceptée : une tuile d'écran 8K en PNG reste
    /// très en deçà.
    /// </summary>
    private const uint MaxJumboSize = 64 * 1024 * 1024;

    /// <summary>Extrait les commandes complètes du buffer de réassemblage.</summary>
    private void DrainCommands(List<byte> buffer)
    {
        while (buffer.Count >= 4)
        {
            ushort command = (ushort)((buffer[0] << 8) | buffer[1]);
            ushort sizeField = (ushort)((buffer[2] << 8) | buffer[3]);
            int totalLength = sizeField;
            byte[] body;

            if (command == Kvm.Jumbo && sizeField == 8)
            {
                if (buffer.Count < 8) return;
                uint realSize = ((uint)buffer[4] << 24) | ((uint)buffer[5] << 16)
                              | ((uint)buffer[6] << 8) | buffer[7];
                // Taille aberrante : la trame est malformée, et l'attendre ferait
                // grossir le tampon sans fin. On abandonne ce qui est en attente.
                if (realSize < 2 || realSize > MaxJumboSize)
                {
                    Log.Warning($"Trame KVM de {realSize} octets ignorée (malformée).", "Bureau");
                    buffer.Clear();
                    return;
                }
                totalLength = 8 + (int)realSize;
                if (buffer.Count < totalLength) return;
                body = new byte[totalLength - 8];
                buffer.CopyTo(8, body, 0, totalLength - 8);
                command = body.Length >= 2 ? (ushort)((body[0] << 8) | body[1]) : (ushort)0;
            }
            else
            {
                if (totalLength < 4) { buffer.Clear(); return; } // malformé : on jette
                if (buffer.Count < totalLength) return;
                body = new byte[totalLength];
                buffer.CopyTo(0, body, 0, totalLength);
            }

            Process(command, body);
            buffer.RemoveRange(0, totalLength);
        }
    }

    private void Process(ushort command, byte[] body)
    {
        switch (command)
        {
            case Kvm.Tile:
            {
                var x = body.BeUInt16(4);
                var y = body.BeUInt16(6);
                if (x is null || y is null || body.Length <= 8) return;
                TilesReceived++;
                Volatile.Write(ref _lastTileAt, Stopwatch.GetTimestamp());
                var imageData = new byte[body.Length - 8];
                Array.Copy(body, 8, imageData, 0, imageData.Length);

                // DÉCODAGE JPEG ici, sur le thread de fond (on y est déjà) : c'est
                // l'opération coûteuse, on la sort du thread UI. Seule la copie de
                // pixels repart sur l'UI, où elle est rapide.
                var decoded = KVMFramebuffer.Decode(imageData, x.Value, y.Value);
                if (decoded is null) break; // tuile illisible : la prochaine corrigera

                PostUpdate(new PendingUpdate(decoded.Value, 0, 0));
                break;
            }
            case Kvm.Screen:
            {
                var w = body.BeUInt16(4);
                var h = body.BeUInt16(6);
                if (w is null || h is null) return;
                HandleScreenSize(w.Value, h.Value);
                break;
            }
            case Kvm.GetDisplays:
            {
                var count = body.BeUInt16(4);
                if (count is null) return;
                _displays.Clear();
                for (int i = 0; i < count.Value; i++)
                    if (body.BeUInt16(6 + i * 2) is { } id) _displays.Add(id);
                // Le choix récent de l'utilisateur l'emporte : la réponse peut
                // avoir été émise avant que l'agent ne traite notre SetDisplay.
                if (body.BeUInt16(6 + count.Value * 2) is { } sel
                    && (DateTime.UtcNow - _displayChosenAt).TotalSeconds > 3)
                    SelectedDisplay = sel;

                // La géométrie arrive séparément (commande 82) et parfois jamais :
                // on publie d'abord les écrans sans dimensions, pour que l'UI soit
                // utilisable même si l'agent ne l'envoie pas.
                var known = _displayList.ToDictionary(d => d.Id);
                _displayList = _displays
                    .Select(id => known.TryGetValue(id, out var prev)
                        ? prev
                        : new RemoteDisplay(id, 0, 0, 0, 0))
                    .ToList();

                EnsureSingleDisplay();
                UpdateTroubleshootScale();
                _uiDispatcher.BeginInvoke(() => DisplaysChanged?.Invoke());
                break;
            }
            case Kvm.DisplayGeometry:
            {
                // Enregistrements de 10 octets : id, x, y, w, h (u16 chacun).
                var records = (body.Length - 4) / 10;
                if (records <= 0) return;

                var geometry = new List<RemoteDisplay>(records);
                for (int i = 0; i < records; i++)
                {
                    int off = 4 + i * 10;
                    if (body.BeUInt16(off) is not { } id
                        || body.BeUInt16(off + 2) is not { } x
                        || body.BeUInt16(off + 4) is not { } y
                        || body.BeUInt16(off + 6) is not { } w
                        || body.BeUInt16(off + 8) is not { } h) continue;
                    geometry.Add(new RemoteDisplay(id, x, y, w, h));
                }
                if (geometry.Count == 0) return;

                // La géométrie peut précéder la réponse GetDisplays : dans ce cas
                // elle fait autorité et fournit à elle seule la liste des écrans.
                _displayList = geometry;
                if (_displays.Count == 0)
                    _displays.AddRange(geometry.Select(d => d.Id));

                UpdateTroubleshootScale();
                _uiDispatcher.BeginInvoke(() => DisplaysChanged?.Invoke());
                break;
            }
            case Kvm.Message:
            case Kvm.Alert:
            {
                if (body.Length > 4)
                {
                    var text = Encoding.UTF8.GetString(body, 4, body.Length - 4);
                    if (!text.StartsWith(".")) RemoteMessage = text;
                }
                break;
            }
        }
    }

    private void HandleScreenSize(int w, int h)
    {
        ScreenWidth = w;
        ScreenHeight = h;

        // Largeur réelle déduite de l'échelle en vigueur, seulement si elle
        // n'a pas changé récemment : juste après un changement, l'agent peut
        // encore annoncer une taille calculée sur l'ancienne échelle.
        if (w > 0 && Stopwatch.GetElapsedTime(_scalingChangedAt).TotalMilliseconds > ScalingSettleMs)
            _estimatedNativeWidth = (int)Math.Round(w * 1024.0 / _scalingSent);
        UpdateTroubleshootScale();

        // Sans attendre l'interface, qui bloquerait le fil de réception : les
        // trames suivantes s'accumuleraient pendant ce temps.
        //
        // Le redimensionnement emprunte la file des tuiles : l'ordre est ainsi
        // garanti. Il s'applique après les tuiles qui le précèdent et avant
        // celles qui le suivent — indispensable, sinon elles seraient rognées
        // sur l'ancienne taille du framebuffer.
        PostUpdate(new PendingUpdate(default, w, h));

        // Reproduit la rafale du client web après screen-size.
        SendCompressionSettings();
        SendCommand(Kvm.Pause, new byte[] { 0 });
        SendCommand(Kvm.InputLock, new byte[] { 2 });
        foreach (byte keyCode in new byte[] { 16, 17, 18, 91, 92, 16 })
            SendKey(keyCode, KeyAction.Up);
        SendCommand(Kvm.TouchInit);
        SendCommand(Kvm.GetDisplays);
    }

    public void Dispose() => Stop();
}
