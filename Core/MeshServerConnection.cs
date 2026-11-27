using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MeshControl.Core;

public enum ConnectionState { Disconnected, Connecting, Connected, Failed, Reconnecting }

/// <summary>Erreurs de haut niveau, présentables à l'utilisateur.</summary>
public class MeshException(string message) : Exception(message);

/// <summary>
/// Le serveur réclame un second facteur (« tokenrequired »). Transporte ce
/// qu'il annonce dans la même trame, pour que l'interface sache quoi proposer :
/// envoi du code par courriel ou SMS, et durée de la mémorisation du poste.
/// </summary>
public sealed class MeshTwoFactorRequiredException(string message) : MeshException(message)
{
    /// <summary>Le serveur peut envoyer un code par courriel (jeton « **email** »).</summary>
    public bool EmailAvailable { get; init; }

    /// <summary>Le serveur peut envoyer un code par SMS (jeton « **sms** »).</summary>
    public bool SmsAvailable { get; init; }

    /// <summary>Le serveur peut envoyer un code par messagerie (jeton « **msg** »).</summary>
    public bool MessengerAvailable { get; init; }

    /// <summary>
    /// Durée, en jours, pendant laquelle le serveur accepte qu'un poste soit
    /// mémorisé. Zéro signifie que l'administrateur a désactivé l'option.
    /// </summary>
    public int TrustedDeviceDays { get; init; }

    /// <summary>Un code vient d'être envoyé (courriel, SMS ou messagerie).</summary>
    public bool CodeSent { get; init; }
}

/// <summary>
/// Jeton de connexion créé par le serveur : un couple identifiant/mot de passe
/// de remplacement, propre à un client, qui ne passe pas par le second facteur.
/// </summary>
/// <param name="User">Identifiant du jeton, toujours préfixé « ~t: ».</param>
/// <param name="Password">Mot de passe du jeton — visible une seule fois.</param>
/// <param name="Name">Nom donné au jeton, affiché côté serveur.</param>
/// <param name="Expire">Fin de validité, ou null si le jeton n'expire pas.</param>
public sealed record LoginToken(string User, string Password, string Name, DateTime? Expire);

/// <summary>
/// De quoi ouvrir une session : les identifiants, et ce qui permet de passer
/// le second facteur sans ressaisie.
/// </summary>
public sealed class MeshLogin
{
    /// <summary>Mot de passe du compte — ou du jeton de connexion, s'il y en a un.</summary>
    public required string Password { get; init; }

    /// <summary>
    /// Code à deux facteurs saisi par l'utilisateur. Les valeurs spéciales
    /// « **email** », « **sms** » et « **msg** » demandent au serveur d'en
    /// envoyer un au lieu d'en vérifier un.
    /// </summary>
    public string? TwoFactorToken { get; init; }

    /// <summary>
    /// Cookie « appareil de confiance » obtenu lors d'une précédente validation.
    /// Envoyé tel quel : le serveur le reconnaît et ne redemande pas de code.
    /// </summary>
    public string? TwoFactorCookie { get; init; }

    /// <summary>
    /// Identifiant « ~t:… » quand la session s'ouvre par jeton de connexion.
    /// Remplace alors le nom d'utilisateur du profil.
    /// </summary>
    public string? LoginTokenUser { get; init; }
}

/// <summary>
/// Le canal de contrôle MeshCentral : wss://server/control.ashx en trames JSON.
/// Gère login, liste des appareils, événements temps réel, cookies relais et
/// ouverture de tunnels.
/// </summary>
public sealed class MeshServerConnection : IDisposable
{
    public ServerProfile Profile { get; }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ServerInfo? ServerInfo { get; private set; }
    public UserInfo? UserInfo { get; private set; }

    /// <summary>
    /// Vrai quand la session a été ouverte avec un jeton de connexion. Le
    /// serveur refuse alors tout ce qui touche au compte : changement de mot de
    /// passe, réglages de 2FA, et création ou révocation de jetons.
    /// </summary>
    public bool UsedLoginToken { get; private set; }

    /// <summary>
    /// Vrai si le serveur autorise la création de jetons de connexion pour ce
    /// compte. Le drapeau 0x00000100 de features2 signale au contraire une
    /// session déjà ouverte par jeton, donc bridée.
    /// </summary>
    public bool CanCreateLoginToken
        => !UsedLoginToken && ((ServerInfo?.Features2 ?? 0) & 0x00000100) == 0;
    public IReadOnlyList<Mesh> Meshes => _meshes;
    public IReadOnlyDictionary<string, MeshNode> Nodes => _nodes;
    public string? LastError { get; private set; }

    // Événements pour l'UI (marshalés vers le thread UI par l'appelant).
    public event Action? StateChanged;
    public event Action? DevicesChanged;

    private readonly List<Mesh> _meshes = new();
    private readonly ConcurrentDictionary<string, MeshNode> _nodes = new();

    private MeshWebSocket? _socket;
    private CancellationTokenSource? _loopCts;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private Task? _cookieRefreshTask;
    private Task? _inventoryRefreshTask;

    // Cookies relais générés par {action:'authcookie'}.
    private string? _authCookie;
    private string? _rCookie;

    private static readonly JsonSerializerOptions JsonOpts = new();

    public MeshServerConnection(ServerProfile profile) => Profile = profile;

    private void SetState(ConnectionState s)
    {
        State = s;
        StateChanged?.Invoke();
    }

    // MARK: - Connexion

    /// <summary>Connexion par identifiants de compte, sans second facteur mémorisé.</summary>
    public Task ConnectAsync(string password, string? token = null)
        => ConnectAsync(new MeshLogin { Password = password, TwoFactorToken = token });

