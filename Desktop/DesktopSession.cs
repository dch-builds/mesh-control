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

public enum DesktopQuality { Low, Balanced, High }

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
        set { _quality = value; SendCompressionSettings(); }
    }

    /// <summary>
    /// Intervalle entre deux trames côté agent, en millisecondes (100 ms = 10 img/s).
    /// Plus la valeur est basse, plus l'image est fluide — au prix de la bande passante.
    /// </summary>
    private ushort _frameTimerMs = 100;
    public int FrameRate
    {
        get => _frameTimerMs > 0 ? 1000 / _frameTimerMs : 10;
        set
        {
            var fps = Math.Clamp(value, 1, 60);
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
                    // de compression ne survivent pas au nouveau tunnel.
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
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
                catch { return; }
            }
        }, ct);
    }

    // MARK: - Encodage des entrées

    public void SendMouseMove(int x, int y) => SendMouse(0, x, y);

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
        var payload = new List<byte> { 0, button };
        payload.AppendBE(cx);
        payload.AppendBE(cy);
        SendCommand(Kvm.Mouse, payload.ToArray());
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

    /// <summary>
    /// Horodatage du dernier choix d'écran fait par l'utilisateur. Changer
    /// d'écran modifie la résolution, ce qui relance un GetDisplays dont la
    /// réponse peut encore porter l'ancien « selected » ; sans cette garde, le
    /// sélecteur reculerait alors que l'image affiche déjà le bon écran.
    /// </summary>
    private DateTime _displayChosenAt = DateTime.MinValue;

    public void SelectDisplay(int display)
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
            _scalePercent = clamped;
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
        byte quality = _quality switch
        {
            DesktopQuality.Low => 15,
            DesktopQuality.High => 80,
            _ => 50,
        };

        // 1024 = 100 %. L'agent attend cette unité, pas un pourcentage.
        ushort scaling = (ushort)(1024 * _scalePercent / 100);

        // Type 1 = JPEG. Le type 4 (WebP) existe côté protocole mais aucun agent
        // MeshCentral ne l'implémente : Linux et macOS ignorent le champ, et
        // Windows n'a pas de branche pour 4 et reste sur JPEG. L'envoyer serait
        // sans effet.
        var payload = new List<byte> { 1, quality };
        payload.AppendBE(scaling);
        payload.AppendBE(_frameTimerMs);
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
                if (elapsed >= 0 && elapsed < 60000) LatencyMs = (int)elapsed;
            }
            else if (type == "console" && root.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var msg = m.GetString();
                RemoteMessage = string.IsNullOrEmpty(msg) ? null : msg;
            }
        }
        catch { /* trame de contrôle illisible : ignorée */ }
    }

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
                var imageData = new byte[body.Length - 8];
                Array.Copy(body, 8, imageData, 0, imageData.Length);

                // DÉCODAGE JPEG ici, sur le thread de fond (on y est déjà) : c'est
                // l'opération coûteuse, on la sort du thread UI. Seule la copie de
                // pixels repart sur l'UI, où elle est rapide.
                var decoded = KVMFramebuffer.Decode(imageData, x.Value, y.Value);
                if (decoded is null) break; // tuile illisible : la prochaine corrigera

                // Apply rend le tampon loué au pool. Si l'UI n'exécute jamais ce
                // travail (arrêt du dispatcher à la fermeture), on le rend ici :
                // sans ce filet, chaque tuile en vol à la fermeture fuirait.
                var operation = _uiDispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        Framebuffer.Apply(decoded.Value);
                        FrameUpdated?.Invoke();
                    }
                    catch { /* tuile ignorée : la prochaine rafraîchira la zone */ }
                });
                operation.Aborted += (_, _) =>
                    System.Buffers.ArrayPool<byte>.Shared.Return(decoded.Value.Pixels);
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

        // BeginInvoke plutôt qu'Invoke : attendre l'interface bloquerait le fil de
        // réception, et les trames suivantes s'accumuleraient pendant ce temps.
        //
        // L'ordre reste garanti vis-à-vis des tuiles : elles empruntent le même
        // dispatcher à la même priorité, et WPF traite sa file en FIFO à priorité
        // égale. Le redimensionnement s'exécute donc avant les tuiles qui le
        // suivent — indispensable, sinon elles seraient rognées sur l'ancienne
        // taille du framebuffer.
        _uiDispatcher.BeginInvoke(() =>
        {
            try
            {
                Framebuffer.Resize(w, h);
                ScreenSizeChanged?.Invoke();
            }
            catch { /* dimensions aberrantes : ignorées, un prochain screen-size corrigera */ }
        });

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
