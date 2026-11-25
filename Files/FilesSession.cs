using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using MeshControl.Core;

namespace MeshControl.Files;

public enum FileKind { Drive = 1, Directory = 2, File = 3 }

/// <summary>Une entrée du système de fichiers distant.</summary>
public sealed class FileEntry
{
    public string Name { get; init; } = "";
    public FileKind Kind { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public string? DriveType { get; init; }
    public long? FreeBytes { get; init; }

    /// <summary>Ordre d'affichage : dossiers et lecteurs d'abord, puis tri alphabétique.</summary>
    public static int CompareForDisplay(FileEntry x, FileEntry y)
    {
        bool xDir = x.Kind != FileKind.File;
        bool yDir = y.Kind != FileKind.File;
        if (xDir != yDir) return xDir ? -1 : 1;
        return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
    }

    public static FileEntry? From(JsonElement json)
    {
        if (!json.TryGetProperty("n", out var n) || n.ValueKind != JsonValueKind.String) return null;
        if (!json.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.Number) return null;

        DateTime? modified = null;
        if (json.TryGetProperty("d", out var d))
        {
            if (d.ValueKind == JsonValueKind.Number)
                modified = DateTimeOffset.FromUnixTimeSeconds(d.GetInt64()).LocalDateTime;
            else if (d.ValueKind == JsonValueKind.String
                     && DateTime.TryParse(d.GetString(), out var parsed))
                modified = parsed;
        }

        return new FileEntry
        {
            Name = n.GetString()!,
            Kind = (FileKind)Math.Min(t.GetInt32(), 3),
            Size = json.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.Number
                   ? s.GetInt64() : 0,
            Modified = modified,
            DriveType = json.TryGetProperty("dt", out var dt) && dt.ValueKind == JsonValueKind.String
                        ? dt.GetString() : null,
            FreeBytes = json.TryGetProperty("f", out var f) && f.ValueKind == JsonValueKind.Number
                        ? f.GetInt64() : null,
        };
    }
}

public enum FilesSessionState { Connecting, Ready, Closed }

/// <summary>Progression d'un transfert en cours.</summary>
public sealed class TransferProgress
{
    public string FileName { get; init; } = "";
    public long Transferred { get; set; }
    public long Total { get; set; }
    public bool IsUpload { get; init; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public double Fraction => Total > 0 ? Math.Min(1, (double)Transferred / Total) : 0;

    /// <summary>
    /// Débit moyen depuis le début du fichier, en octets par seconde. Nul tant
    /// que la mesure porte sur trop peu de temps pour signifier quelque chose :
    /// la première fenêtre de fragments part d'un bloc et afficherait un débit
    /// fantaisiste.
    /// </summary>
    public double BytesPerSecond
    {
        get
        {
            var elapsed = (DateTime.UtcNow - StartedAt).TotalSeconds;
            return elapsed > 0.5 ? Transferred / elapsed : 0;
        }
    }
}

/// <summary>
/// Navigation de fichiers via un tunnel meshrelay p=5 : les commandes JSON sont
/// envoyées en trames BINAIRES, et toute trame entrante dont le premier octet
/// n'est pas '{' est un fragment de téléchargement.
/// </summary>
public sealed class FilesSession : IDisposable
{
    public FilesSessionState State { get; private set; } = FilesSessionState.Connecting;
    public IReadOnlyList<FileEntry> Entries => _entries;
    public IReadOnlyList<string> PathComponents => _pathComponents;
    public bool IsLoading { get; private set; }
    public TransferProgress? Transfer { get; private set; }
    public string? LastError { get; private set; }
    public string? CloseReason { get; private set; }

    public event Action? StateChanged;
    public event Action? EntriesChanged;
    public event Action? TransferChanged;

    public MeshNode Node { get; }

    private readonly List<FileEntry> _entries = new();
    private readonly List<string> _pathComponents = new();
    private readonly MeshServerConnection _connection;
    private readonly Dispatcher _ui;
    private MeshWebSocket? _socket;
    private CancellationTokenSource? _cts;
    private int _requestCounter = 1;
    private bool _stopped;