    public async Task ConnectAsync(MeshLogin login)
    {
        if (State == ConnectionState.Connecting) return;
        var url = Profile.WebSocketUri("control.ashx")
                  ?? throw new MeshException("Adresse de serveur invalide.");
        SetState(ConnectionState.Connecting);
        LastError = null;

        // Un jeton de connexion remplace le couple compte/mot de passe : son
        // identifiant « ~t:… » prend la place du nom d'utilisateur.
        var username = string.IsNullOrEmpty(login.LoginTokenUser)
            ? Profile.Username
            : login.LoginTokenUser;
        UsedLoginToken = !string.IsNullOrEmpty(login.LoginTokenUser);

        // En-tête x-meshauth : base64(user),base64(pass)[,base64(2fa)]
        //
        // Le troisième champ accepte aussi bien un code à usage unique qu'un
        // cookie d'appareil de confiance, préfixé « cookie= » — le serveur
        // distingue les deux (checkUserOneTimePassword). Un code fraîchement
        // saisi l'emporte : c'est lui qui permettra d'obtenir un cookie neuf.
        var authValue = $"{B64(username)},{B64(login.Password)}";
        var token = login.TwoFactorToken;
        if (!string.IsNullOrEmpty(token))
            authValue += $",{B64(token)}";
        else if (!string.IsNullOrEmpty(login.TwoFactorCookie))
            authValue += $",{B64($"cookie={login.TwoFactorCookie}")}";

        var socket = new MeshWebSocket(
            url,
            new Dictionary<string, string> { ["x-meshauth"] = authValue },
            Profile.AllowSelfSigned);
        _socket = socket;

        try
        {
            await socket.ConnectAsync().ConfigureAwait(false);
            await WaitForLoginAsync(socket).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _socket = null;
            socket.Dispose();
            LastError = FriendlyMessage(ex);

            // Une demande de second facteur n'est pas un échec : l'authentification
            // se poursuit, la main revient simplement à l'utilisateur. La marquer
            // « Failed » déclencherait le traitement d'une connexion perdue, lequel
            // repart d'un formulaire de connexion vierge — et effacerait le champ du
            // code que l'interface vient d'afficher, l'événement d'état étant traité
            // après coup par la file de messages.
            if (ex is MeshTwoFactorRequiredException)
            {
                Log.Info($"{Profile.Host} réclame un code à deux facteurs", "Serveur");
                SetState(ConnectionState.Disconnected);
            }
            else
            {
                // Refus du serveur et réseau injoignable sont des issues prévues :
                // une ligne suffit. Pendant une coupure, la reconnexion automatique
                // les répète chaque minute, et la pile d'appels, toujours la même,
                // noyait le journal.
                if (ex is MeshException or OperationCanceledException
                    or System.Net.WebSockets.WebSocketException)
                {
                    var detail = TechnicalDetail(ex);
                    Log.Warning(detail == LastError
                                    ? $"Connexion à {Profile.Host} échouée : {LastError}"
                                    : $"Connexion à {Profile.Host} échouée : {LastError} ({detail})",
                                "Serveur");
                }
                else
                    Log.Error($"Connexion à {Profile.Host} échouée : {LastError}", ex, "Serveur");
                SetState(ConnectionState.Failed);
            }
            throw;
        }

        Log.Info($"Connecté à {Profile.Host} en tant que {UserInfo?.Name ?? Profile.Username}",
                 "Serveur");
        SetState(ConnectionState.Connected);

        _lastFrameAt = DateTime.UtcNow;
        _pongSeen = false;

        _loopCts = new CancellationTokenSource();
        StartReceiveLoop(socket, _loopCts.Token);
        StartHeartbeat(_loopCts.Token);
        StartCookieRefresh(_loopCts.Token);
        StartInventoryRefresh(_loopCts.Token);

        await SendJsonAsync(new { action = "meshes" }).ConfigureAwait(false);
        await SendJsonAsync(new { action = "nodes" }).ConfigureAwait(false);
        // Les dates de dernière connexion vivent dans une collection séparée du
        // serveur : elles n'arrivent pas avec « nodes » et demandent leur propre
        // requête, comme le fait le client web officiel.
        await SendJsonAsync(new { action = "lastconnects" }).ConfigureAwait(false);
        await SendJsonAsync(new { action = "authcookie" }).ConfigureAwait(false);
    }

    /// <summary>Lit les trames jusqu'à userinfo (login OK) ou un message de close.</summary>
    private async Task WaitForLoginAsync(MeshWebSocket socket)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var message = await socket.ReceiveAsync(watchdog.Token).ConfigureAwait(false);
            if (message is not WsMessage.Text(var text)) continue;
            if (!TryParse(text, out var json)) continue;