    private readonly Channel<byte[]> _sendQueue =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    // État de l'envoi. L'agent n'applique pas de contrôle de flux : il écrit ce
    // qui arrive et acquitte chaque fragment. On garde donc une fenêtre de
    // fragments en vol plutôt qu'un aller-retour par fragment.
    //
    // Le fichier est lu au fil de l'eau, fragment par fragment : le charger en
    // entier limitait la taille envoyable à la mémoire disponible, et un même
    // fichier envoyé à dix machines aurait occupé dix fois sa taille.
    private const int UploadWindow = 16;      // × 64 Ko ≈ 1 Mo en vol
    private const int ChunkSize = 65536;
    private readonly object _uploadLock = new();
    private int _uploadInFlight;
    private FileStream? _uploadStream;
    private long _uploadLength;
    private long _uploadOffset;
    private int _uploadReqId;
    private bool _uploadDoneSent;
    private TaskCompletionSource<bool>? _uploadCompletion;

    /// <summary>
    /// Lectures de dossier faites pour le compte d'un appelant (création de
    /// dossiers, téléchargement récursif…), indexées par numéro de requête :
    /// leur réponse ne doit pas remplacer la liste affichée.
    /// </summary>
    private readonly ConcurrentDictionary<int, TaskCompletionSource<List<FileEntry>?>> _probes = new();

    /// <summary>Délai au-delà duquel une lecture de dossier est jugée perdue.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private DateTime _lastProgressUpdate = DateTime.MinValue;

    public FilesSession(MeshServerConnection connection, MeshNode node, Dispatcher ui)
    {
        _connection = connection;
        Node = node;
        _ui = ui;
    }

    /// <summary>Les agents Windows listent les lecteurs au chemin vide ; les autres partent de /.</summary>
    public bool IsWindows => Node.OsFamily == OSFamily.Windows;

    public string CurrentPath =>
        _pathComponents.Count == 0 ? (IsWindows ? "" : "/") : string.Join("/", _pathComponents);

    public string DisplayPath =>
        _pathComponents.Count == 0 ? (IsWindows ? "Cet ordinateur" : "/")
                                   : string.Join("/", _pathComponents);

    private int NextRequestId() => Interlocked.Increment(ref _requestCounter);

    private void SetState(FilesSessionState state)
    {
        State = state;
        _ui.BeginInvoke(() => StateChanged?.Invoke());
    }

    // MARK: - Cycle de vie

    /// <param name="listOnStart">
    /// Lire le dossier racine dès l'ouverture. Inutile pour une session qui ne
    /// sert qu'à envoyer des fichiers vers un chemin connu.
    /// </param>
    public async Task StartAsync(bool listOnStart = true)
    {
        try
        {
            var socket = await _connection.OpenTunnelAsync(Node.Id, RelayProtocol.Files)
                                          .ConfigureAwait(false);
            if (_stopped) { socket.Close(); socket.Dispose(); return; }

            _socket = socket;
            _cts = new CancellationTokenSource();
            StartSenderLoop(socket, _cts.Token);
            StartReceiveLoop(socket, _cts.Token);

            SetState(FilesSessionState.Ready);
            if (listOnStart) await ListAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_stopped) return;
            CloseReason = ex.Message;
            SetState(FilesSessionState.Closed);
        }
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try { _cts?.Cancel(); } catch { }
        _sendQueue.Writer.TryComplete();
        _uploadCompletion?.TrySetCanceled();
        FailProbes(new OperationCanceledException());
        _socket?.Close();
        _socket?.Dispose();
        _socket = null;
        if (State != FilesSessionState.Closed) SetState(FilesSessionState.Closed);
    }

    // MARK: - Navigation

    public Task ListAsync()
    {
        IsLoading = true;
        return SendJsonAsync(new { action = "ls", reqid = NextRequestId(), path = CurrentPath });
    }

    public Task EnterAsync(FileEntry entry)
    {
        if (entry.Kind == FileKind.File) return Task.CompletedTask;

        // Un lecteur Windows (« C:\ ») devient le premier composant du chemin.
        var name = entry.Kind == FileKind.Drive
            ? entry.Name.TrimEnd('\\', '/')
            : entry.Name;
        _pathComponents.Add(name);
        return ListAsync();
    }

    public Task GoUpAsync()
    {
        if (_pathComponents.Count == 0) return Task.CompletedTask;
        _pathComponents.RemoveAt(_pathComponents.Count - 1);
        return ListAsync();
    }

    public Task GoToAsync(int componentIndex)
    {
        if (componentIndex < 0)
        {
            _pathComponents.Clear();
        }
        else if (componentIndex < _pathComponents.Count - 1)
        {
            _pathComponents.RemoveRange(componentIndex + 1,
                                        _pathComponents.Count - componentIndex - 1);
        }
        return ListAsync();
    }

    // MARK: - Lecture et création de dossiers à la demande

    /// <summary>
    /// Lit un dossier sans toucher à la liste affichée.
    /// </summary>
    /// <returns>Son contenu, ou null si le dossier n'existe pas ou n'est pas lisible.</returns>
    public async Task<IReadOnlyList<FileEntry>?> ListDirectoryAsync(string path,
                                                                   CancellationToken ct = default)
    {
        if (State != FilesSessionState.Ready)
            throw new MeshException(CloseReason ?? "La session de fichiers n'est pas ouverte.");

        var reqid = NextRequestId();
        var probe = new TaskCompletionSource<List<FileEntry>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _probes[reqid] = probe;

        try
        {
            await SendJsonAsync(new { action = "ls", reqid, path }).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            using (timeout.Token.Register(() => probe.TrySetCanceled()))
            {
                return await probe.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_stopped)
        {
            throw new MeshException("La machine ne répond pas.");
        }
        finally
        {
            _probes.TryRemove(reqid, out _);
        }
    }

    /// <summary>
    /// Garantit l'existence d'un dossier, en créant au besoin ses parents.
    ///
    /// « mkdir » n'est pas acquitté par l'agent. Une relecture du dossier en
    /// tient lieu : l'agent traite les commandes d'un tunnel dans l'ordre, la
    /// réponse au « ls » arrive donc après la création, et dit si elle a réussi.
    /// </summary>
    public async Task EnsureDirectoryAsync(string path, CancellationToken ct = default)
    {
        if (IsRootPath(path)) return;
        if (await ListDirectoryAsync(path, ct).ConfigureAwait(false) is not null) return;

        if (ParentPath(path) is { } parent) await EnsureDirectoryAsync(parent, ct).ConfigureAwait(false);

        await SendJsonAsync(new { action = "mkdir", reqid = NextRequestId(), path }).ConfigureAwait(false);

        if (await ListDirectoryAsync(path, ct).ConfigureAwait(false) is null)
            throw new MeshException($"Le dossier « {path} » n'a pas pu être créé.");
    }

    /// <summary>
    /// Racine d'un système de fichiers : « / », « C: » ou « C:\ ». On ne cherche
    /// jamais à la créer.
    /// </summary>
    private static bool IsRootPath(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        return trimmed.Length == 0
               || (trimmed.Length == 2 && trimmed[1] == ':' && char.IsLetter(trimmed[0]));
    }

    /// <summary>Dossier parent, ou null pour une racine.</summary>
    private static string? ParentPath(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        if (index < 0) return null;

        var parent = trimmed[..index];
        // « /home » a pour parent « / », pas la chaîne vide.
        if (parent.Length == 0) return "/";
        return IsRootPath(parent) ? null : parent;
    }

    // MARK: - Opérations sur les fichiers

    public Task CreateFolderAsync(string name)
    {
        var path = CombinePath(CurrentPath, name);
        return SendJsonAsync(new { action = "mkdir", reqid = NextRequestId(), path });
    }

    public Task DeleteAsync(IEnumerable<string> names, bool recursive)
        => SendJsonAsync(new
        {
            action = "rm",
            reqid = NextRequestId(),
            path = CurrentPath,
            delfiles = names.ToArray(),
            rec = recursive,
        });

    public Task RenameAsync(string oldName, string newName)
        => SendJsonAsync(new
        {
            action = "rename",
            path = CurrentPath,
            oldname = oldName,
            newname = newName,
        });

    /// <summary>
    /// Assemble un chemin en respectant le séparateur de la plateforme distante.
    /// </summary>
    public string CombinePath(string basePath, string name)
    {
        if (string.IsNullOrEmpty(basePath)) return name;
        var separator = IsWindows ? "\\" : "/";
        return basePath.EndsWith(separator) || basePath.EndsWith("/")
            ? basePath + name
            : basePath + separator + name;
    }

    /// <summary>Chemin complet d'une entrée du dossier courant.</summary>
    public string FullPathOf(FileEntry entry) => CombinePath(CurrentPath, entry.Name);

    // MARK: - Téléchargement (HTTPS via devicefile.ashx)

    /// <summary>
    /// Télécharge un fichier vers <paramref name="destination"/>. On passe par
    /// devicefile.ashx (HTTP direct) plutôt que par le relais : c'est plus simple
    /// et plus rapide qu'un flux de fragments acquittés.
    /// </summary>
    /// <param name="remotePath">
    /// Chemin complet du fichier sur la machine. Fourni par l'appelant plutôt que
    /// recalculé depuis le dossier courant : une file de transferts continue de
    /// tourner pendant que l'utilisateur navigue ailleurs.
    /// </param>
    public async Task DownloadAsync(FileEntry entry, string remotePath, string destination,
                                    CancellationToken ct = default)
    {
        if (Transfer is not null)
            throw new MeshException("Un transfert est déjà en cours.");

        var (auth, _) = await _connection.RelayCookiesAsync().ConfigureAwait(false);
        var baseUri = _connection.Profile.BaseUri
                      ?? throw new MeshException("Adresse de serveur invalide.");

        // Les identifiants mesh/node ne transmettent que leur 3e segment.
        var meshPart = LastSegment(Node.MeshId);
        var nodePart = LastSegment(Node.Id);
        var url = $"{baseUri.Scheme}://{baseUri.Authority}/devicefile.ashx"
                + $"?c={Uri.EscapeDataString(auth)}"
                + $"&m={Uri.EscapeDataString(meshPart)}"
                + $"&n={Uri.EscapeDataString(nodePart)}"
                + $"&f={Uri.EscapeDataString(remotePath)}";

        SetTransfer(new TransferProgress
        {
            FileName = entry.Name, Total = entry.Size, IsUpload = false,
        });

        var completed = false;
        try
        {
            using var handler = new HttpClientHandler();
            if (_connection.Profile.AllowSelfSigned)
                handler.ServerCertificateCustomValidationCallback =
                    CertificateTrust.HttpCallback(baseUri.Authority);

            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                           .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? entry.Size;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var target = File.Create(destination))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    UpdateProgress(received, total);
                }
            }
            completed = true;
            MarkFromRemote(destination);
        }
        finally
        {
            // Un fichier à moitié reçu ressemble à s'y méprendre au bon : on ne
            // le laisse pas traîner derrière une annulation ou une erreur.
            if (!completed)
            {
                try { File.Delete(destination); } catch { }
            }
            SetTransfer(null);
        }
    }

    /// <summary>
    /// Marque le fichier reçu comme venant d'ailleurs (« Mark of the Web »,
    /// zone Internet), comme le fait un navigateur : SmartScreen contrôle alors
    /// un exécutable avant son lancement, et Office ouvre les documents en mode
    /// protégé. Sans effet sur un disque qui ne gère pas les flux NTFS.
    /// </summary>
    private static void MarkFromRemote(string path)
    {
        try { File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch { /* FAT, exFAT, partage réseau : pas de flux alternatif */ }
    }

    private static string LastSegment(string id)
    {
        var parts = id.Split('/');
        return parts.Length > 0 ? parts[^1] : id;
    }

    // MARK: - Envoi de fichier

    /// <param name="remotePath">
    /// Dossier de destination sur la machine, figé par l'appelant : dans un lot,
    /// les fichiers doivent arriver là où ils ont été déposés, même si
    /// l'utilisateur a changé de dossier entre-temps.
    /// </param>
    /// <remarks>
    /// La relecture du dossier n'est pas faite ici : sur un lot, elle serait
    /// refaite à chaque fichier. C'est à l'appelant de relire une fois le lot
    /// terminé — l'agent ne renvoie pas la nouvelle liste de lui-même.
    /// </remarks>
    public async Task UploadAsync(string localPath, string remotePath,
                                  CancellationToken ct = default)
    {
        if (Transfer is not null)
            throw new MeshException("Un transfert est déjà en cours.");
        if (State != FilesSessionState.Ready)
            throw new MeshException(CloseReason ?? "La session de fichiers n'est pas ouverte.");

        ct.ThrowIfCancellationRequested();

        var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                    ChunkSize, FileOptions.SequentialScan);
        var name = Path.GetFileName(localPath);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int reqid;

        lock (_uploadLock)
        {
            reqid = NextRequestId();
            _uploadReqId = reqid;
            _uploadStream = stream;
            _uploadLength = stream.Length;
            _uploadOffset = 0;
            _uploadInFlight = 0;
            _uploadDoneSent = false;
            _uploadCompletion = completion;
        }

        SetTransfer(new TransferProgress
        {
            FileName = name, Total = stream.Length, IsUpload = true,
        });

        try
        {
            await SendJsonAsync(new
            {
                action = "upload",
                reqid,
                path = remotePath,
                name,
                size = stream.Length,
                append = false,
            }).ConfigureAwait(false);

            using (ct.Register(() => CancelTransfer()))
            {
                await completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_uploadLock)
            {
                if (ReferenceEquals(_uploadStream, stream)) _uploadStream = null;
                if (ReferenceEquals(_uploadCompletion, completion)) _uploadCompletion = null;
            }
            // Hors du verrou : PumpUpload ne lit le flux que sous ce verrou, et
            // ne peut donc plus s'en servir une fois la référence effacée.
            stream.Dispose();
            SetTransfer(null);
        }
    }

    /// <summary>
    /// Envoie des fragments tant que la fenêtre n'est pas pleine ; appelé au
    /// démarrage puis à chaque acquittement.
    /// </summary>
    private void PumpUpload()
    {
        long acknowledged;
        long length;

        lock (_uploadLock)
        {
            var stream = _uploadStream;
            if (stream is null) return;

            try
            {
                while (_uploadInFlight < UploadWindow && _uploadOffset < _uploadLength)
                {
                    int size = (int)Math.Min(ChunkSize, _uploadLength - _uploadOffset);
                    var chunk = new byte[size];
                    int read = stream.ReadAtLeast(chunk, size, throwOnEndOfStream: false);
                    if (read == 0)
                    {
                        // Fichier raccourci pendant l'envoi : on clôt sur ce qui a été lu.
                        _uploadLength = _uploadOffset;
                        break;
                    }
                    if (read < size) chunk = chunk[..read];

                    _uploadOffset += read;
                    _uploadInFlight++;

                    // Un fragment commençant par '{' ou 0x00 serait confondu avec une
                    // commande JSON : on le préfixe d'un octet d'échappement 0x00.
                    if (chunk[0] == 0x7B || chunk[0] == 0x00)
                    {
                        var escaped = new byte[chunk.Length + 1];
                        escaped[0] = 0x00;
                        Array.Copy(chunk, 0, escaped, 1, chunk.Length);
                        chunk = escaped;
                    }
                    _sendQueue.Writer.TryWrite(chunk);
                }
            }
            catch (Exception ex)
            {
                // Fichier devenu illisible en cours de route (support retiré,
                // verrou posé par une autre application) : l'agent doit effacer
                // ce qu'il a déjà écrit.
                _ = SendJsonAsync(new { action = "uploadcancel", reqid = _uploadReqId });
                _uploadCompletion?.TrySetException(
                    new MeshException($"Lecture du fichier impossible : {ex.Message}"));
                _uploadCompletion = null;
                _uploadStream = null;
                return;
            }

            // Progression approximative : octets effectivement acquittés par l'agent.
            acknowledged = Math.Max(0, _uploadOffset - (long)_uploadInFlight * ChunkSize);
            length = _uploadLength;

            if (_uploadOffset >= _uploadLength && _uploadInFlight == 0 && !_uploadDoneSent)
            {
                _uploadDoneSent = true;
                _ = SendJsonAsync(new { action = "uploaddone", reqid = _uploadReqId });
            }
        }

        UpdateProgress(acknowledged, length);
    }

    public void CancelTransfer()
    {
        lock (_uploadLock)
        {
            if (_uploadCompletion is not null)
            {
                _ = SendJsonAsync(new { action = "uploadcancel", reqid = _uploadReqId });
                _uploadCompletion.TrySetCanceled();
                _uploadCompletion = null;
                _uploadStream = null;
            }
        }
        SetTransfer(null);
    }

    private void SetTransfer(TransferProgress? progress)
    {
        Transfer = progress;
        _ui.BeginInvoke(() => TransferChanged?.Invoke());
    }

    /// <summary>
    /// Met à jour la progression, en limitant la fréquence des notifications :
    /// redessiner l'interface à chaque fragment volerait du temps au transfert.
    /// </summary>
    private void UpdateProgress(long transferred, long total)
    {
        if (Transfer is not { } transfer) return;
        var now = DateTime.UtcNow;
        bool finished = transferred >= total;
        if (!finished && (now - _lastProgressUpdate).TotalMilliseconds < 100) return;

        _lastProgressUpdate = now;
        transfer.Transferred = transferred;
        if (total > 0) transfer.Total = total;
        _ui.BeginInvoke(() => TransferChanged?.Invoke());
    }

    // MARK: - Réception

    private void StartSenderLoop(MeshWebSocket socket, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var data in _sendQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try { await socket.SendAsync(data, ct).ConfigureAwait(false); }
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
                    // Libérer un envoi en cours pour ne pas laisser l'interface figée.
                    var lost = new MeshException("La connexion a été perdue pendant le transfert.");
                    lock (_uploadLock)
                    {
                        _uploadCompletion?.TrySetException(lost);
                        _uploadCompletion = null;
                        _uploadStream = null;
                    }
                    FailProbes(lost);
                    SetTransfer(null);
                    if (State != FilesSessionState.Closed)
                    {
                        CloseReason = "La session de fichiers s'est terminée.";
                        SetState(FilesSessionState.Closed);
                    }
                    return;
                }

                try
                {
                    switch (message)
                    {
                        case WsMessage.Text(var text):
                            HandleText(text);
                            break;
                        case WsMessage.Binary(var data) when data.Length > 0 && data[0] == 0x7B:
                            // '{' → commande JSON encapsulée en binaire.
                            HandleText(Encoding.UTF8.GetString(data));
                            break;
                        // Les autres trames binaires seraient des fragments de
                        // téléchargement par relais : on passe par HTTPS, donc rien à faire.
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"Trame ignorée : {ex.Message}", "Fichiers");
                }
            }
        }, ct);
    }

    private void FailProbes(Exception reason)
    {
        foreach (var probe in _probes.Values)
        {
            if (reason is OperationCanceledException) probe.TrySetCanceled();
            else probe.TrySetException(reason);
        }
    }

    private static List<FileEntry> ParseEntries(JsonElement dir)
    {
        var entries = new List<FileEntry>();
        foreach (var item in dir.EnumerateArray())
            if (FileEntry.From(item) is { } entry) entries.Add(entry);

        entries.Sort(FileEntry.CompareForDisplay);
        return entries;
    }

    private void HandleText(string text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '{') return;

        JsonElement json;
        try
        {
            using var doc = JsonDocument.Parse(text);
            json = doc.RootElement.Clone();
        }
        catch { return; }

        var action = json.TryGetProperty("action", out var a) ? a.GetString() : null;

        // Réponse à un « ls » : la charge utile est dans « dir ».
        if (json.TryGetProperty("dir", out var dir))
        {
            // Lecture demandée par un appelant : elle ne concerne pas l'affichage.
            if (json.TryGetProperty("reqid", out var r) && r.ValueKind == JsonValueKind.Number
                && r.TryGetInt32(out var reqid) && _probes.TryRemove(reqid, out var probe))
            {
                probe.TrySetResult(dir.ValueKind == JsonValueKind.Array ? ParseEntries(dir) : null);
                return;
            }

            _entries.Clear();
            if (dir.ValueKind == JsonValueKind.Array)
            {
                _entries.AddRange(ParseEntries(dir));
                LastError = null;
            }
            else
            {
                // dir:null → dossier illisible (droits insuffisants, chemin absent).
                LastError = "Ce dossier n'a pas pu être lu.";
            }

            IsLoading = false;
            _ui.BeginInvoke(() => EntriesChanged?.Invoke());
            return;
        }

        switch (action)
        {
            case "uploadstart":
                PumpUpload();
                break;

            case "uploadack":
                lock (_uploadLock)
                {
                    if (_uploadInFlight > 0) _uploadInFlight--;
                }
                PumpUpload();
                break;

            case "uploaddone":
                lock (_uploadLock)
                {
                    _uploadCompletion?.TrySetResult(true);
                    _uploadCompletion = null;
                }
                break;

            case "uploaderror":
                lock (_uploadLock)
                {
                    _uploadCompletion?.TrySetException(
                        new MeshException("L'envoi a été refusé par la machine distante."));
                    _uploadCompletion = null;
                }
                break;

            case "refresh":
                _ = ListAsync();
                break;
        }
    }

    private Task SendJsonAsync(object obj)
    {
        // Les commandes JSON circulent en trames BINAIRES sur ce tunnel.
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        _sendQueue.Writer.TryWrite(bytes);
        return Task.CompletedTask;
    }

    public void Dispose() => Stop();
}