            var action = GetString(json, "action");
            switch (action)
            {
                case "serverinfo":
                    if (json.TryGetProperty("serverinfo", out var si))
                        ServerInfo = ServerInfo.From(si);
                    break;
                case "userinfo":
                    if (json.TryGetProperty("userinfo", out var ui))
                        UserInfo = UserInfo.From(ui);
                    return;
                case "close":
                    throw CloseError(json);
            }
        }
    }

    private static MeshException CloseError(JsonElement json)
    {
        var cause = GetString(json, "cause") ?? "";
        var msg = GetString(json, "msg") ?? "";

        if (msg == "tokenrequired")
        {
            var sent = GetBool(json, "email2fasent")
                       || GetBool(json, "sms2fasent")
                       || GetBool(json, "msg2fasent");
            return new MeshTwoFactorRequiredException(
                sent ? "Un code vient de vous être envoyé. Saisissez-le pour continuer."
                     : "Ce compte requiert un code à deux facteurs.")
            {
                EmailAvailable = GetBool(json, "email2fa"),
                SmsAvailable = GetBool(json, "sms2fa"),
                MessengerAvailable = GetBool(json, "msg2fa"),
                TrustedDeviceDays = json.TryGetProperty("twoFactorCookieDays", out var days)
                                    && days.ValueKind == JsonValueKind.Number
                    ? days.GetInt32() : 0,
                CodeSent = sent,
            };
        }

        return (cause, msg) switch
        {
            ("noauth", "nokey") => new MeshException("Ce serveur requiert une clé d'accès URL. Ajoutez-la dans les paramètres du serveur."),
            ("banned", _) => new MeshException("Trop de tentatives — cette IP est temporairement bannie par le serveur."),
            ("notools", _) => new MeshException("Ce compte n'est pas autorisé à utiliser des clients API."),
            ("emailvalidation", _) => new MeshException("Vérifiez l'adresse e-mail du compte avant de vous connecter."),
            _ => new MeshException("Nom d'utilisateur ou mot de passe invalide."),
        };
    }

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        MeshException me => me.Message,
        // L'échec de l'établissement porte sa cause dans l'HttpRequestException
        // interne : on ne parle de certificat que lorsqu'il est en cause.
        System.Net.WebSockets.WebSocketException { InnerException: HttpRequestException http }
            => Describe(http),
        HttpRequestException http => Describe(http),
        System.Net.WebSockets.WebSocketException => "La connexion avec le serveur a été interrompue.",
        OperationCanceledException => "Délai de connexion dépassé.",
        _ => ex.Message,
    };

    private static string Describe(HttpRequestException http) => http.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError
            => "Serveur introuvable. Vérifiez l'adresse et la connexion réseau.",
        HttpRequestError.SecureConnectionError when CertificateTrust.HasRejectedChange
            => "Le certificat du serveur a changé depuis la dernière connexion : connexion refusée "
             + "par sécurité. Si ce changement est attendu (certificat renouvelé), décochez "
             + "« Autoriser un certificat auto-signé » dans les réglages, enregistrez, puis recochez-la.",
        HttpRequestError.SecureConnectionError
            => "Le certificat du serveur a été refusé. S'il est auto-signé, activez l'option correspondante.",
        _ => "Impossible de joindre le serveur. Vérifiez la connexion réseau.",
    };

    /// <summary>
    /// Vérifie qu'un serveur répond, avant même de présenter des identifiants :
    /// une faute de frappe dans l'adresse se signale ainsi au moment où on la
    /// saisit. Toute réponse HTTP suffit — c'est l'adresse et le certificat
    /// qu'on éprouve, pas le compte. Renvoie null si le serveur répond, sinon
    /// un message destiné à l'utilisateur.
    /// </summary>
    public static async Task<string?> ProbeAsync(ServerProfile profile)
    {
        if (profile.BaseUri is not { } uri) return "Adresse invalide.";

        using var handler = new HttpClientHandler();
        if (profile.AllowSelfSigned)
            handler.ServerCertificateCustomValidationCallback = CertificateTrust.HttpCallback(uri.Authority);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead)
                                           .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning($"{profile.Host} ne répond pas : {TechnicalDetail(ex)}", "Serveur");
            return FriendlyMessage(ex);
        }
    }

    /// <summary>Cause d'origine, pour le journal : le message affiché la résume.</summary>
    private static string TechnicalDetail(Exception ex)
    {
        while (ex.InnerException is { } inner) ex = inner;
        return ex.Message;
    }

    public void Disconnect()
    {
        Cleanup();
        SetState(ConnectionState.Disconnected);
    }

    private void Cleanup()
    {
        try { _loopCts?.Cancel(); } catch { }
        _loopCts?.Dispose();
        _loopCts = null;
        InvalidateCookies();
        _socket?.Close();
        _socket?.Dispose();
        _socket = null;
    }

    private void InvalidateCookies()
    {
        _authCookie = null;
        _rCookie = null;
    }

    private void MarkConnectionLost()
    {
        if (State != ConnectionState.Connected) return;
        Log.Warning($"Connexion perdue avec {Profile.Host}", "Serveur");
        Cleanup();
        LastError = "La connexion au serveur a été perdue.";
        SetState(ConnectionState.Failed);
    }

    // MARK: - Boucle de réception

    private void StartReceiveLoop(MeshWebSocket socket, CancellationToken ct)
    {
        _receiveTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                WsMessage message;
                try
                {
                    message = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    MarkConnectionLost();
                    return;
                }
                // Toute trame, quel qu'en soit le contenu, prouve que le canal
                // est vivant.
                _lastFrameAt = DateTime.UtcNow;

                if (message is WsMessage.Text(var text) && TryParse(text, out var json))
                    Handle(json);
            }
        }, ct);
    }

    private void Handle(JsonElement json)
    {
        var action = GetString(json, "action");
        if (action is null) return;

        // Réponse à une requête identifiée (partages…) : le serveur renvoie la
        // commande complétée, on la livre à l'appelant qui l'attend.
        if (GetString(json, "responseid") is { } responseId
            && _pendingResponses.TryRemove(responseId, out var pending))
        {
            pending.TrySetResult(json);
            return;
        }

        // Réponses que le serveur n'identifie que par leur action.
        if (_actionWaiters.TryRemove(action, out var actionWaiter))
        {
            actionWaiter.TrySetResult(json);
            return;
        }

        switch (action)
        {
            case "ping":
                SendJsonAsync(new { action = "pong" }).Forget();
                break;
            case "meshes":
                if (json.TryGetProperty("meshes", out var meshesEl) && meshesEl.ValueKind == JsonValueKind.Array)
                {
                    _meshes.Clear();
                    foreach (var m in meshesEl.EnumerateArray())
                        if (Mesh.From(m) is { } mesh) _meshes.Add(mesh);
                    _meshes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    // Un groupe supprimé emporte ses machines, même si le
                    // serveur n'a pas envoyé d'événement pour chacune.
                    PruneOrphanNodes();
                    DevicesChanged?.Invoke();
                }
                break;
            case "nodes":
                if (json.TryGetProperty("nodes", out var grouped) && grouped.ValueKind == JsonValueKind.Object)
                {
                    MergeNodeInventory(grouped);
                    DevicesChanged?.Invoke();
                }
                break;
            case "pong":
                // Le serveur répond : le contrôle de silence peut s'appuyer
                // dessus (voir StartHeartbeat).
                _pongSeen = true;
                break;
            case "lastconnects":
                // { lastconnects: { "<nodeid>": <timestamp ms>, ... } }
                if (json.TryGetProperty("lastconnects", out var lastSeen)
                    && lastSeen.ValueKind == JsonValueKind.Object)
                {
                    int applied = 0;
                    foreach (var entry in lastSeen.EnumerateObject())
                    {
                        if (entry.Value.ValueKind != JsonValueKind.Number) continue;
                        if (!_nodes.TryGetValue(entry.Name, out var seenNode)) continue;

                        // Timestamp Unix en millisecondes, converti en heure locale.
                        seenNode.LastConnect = DateTimeOffset
                            .FromUnixTimeMilliseconds(entry.Value.GetInt64())
                            .LocalDateTime;
                        applied++;
                    }
                    if (applied > 0) DevicesChanged?.Invoke();
                }
                break;
            case "authcookie":
                _authCookie = GetString(json, "cookie");
                _rCookie = GetString(json, "rcookie");
                break;
            case "event":
                if (json.TryGetProperty("event", out var ev))
                    HandleEvent(ev);
                break;
            case "msg":
                // Journalisé au niveau Debug, mais sans son contenu : un message
                // d'agent peut porter le presse-papiers distant (souvent un mot
                // de passe), une liste de processus ou une sortie de console.
                Log.Debug($"Message d'agent reçu : {DescribeAgentMessage(json)}", "Serveur");
                HandleDeviceMessage(json);
                break;
            case "close":
                LastError = CloseError(json).Message;
                SetState(ConnectionState.Failed);
                break;
            default:
                // Trace des actions non gérées : indispensable pour diagnostiquer
                // une réponse d'agent au format inattendu.
                if (_consoleWaiter is not null || _processListWaiter is not null)
                {
                    Log.Debug($"Action non gérée « {action} » (attente en cours) : "
                            + DescribeAgentMessage(json), "Serveur");
                }
                break;
        }
    }

    /// <summary>
    /// Fusionne un inventaire complet (« nodes ») dans le dictionnaire.
    ///
    /// Les fiches existantes sont mises à jour sur place au lieu d'être
    /// remplacées. L'interface et les sessions ouvertes gardent une référence
    /// vers l'objet MeshNode : le remplacer laissait derrière des instances
    /// orphelines, que les événements « nodeconnect » suivants ne touchaient
    /// plus. L'état affiché se figeait alors jusqu'au redémarrage de
    /// l'application — c'était le cas après chaque rafraîchissement de la liste.
    /// </summary>
    private void MergeNodeInventory(JsonElement grouped)
    {
        var seen = new HashSet<string>();
        var orphans = 0;

        foreach (var group in grouped.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array) continue;

            // Machines d'un groupe que le serveur ne nous liste pas (groupe
            // supprimé, dont les fiches traînent encore en base) : l'interface
            // web les écarte, on fait de même. Sans cela, elles gonflaient le
            // compte des machines hors ligne.
            if (!IsKnownMesh(group.Name))
            {
                orphans += group.Value.GetArrayLength();
                continue;
            }

            foreach (var item in group.Value.EnumerateArray())
            {
                if (MeshNode.From(item, group.Name) is not { } fresh) continue;
                seen.Add(fresh.Id);

                if (_nodes.TryGetValue(fresh.Id, out var existing)) existing.Adopt(fresh);
                else _nodes[fresh.Id] = fresh;
            }
        }

        // Machines absentes de l'inventaire : retirées du serveur, ou droits
        // révoqués. Elles ne doivent pas survivre à un rafraîchissement.
        foreach (var id in _nodes.Keys.Where(id => !seen.Contains(id)).ToList())
            _nodes.TryRemove(id, out _);

        if (orphans > 0)
            Log.Info($"{orphans} machine(s) ignorée(s) : leur groupe n'existe plus sur le serveur", "Serveur");
    }

    /// <summary>
    /// Le groupe fait-il partie de ceux que le serveur nous liste ? Tant que la
    /// liste des groupes n'est pas arrivée, on ne filtre rien.
    /// </summary>
    private bool IsKnownMesh(string meshId)
    {
        return _meshes.Count == 0 || _meshes.Any(m => m.Id == meshId);
    }

    /// <summary>Retire les machines dont le groupe a disparu de la liste.</summary>
    private void PruneOrphanNodes()
    {
        foreach (var node in _nodes.Values.Where(n => !IsKnownMesh(n.MeshId)).ToList())
            _nodes.TryRemove(node.Id, out _);
    }

    /// <summary>
    /// Messages relayés depuis un agent. On n'y traite que la réponse au
    /// presse-papiers ; les autres types passent par leurs tunnels dédiés.
    /// </summary>
    private void HandleDeviceMessage(JsonElement json)
    {
        // L'agent répond avec « sessionid » (et non « nodeid ») : on ne peut donc
        // pas apparier par machine. La demande en cours est la seule attendue.
        switch (GetString(json, "type"))
        {
            case "getclip":
            {
                var waiter = _clipboardWaiter;
                if (waiter is null) return;
                _clipboardWaiter = null;
                waiter.TrySetResult(GetString(json, "data") ?? "");
                break;
            }

            case "ps":
            {
                var waiter = _processListWaiter;
                if (waiter is null) return;
                _processListWaiter = null;
                // « value » contient du JSON sérialisé sous forme de chaîne.
                waiter.TrySetResult(GetString(json, "value") ?? "");
                break;
            }

            case "console":
            {
                // Sortie d'une commande console. L'agent peut en émettre
                // spontanément (messages d'état) : on ne la retient que si une
                // demande est en cours.
                var waiter = _consoleWaiter;
                if (waiter is null) return;
                _consoleWaiter = null;
                waiter.TrySetResult(GetString(json, "value")
                                    ?? GetString(json, "msg") ?? "");
                break;
            }
        }
    }

    /// <summary>Entier d'un champ d'événement, ou null s'il est absent.</summary>
    private static int? EventInt(JsonElement ev, string key)
        => ev.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private void HandleEvent(JsonElement ev)
    {
        var action = GetString(ev, "action");
        switch (action)
        {
            case "nodeconnect":
                if (GetString(ev, "nodeid") is { } nid && _nodes.TryGetValue(nid, out var node))
                {
                    // Seuls les champs réellement présents sont appliqués : un
                    // événement partiel écrivait 0 dans la connectivité, ce qui
                    // affichait la machine hors ligne alors qu'elle venait de se
                    // connecter.
                    if (EventInt(ev, "conn") is { } conn) node.Conn = conn;
                    if (EventInt(ev, "pwr") is { } pwr) node.Pwr = pwr;

                    // Une machine qui se connecte est vue « maintenant » : sans
                    // cela, sa dernière présence resterait sur la date reçue au
                    // démarrage jusqu'au prochain « lastconnects ».
                    if (node.IsOnline) node.LastConnect = DateTime.Now;

                    Log.Debug($"{node.Name} : conn={node.Conn} "
                            + (node.IsOnline ? "(en ligne)" : "(hors ligne)"), "Serveur");
                    DevicesChanged?.Invoke();
                }
                break;
            case "changenode":
                if (ev.TryGetProperty("node", out var nodeJson) && GetString(nodeJson, "_id") is { } cid
                    && _nodes.TryGetValue(cid, out var existing))
                {
                    existing.Merge(nodeJson);
                    DevicesChanged?.Invoke();
                }
                break;
            case "devicesessions":
                // Session MeshCentral ouverte ou refermée sur la machine.
                if (GetString(ev, "nodeid") is { } sessionNodeId
                    && _nodes.TryGetValue(sessionNodeId, out var sessionNode))
                {
                    sessionNode.Sessions = ev.TryGetProperty("sessions", out var sessions)
                        ? MeshNode.ParseSessions(sessions) : [];
                    DevicesChanged?.Invoke();
                }
                break;
            case "addnode":
                if (ev.TryGetProperty("node", out var addJson))
                {
                    var meshId = GetString(ev, "meshid") ?? GetString(addJson, "meshid");
                    if (meshId is not null && IsKnownMesh(meshId)
                        && MeshNode.From(addJson, meshId) is { } added)
                    {
                        _nodes[added.Id] = added;
                        DevicesChanged?.Invoke();
                    }
                }
                break;
            case "removenode":
                if (GetString(ev, "nodeid") is { } rid && _nodes.TryRemove(rid, out _))
                    DevicesChanged?.Invoke();
                break;
            case "nodemeshchange":
            case "meshchange":
            case "createmesh":
            case "deletemesh":
                RefreshDeviceListAsync().Forget();
                break;
        }
    }

    // MARK: - Requêtes

    public async Task SendJsonAsync(object obj)
    {
        var socket = _socket ?? throw new MeshException("Non connecté au serveur.");
        var text = JsonSerializer.Serialize(obj, JsonOpts);
        await socket.SendAsync(text).ConfigureAwait(false);
    }

    public async Task RefreshDeviceListAsync()
    {
        await SendJsonAsync(new { action = "meshes" }).ConfigureAwait(false);
        await SendJsonAsync(new { action = "nodes" }).ConfigureAwait(false);
        // « nodes » reconstruit le dictionnaire : sans cette requête, les dates
        // de dernière connexion seraient perdues au premier rafraîchissement.
        await SendJsonAsync(new { action = "lastconnects" }).ConfigureAwait(false);
    }

    // MARK: - Mémorisation du second facteur

    /// <summary>
    /// Réponses reconnues à leur seule action. Le serveur ne reprend pas le
    /// responseid partout — « createLoginToken » et « twoFactorCookie » n'en
    /// renvoient qu'en cas de refus — et ces réponses n'ont pas de message
    /// d'erreur dédié : un refus se traduit par un silence, d'où l'attente
    /// bornée dans le temps.
    /// </summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _actionWaiters = new();

    private async Task<JsonElement?> RequestByActionAsync(
        string action, object command, int timeoutSeconds = 10)
    {
        if (State != ConnectionState.Connected) throw new MeshException("Non connecté au serveur.");

        var tcs = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _actionWaiters[action] = tcs;
        try
        {
            await SendJsonAsync(command).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var registration = timeout.Token.Register(() => tcs.TrySetResult(default));

            var response = await tcs.Task.ConfigureAwait(false);
            return response.ValueKind == JsonValueKind.Object ? response : null;
        }
        finally
        {
            _actionWaiters.TryRemove(action, out _);
        }
    }

    /// <summary>
    /// Demande au serveur un cookie « appareil de confiance », à renvoyer aux
    /// connexions suivantes pour ne plus avoir à saisir de code. Équivalent de
    /// la case à cocher du client web, soumis à la même durée de validité.
    ///
    /// Renvoie null si le serveur refuse : option désactivée
    /// (twoFactorCookieDurationDays à 0) ou session ouverte par jeton.
    /// </summary>
    public async Task<string?> RequestTrustedDeviceCookieAsync()
    {
        var response = await RequestByActionAsync(
            "twoFactorCookie", new { action = "twoFactorCookie" }).ConfigureAwait(false);
        if (response is not { } json) return null;

        var cookie = GetString(json, "cookie");
        return string.IsNullOrEmpty(cookie) ? null : cookie;
    }

    /// <summary>
    /// Crée un jeton de connexion propre à ce poste : un couple
    /// identifiant/mot de passe de remplacement que le serveur accepte sans
    /// second facteur. Le mot de passe n'est communiqué qu'ici, à la création.
    /// </summary>
    /// <param name="name">Nom du jeton, visible dans « Mon compte » sur le serveur.</param>
    /// <param name="expireMinutes">Durée de validité en minutes ; 0 pour aucune expiration.</param>
    /// <returns>Le jeton, ou null si le serveur refuse (option désactivée pour ce compte).</returns>
    public async Task<LoginToken?> CreateLoginTokenAsync(string name, int expireMinutes = 0)
    {
        var response = await RequestByActionAsync("createLoginToken", new
        {
            action = "createLoginToken",
            name,
            expire = expireMinutes,
            responseid = RandomHex(10),
        }).ConfigureAwait(false);

        if (response is not { } json) return null;

        // Refus : { action, responseid, result: "<raison>" }.
        if (GetString(json, "result") is { } refusal)
        {
            Log.Warning($"Jeton de connexion refusé par le serveur : {refusal}", "Serveur");
            return null;
        }

        var user = GetString(json, "tokenUser");
        var pass = GetString(json, "tokenPass");
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) return null;

        DateTime? expire = null;
        if (json.TryGetProperty("expire", out var expireEl)
            && expireEl.ValueKind == JsonValueKind.Number
            && expireEl.GetInt64() > 0)
        {
            expire = DateTimeOffset.FromUnixTimeMilliseconds(expireEl.GetInt64()).LocalDateTime;
        }

        return new LoginToken(user, pass, GetString(json, "name") ?? name, expire);
    }

    /// <summary>
    /// Révoque un jeton de connexion côté serveur.
    ///
    /// Le serveur peut refuser — jetons désactivés pour ce compte, ou session
    /// ouverte par jeton — et il le fait en restant silencieux. On tente donc,
    /// et c'est sa réponse qui tranche : false signifie qu'il faudra passer par
    /// « Mon compte » sur le serveur.
    /// </summary>
    public async Task<bool> RevokeLoginTokenAsync(string tokenUser)
    {
        if (string.IsNullOrEmpty(tokenUser)) return false;

        var response = await RequestByActionAsync("loginTokens", new
        {
            action = "loginTokens",
            remove = new[] { tokenUser },
        }).ConfigureAwait(false);

        return response is not null;
    }

    /// <summary>Appareil sans agent dans un groupe local/relais (mesh mtype 3).</summary>
    public bool IsLocalDevice(MeshNode node) =>
        _meshes.FirstOrDefault(m => m.Id == node.MeshId)?.MType == 3;

    public Task WakeAsync(string nodeId) => WakeAsync([nodeId]);

    /// <summary>Réveil groupé : le serveur accepte une liste de machines.</summary>
    public Task WakeAsync(IEnumerable<string> nodeIds) =>
        SendJsonAsync(new { action = "wakedevices", nodeids = nodeIds.ToArray() });

    public Task PowerActionAsync(string nodeId, PowerActionType action) => PowerActionAsync([nodeId], action);

    /// <summary>Action d'alimentation groupée, en un seul message au serveur.</summary>
    public Task PowerActionAsync(IEnumerable<string> nodeIds, PowerActionType action) =>
        SendJsonAsync(new { action = "poweraction", nodeids = nodeIds.ToArray(), actiontype = (int)action });

    /// <summary>
    /// Renomme un appareil dans MeshCentral. Seul le nom affiché change : le nom
    /// réseau de la machine reste celui que lui donne son système.
    ///
    /// Le serveur exige le droit « gérer les appareils » sur le groupe. Il
    /// répond « ok » ou la raison du refus, puis diffuse un « changenode » que
    /// tous les clients connectés appliquent — cette application comprise.
    /// </summary>
    public async Task RenameDeviceAsync(string nodeId, string newName)
    {
        if (State != ConnectionState.Connected) throw new MeshException("Non connecté au serveur.");

        var responseId = RandomHex(10);
        var tcs = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponses[responseId] = tcs;

        try
        {
            await SendJsonAsync(new
            {
                action = "changedevice",
                nodeid = nodeId,
                name = newName,
                responseid = responseId,
            }).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var registration = timeout.Token.Register(() => tcs.TrySetCanceled());

            JsonElement response;
            try
            {
                response = await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Certaines versions du serveur n'acquittent pas un changement
                // réussi : l'événement « changenode » arrivé entre-temps en tient lieu.
                if (_nodes.TryGetValue(nodeId, out var current) && current.Name == newName) return;
                throw new MeshException("Le serveur n'a pas confirmé le changement de nom.");
            }

            var result = GetString(response, "result");
            if (result is not null && !result.Equals("ok", StringComparison.OrdinalIgnoreCase))
                throw new MeshException(result.Equals("Access denied", StringComparison.OrdinalIgnoreCase)
                    ? "Vous n'avez pas le droit de modifier les appareils de ce groupe."
                    : $"Renommage refusé par le serveur : {result}");

            // Sans attendre l'événement du serveur : la liste suit tout de suite.
            if (_nodes.TryGetValue(nodeId, out var node) && node.Name != newName)
            {
                node.Name = newName;
                DevicesChanged?.Invoke();
            }
        }
        finally
        {
            _pendingResponses.TryRemove(responseId, out _);
        }
    }

    // MARK: - Cookies relais & tunnels

    private void StartCookieRefresh(CancellationToken ct)
    {
        _cookieRefreshTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(20), ct).ConfigureAwait(false); }
                catch { return; }
                try { await SendJsonAsync(new { action = "authcookie" }).ConfigureAwait(false); } catch { }
            }
        }, ct);
    }

    /// <summary>Période entre deux pings envoyés au serveur.</summary>
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Silence au-delà duquel le canal est considéré comme mort. Trois périodes
    /// de ping : il faut avoir laissé passer plusieurs occasions de répondre
    /// avant de couper une session qui fonctionne peut-être encore.
    /// </summary>
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(95);

    /// <summary>Dernière trame reçue, quelle qu'elle soit.</summary>
    private DateTime _lastFrameAt = DateTime.UtcNow;

    /// <summary>
    /// Vrai dès que le serveur a répondu à un ping. Le contrôle de silence n'est
    /// armé qu'à partir de là : un serveur qui ne répondrait pas aux pings ne
    /// doit pas provoquer de déconnexions en boucle.
    /// </summary>
    private bool _pongSeen;

    /// <summary>
    /// Ping régulier, doublé d'un contrôle de silence.
    ///
    /// Un websocket peut rester « ouvert » alors que plus rien ne circule :
    /// coupure Wi-Fi, veille du poste, pare-feu ou proxy qui oublie la session.
    /// L'envoi continue de réussir — le tampon TCP l'accepte — et l'application
    /// attendait alors indéfiniment des événements qui ne viendraient plus : la
    /// liste des machines restait figée jusqu'au redémarrage. Le silence est donc
    /// surveillé, et la perte signalée pour que la reconnexion automatique prenne
    /// le relais.
    /// </summary>
    private void StartHeartbeat(CancellationToken ct)
    {
        _heartbeatTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(PingInterval, ct).ConfigureAwait(false); }
                catch { return; }

                var silence = DateTime.UtcNow - _lastFrameAt;
                if (_pongSeen && silence > SilenceLimit)
                {
                    Log.Warning($"Aucune trame du serveur depuis {(int)silence.TotalSeconds} s : "
                              + "canal considéré comme perdu.", "Serveur");
                    MarkConnectionLost();
                    return;
                }

                try { await SendJsonAsync(new { action = "ping" }).ConfigureAwait(false); }
                catch { MarkConnectionLost(); return; }
            }
        }, ct);
    }

    /// <summary>Période de resynchronisation complète de l'inventaire.</summary>
    private static readonly TimeSpan InventoryRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Relit périodiquement l'inventaire complet.
    ///
    /// Les événements suffisent en temps normal, mais un seul événement manqué
    /// — trame perdue pendant une microcoupure, machine passée en ligne pendant
    /// la reconnexion — laisserait un état faux jusqu'au prochain changement.
    /// C'est la ceinture de sécurité : au pire, l'écart se corrige tout seul.
    /// </summary>
    private void StartInventoryRefresh(CancellationToken ct)
    {
        _inventoryRefreshTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(InventoryRefreshInterval, ct).ConfigureAwait(false); }
                catch { return; }

                if (State != ConnectionState.Connected) continue;
                try { await RefreshDeviceListAsync().ConfigureAwait(false); }
                catch { /* la perte de connexion est traitée ailleurs */ }
            }
        }, ct);
    }

    /// <summary>Garantit des cookies relais frais, en les demandant au besoin.</summary>
    public async Task<(string auth, string rauth)> RelayCookiesAsync()
    {
        if (_authCookie is { } a0 && _rCookie is { } r0) return (a0, r0);

        await SendJsonAsync(new { action = "authcookie" }).ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (_authCookie is { } a && _rCookie is { } r) return (a, r);
            await Task.Delay(100).ConfigureAwait(false);
        }
        throw new MeshException("Délai dépassé pour l'obtention des identifiants d'appareil.");
    }

    /// <summary>Ouvre un tunnel relais vers un appareil (bureau à distance ou fichiers).</summary>
    public async Task<MeshWebSocket> OpenTunnelAsync(string nodeId, RelayProtocol relayProtocol)
    {
        try { return await OpenTunnelOnceAsync(nodeId, relayProtocol).ConfigureAwait(false); }
        catch (MeshException)
        {
            InvalidateCookies();
            return await OpenTunnelOnceAsync(nodeId, relayProtocol).ConfigureAwait(false);
        }
    }

    private async Task<MeshWebSocket> OpenTunnelOnceAsync(string nodeId, RelayProtocol relayProtocol)
    {
        if (State != ConnectionState.Connected) throw new MeshException("Non connecté au serveur.");
        var (auth, rauth) = await RelayCookiesAsync().ConfigureAwait(false);
        var tunnelId = RandomHex(12);
        var p = (int)relayProtocol;

        // Côté agent (via le canal de contrôle). Valeur relative au serveur ('*').
        var agentUrl = $"*/meshrelay.ashx?p={p}&nodeid={nodeId}&id={tunnelId}&rauth={rauth}";
        await SendJsonAsync(new
        {
            action = "msg",
            type = "tunnel",
            nodeid = nodeId,
            value = agentUrl,
            usage = p
        }).ConfigureAwait(false);

        // Notre côté.
        var url = Profile.WebSocketUri("meshrelay.ashx", new Dictionary<string, string>
        {
            ["browser"] = "1",
            ["p"] = p.ToString(),
            ["nodeid"] = nodeId,
            ["id"] = tunnelId,
            ["auth"] = auth,
        }) ?? throw new MeshException("Adresse de serveur invalide.");

        var relay = new MeshWebSocket(url, null, Profile.AllowSelfSigned);
        await relay.ConnectAsync().ConfigureAwait(false);

        // Attendre 'c' ou 'cr' (l'agent peut mettre quelques secondes à composer).
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                var message = await relay.ReceiveAsync(watchdog.Token).ConfigureAwait(false);
                if (message is WsMessage.Text(var t) && (t == "c" || t == "cr")) break;
            }
        }
        catch
        {
            relay.Close();
            relay.Dispose();
            throw new MeshException("Délai dépassé en attendant la connexion de l'appareil.");
        }
        await relay.SendAsync(p.ToString()).ConfigureAwait(false);
        return relay;
    }

    /// <summary>Ouvre le relais terminal SSH (endpoint distinct de meshrelay).</summary>
    public async Task<MeshWebSocket> OpenSshTunnelAsync(string nodeId)
    {
        if (State != ConnectionState.Connected) throw new MeshException("Non connecté au serveur.");
        var (auth, _) = await RelayCookiesAsync().ConfigureAwait(false);
        var url = Profile.WebSocketUri("sshterminalrelay.ashx", new Dictionary<string, string>
        {
            ["browser"] = "1",
            ["p"] = "11",
            ["nodeid"] = nodeId,
            ["id"] = RandomHex(12),
            ["auth"] = auth,
        }) ?? throw new MeshException("Adresse de serveur invalide.");

        var relay = new MeshWebSocket(url, null, Profile.AllowSelfSigned);
        try { await relay.ConnectAsync().ConfigureAwait(false); }
        catch
        {
            relay.Dispose();
            throw new MeshException("Impossible d'ouvrir une session SSH. Vérifiez que SSH est activé sur le serveur (\"ssh\": true dans la config du domaine).");
        }
        return relay;
    }

    // MARK: - Presse-papiers de la machine distante

    /// <summary>
    /// Demande de presse-papiers en cours. L'agent répond avec « sessionid » et
    /// non « nodeid » : on ne peut donc pas apparier la réponse à une machine
    /// précise. Une seule demande à la fois suffit largement à l'usage.
    /// </summary>
    private TaskCompletionSource<string>? _clipboardWaiter;

    /// <summary>
    /// Récupère le texte du presse-papiers de la machine distante.
    ///
    /// Ces messages transitent par le canal de contrôle du serveur (et non par le
    /// tunnel KVM), qui les route vers l'agent. Le serveur peut les refuser si
    /// « clipboardget » est désactivé dans sa configuration.
    /// </summary>
    public async Task<string> GetRemoteClipboardAsync(string nodeId, CancellationToken ct = default)
    {
        if (State != ConnectionState.Connected) throw new MeshException("Non connecté au serveur.");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _clipboardWaiter = tcs;

        try
        {
            // « tag » nous revient tel quel dans la réponse de l'agent. La valeur 3
            // évite d'inscrire une ligne dans le journal du serveur à chaque lecture.
            await SendJsonAsync(new
            {
                action = "msg",
                type = "getclip",
                nodeid = nodeId,
                tag = 3,
            }).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var registration = linked.Token.Register(() => tcs.TrySetException(
                new MeshException(
                    "La machine n'a pas renvoyé son presse-papiers.\n\n"
                  + "Causes possibles : le presse-papiers distant est vide (l'agent ne "
                  + "répond alors pas), la fonction est désactivée sur le serveur "
                  + "(clipboardget), ou l'agent est trop ancien.")));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_clipboardWaiter, tcs)) _clipboardWaiter = null;
        }
    }

    /// <summary>Écrit du texte dans le presse-papiers de la machine distante.</summary>
    public Task SetRemoteClipboardAsync(string nodeId, string text)
        => SendJsonAsync(new
        {
            action = "msg",
            type = "setclip",
            nodeid = nodeId,
            data = text,
        });

    // MARK: - Commandes console de l'agent

    /// <summary>Réponse en attente à une demande de liste de processus.</summary>
    private TaskCompletionSource<string>? _processListWaiter;

    /// <summary>
    /// Envoie une commande console à l'agent (openurl, kill, lock, toast…).
    ///
    /// L'agent exige les droits « Remote Control » (8) ET « Console » (16) sur la
    /// machine : sans eux la commande est ignorée silencieusement.
    /// </summary>
    public Task SendConsoleCommandAsync(string nodeId, string command)
        => SendJsonAsync(new
        {
            action = "msg",
            type = "console",
            nodeid = nodeId,
            value = command,
        });

    /// <summary>Ouvre une URL dans le navigateur de la machine distante.</summary>
    public Task OpenUrlAsync(string nodeId, string url)
    {
        // L'URL est concaténée dans une ligne de commande côté agent : on refuse
        // tout ce qui n'est pas une adresse web simple, et les espaces qui
        // permettraient d'injecter un argument supplémentaire.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            throw new MeshException("Adresse invalide : seules les URL http:// et https:// sont acceptées.");
        if (url.Any(char.IsWhiteSpace))
            throw new MeshException("L'adresse ne doit pas contenir d'espace.");

        return SendConsoleCommandAsync(nodeId, $"openurl {parsed.AbsoluteUri}");
    }

    /// <summary>
    /// Récupère la liste des processus de la machine distante. L'agent répond par
    /// un message « ps » dont la valeur est un objet JSON { pid: { ... } }.
    /// </summary>
    public async Task<string> GetProcessListAsync(string nodeId, CancellationToken ct = default)
    {
        if (State != ConnectionState.Connected) throw new MeshException("Non connecté au serveur.");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _processListWaiter = tcs;

        try
        {
            await SendJsonAsync(new
            {
                action = "msg",
                type = "ps",
                nodeid = nodeId,
            }).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var registration = linked.Token.Register(() => tcs.TrySetException(
                new MeshException(
                    "La machine n'a pas renvoyé la liste des processus.\n\n"
                  + "Vérifiez que votre compte dispose du droit « Console » sur ce groupe "
                  + "d'appareils, et que l'agent est à jour.")));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_processListWaiter, tcs)) _processListWaiter = null;
        }
    }

    /// <summary>
    /// Arrête un processus distant par son identifiant, comme l'interface web
    /// de MeshCentral : un message « pskill » que l'agent traite directement.
    ///
    /// La commande console « kill » utilisée auparavant passait par la console
    /// de l'agent, qui exige le droit « Console » et ne renvoie rien quand elle
    /// ignore l'ordre : l'arrêt échouait sans le moindre signe.
    /// </summary>
    public Task KillProcessAsync(string nodeId, int pid)
    {
        if (State != ConnectionState.Connected) throw new MeshException("Non connecté au serveur.");
        return SendJsonAsync(new { action = "msg", type = "pskill", nodeid = nodeId, value = pid });
    }

    /// <summary>
    /// Arrêt forcé, quand « pskill » n'a pas suffi : la machine exécute
    /// « taskkill /F » (Windows) ou « kill -9 » (Linux, macOS) avec les droits
    /// de l'agent. Passe par la fonction « Exécuter des commandes » du serveur.
    /// </summary>
    public Task ForceKillProcessAsync(string nodeId, int pid, bool windows)
    {
        if (State != ConnectionState.Connected) throw new MeshException("Non connecté au serveur.");
        return SendJsonAsync(new
        {
            action = "runcommands",
            nodeids = new[] { nodeId },
            type = windows ? 1 : 3,
            cmds = windows ? $"taskkill /F /PID {pid}" : $"kill -9 {pid}",
            runAsUser = 0,
            reply = false,
        });
    }

    /// <summary>Réponses console en attente (sysinfo, netinfo…).</summary>
    private TaskCompletionSource<string>? _consoleWaiter;

    /// <summary>
    /// Exécute une commande console et attend sa réponse textuelle.
    ///
    /// C'est la voie fiable pour interroger l'agent : l'action « sysinfo » directe
    /// répond avec un « sessionid » issu d'un contexte de rappel, souvent
    /// indéfini, si bien que le serveur ne sait pas vers qui router la réponse.
    /// La commande console, elle, renvoie un simple texte sur le canal habituel.
    /// </summary>
    public async Task<string> RunConsoleCommandAsync(string nodeId, string command,
                                                     int timeoutSeconds = 25,
                                                     CancellationToken ct = default)
    {
        if (State != ConnectionState.Connected) throw new MeshException("Non connecté au serveur.");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _consoleWaiter = tcs;

        try
        {
            await SendConsoleCommandAsync(nodeId, command).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using var registration = linked.Token.Register(() => tcs.TrySetException(
                new MeshException(
                    "La machine n'a pas répondu.\n\n"
                  + "Vérifiez que votre compte dispose du droit « Console » sur ce "
                  + "groupe d'appareils et que l'agent est à jour.")));

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_consoleWaiter, tcs)) _consoleWaiter = null;
        }
    }

    /// <summary>Informations système, sous forme de texte renvoyé par l'agent.</summary>
    public Task<string> GetSystemInfoTextAsync(string nodeId, CancellationToken ct = default)
        => RunConsoleCommandAsync(nodeId, "sysinfo", timeoutSeconds: 30, ct: ct);

    // MARK: - Partage d'appareil (liens publics)

    /// <summary>
    /// Réponses en attente, indexées par responseid : le serveur renvoie la
    /// commande complétée plutôt qu'un message dédié.
    /// </summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingResponses = new();

    /// <summary>
    /// Crée un lien de partage public vers un appareil.
    /// </summary>
    /// <param name="nodeId">Appareil à partager.</param>
    /// <param name="guestName">Nom de l'invité (affiché côté serveur et dans les journaux).</param>
    /// <param name="protocols">
    /// Masque de bits — attention, ces valeurs diffèrent de celles des tunnels :
    /// 1 = terminal, 2 = bureau, 4 = fichiers, 8 = HTTP, 16 = HTTPS.
    /// </param>
    /// <param name="durationMinutes">Durée de validité en minutes.</param>
    /// <param name="consent">
    /// Drapeaux de consentement côté machine (0 = aucun ; sinon combinaison
    /// définie par MeshCentral, p. ex. invite + notification).
    /// </param>
    /// <param name="viewOnly">
    /// Lecture seule : le destinataire voit l'écran mais ne peut ni cliquer ni
    /// taper. Ce réglage est porté par le cookie du lien, il se fixe donc à la
    /// création et ne peut pas être changé ensuite via l'URL.
    /// </param>
    public async Task<DeviceShare> CreateShareLinkAsync(
        string nodeId, string guestName, int protocols, int durationMinutes, int consent,
        bool viewOnly = false)
    {
        if (State != ConnectionState.Connected) throw new MeshException("Non connecté au serveur.");

        var responseId = RandomHex(10);
        var tcs = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponses[responseId] = tcs;

        try
        {
            // start + expire : le serveur exige soit (start, end), soit
            // (start, expire) — expire étant une durée en minutes.
            var startSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await SendJsonAsync(new
            {
                action = "createDeviceShareLink",
                nodeid = nodeId,
                guestname = guestName,
                p = protocols,
                consent,
                start = startSeconds,
                expire = durationMinutes,
                viewOnly,
                responseid = responseId,
            }).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var registration = timeout.Token.Register(
                () => tcs.TrySetException(new MeshException("Le serveur n'a pas répondu à la demande de partage.")));

            var response = await tcs.Task.ConfigureAwait(false);

            var result = GetString(response, "result");
            if (result is not null && result != "OK")
                throw new MeshException($"Partage refusé par le serveur : {result}");

            var url = GetString(response, "url")
                      ?? throw new MeshException("Le serveur n'a pas renvoyé de lien.");

            return new DeviceShare
            {
                PublicId = GetString(response, "publicid") ?? "",
                Url = NormalizeShareUrl(url),
                GuestName = guestName,
                Protocols = protocols,
            };
        }
        finally
        {
            _pendingResponses.TryRemove(responseId, out _);
        }
    }

    /// <summary>
    /// Le serveur renvoie parfois un chemin relatif (quand son nom d'hôte n'est
    /// pas qualifié) : on le complète avec l'adresse du profil.
    /// </summary>
    /// <summary>
    /// Lien de partage absolu, en http ou https uniquement — vide sinon. Il
    /// finit ouvert par le navigateur (ShellExecute) : toute autre forme
    /// (« file: », chemin, protocole applicatif) n'a rien à y faire.
    /// </summary>
    private string NormalizeShareUrl(string url)
    {
        var baseUri = Profile.BaseUri;
        var absolute = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || baseUri is null
            ? url
            : $"{baseUri.Scheme}://{baseUri.Authority}{(url.StartsWith('/') ? "" : "/")}{url}";

        return IsWebUrl(absolute) ? absolute : "";
    }

    /// <summary>Adresse absolue en http ou https.</summary>
    public static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// Résumé d'un message d'agent pour le journal : son type et sa taille,
    /// jamais son contenu.
    /// </summary>
    private static string DescribeAgentMessage(JsonElement json)
    {
        var type = json.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        return $"type « {type ?? "?"} », {json.GetRawText().Length} caractères";
    }

    /// <summary>Liste les partages actifs d'un appareil.</summary>
    public async Task<List<DeviceShare>> ListSharesAsync(string nodeId)
    {
        if (State != ConnectionState.Connected) throw new MeshException("Non connecté au serveur.");

        var responseId = RandomHex(10);
        var tcs = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponses[responseId] = tcs;

        try
        {
            await SendJsonAsync(new
            {
                action = "deviceShares",
                nodeid = nodeId,
                responseid = responseId,
            }).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var registration = timeout.Token.Register(
                () => tcs.TrySetException(new MeshException("Le serveur n'a pas répondu.")));

            var response = await tcs.Task.ConfigureAwait(false);
            var shares = new List<DeviceShare>();

            if (response.TryGetProperty("deviceShares", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var url = GetString(item, "url");
                    shares.Add(new DeviceShare
                    {
                        PublicId = GetString(item, "publicid") ?? "",
                        Url = url is null ? "" : NormalizeShareUrl(url),
                        GuestName = GetString(item, "guestName") ?? "",
                        Protocols = item.TryGetProperty("p", out var p)
                                    && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                        ExpireTime = item.TryGetProperty("expireTime", out var e)
                                     && e.ValueKind == JsonValueKind.Number
                                     ? DateTimeOffset.FromUnixTimeSeconds(e.GetInt64()).LocalDateTime
                                     : null,
                    });
                }
            }
            return shares;
        }
        finally
        {
            _pendingResponses.TryRemove(responseId, out _);
        }
    }

    /// <summary>Révoque un partage : le lien cesse immédiatement de fonctionner.</summary>
    public Task RemoveShareAsync(string nodeId, string publicId)
        => SendJsonAsync(new
        {
            action = "removeDeviceShare",
            nodeid = nodeId,
            publicid = publicId,
        });

    // MARK: - Utilitaires

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    /// <summary>
    /// Identifiant aléatoire (tunnels, réponses). Générateur cryptographique :
    /// un identifiant de tunnel prévisible permettrait de s'y raccrocher.
    /// </summary>
    public static string RandomHex(int length) =>
        System.Security.Cryptography.RandomNumberGenerator.GetHexString(length, lowercase: true);

    private static bool TryParse(string text, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            element = doc.RootElement.Clone();
            return element.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            element = default;
            return false;
        }
    }

    private static string? GetString(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    public void Dispose() => Cleanup();
}
