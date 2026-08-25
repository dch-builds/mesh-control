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
    public double Fraction => Total > 0 ? (double)Transferred / Total : 0;
}

/// <summary>
/// Navigation de fichiers via un tunnel meshrelay p=5 : les commandes JSON sont
/// envoyées en trames BINAIRES, et toute trame entrante dont le premier octet
/// n'est pas '{' est un fragment de téléchargement.
///
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
    private const int UploadWindow = 16;      // × 64 Ko ≈ 1 Mo en vol
    private const int ChunkSize = 65536;
    private int _uploadInFlight;
    private byte[]? _uploadData;
    private int _uploadOffset;
    private int _uploadReqId;
    private TaskCompletionSource<bool>? _uploadCompletion;

    private DateTime _lastProgressUpdate = DateTime.MinValue;

    public FilesSession(MeshServerConnection connection, MeshNode node, Dispatcher ui)
    {
        _connection = connection;
        Node = node;
        _ui = ui;
    }

    /// <summary>Les agents Windows listent les lecteurs au chemin vide ; les autres partent de /.</summary>
    private bool IsWindows => Node.OsFamily == OSFamily.Windows;

    public string CurrentPath =>
        _pathComponents.Count == 0 ? (IsWindows ? "" : "/") : string.Join("/", _pathComponents);

    public string DisplayPath =>
        _pathComponents.Count == 0 ? (IsWindows ? "Cet ordinateur" : "/")
                                   : string.Join("/", _pathComponents);

    private void SetState(FilesSessionState state)
    {
        State = state;
        _ui.BeginInvoke(() => StateChanged?.Invoke());
    }

    // MARK: - Cycle de vie

    public async Task StartAsync()
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
            await ListAsync().ConfigureAwait(false);
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
        _socket?.Close();
        _socket?.Dispose();
        _socket = null;
        if (State != FilesSessionState.Closed) SetState(FilesSessionState.Closed);
    }

    // MARK: - Navigation

    public Task ListAsync()
    {
        IsLoading = true;
        _requestCounter++;
        return SendJsonAsync(new { action = "ls", reqid = _requestCounter, path = CurrentPath });
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

    // MARK: - Opérations sur les fichiers

    public Task CreateFolderAsync(string name)
    {
        _requestCounter++;
        var path = CombinePath(CurrentPath, name);
        return SendJsonAsync(new { action = "mkdir", reqid = _requestCounter, path });
    }

    public Task DeleteAsync(IEnumerable<string> names, bool recursive)
    {
        _requestCounter++;
        return SendJsonAsync(new
        {
            action = "rm",
            reqid = _requestCounter,
            path = CurrentPath,
            delfiles = names.ToArray(),
            rec = recursive,
        });
    }

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
    private string CombinePath(string basePath, string name)
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

        try
        {
            using var handler = new HttpClientHandler();
            if (_connection.Profile.AllowSelfSigned)
                handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;

            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                           .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? entry.Size;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(destination);

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
        finally
        {
            SetTransfer(null);
        }
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

        var data = await File.ReadAllBytesAsync(localPath, ct).ConfigureAwait(false);
        var name = Path.GetFileName(localPath);

        _requestCounter++;
        _uploadReqId = _requestCounter;
        _uploadData = data;
        _uploadOffset = 0;
        _uploadInFlight = 0;
        _uploadCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        SetTransfer(new TransferProgress
        {
            FileName = name, Total = data.LongLength, IsUpload = true,
        });

        try
        {
            await SendJsonAsync(new
            {
                action = "upload",
                reqid = _uploadReqId,
                path = remotePath,
                name,
                size = data.Length,
                append = false,
            }).ConfigureAwait(false);

            using (ct.Register(() => CancelTransfer()))
            {
                await _uploadCompletion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _uploadData = null;
            _uploadCompletion = null;
            SetTransfer(null);
        }
    }

    /// <summary>
    /// Envoie des fragments tant que la fenêtre n'est pas pleine ; appelé au
    /// démarrage puis à chaque acquittement.
    /// </summary>
    private void PumpUpload()
    {
        var data = _uploadData;
        if (data is null) return;

        while (_uploadInFlight < UploadWindow && _uploadOffset < data.Length)
        {
            int size = Math.Min(ChunkSize, data.Length - _uploadOffset);
            var chunk = new byte[size];
            Array.Copy(data, _uploadOffset, chunk, 0, size);
            _uploadOffset += size;
            _uploadInFlight++;

            // Un fragment commençant par '{' ou 0x00 serait confondu avec une
            // commande JSON : on le préfixe d'un octet d'échappement 0x00.
            if (chunk.Length > 0 && (chunk[0] == 0x7B || chunk[0] == 0x00))
            {
                var escaped = new byte[chunk.Length + 1];
                escaped[0] = 0x00;
                Array.Copy(chunk, 0, escaped, 1, chunk.Length);
                chunk = escaped;
            }
            _sendQueue.Writer.TryWrite(chunk);
        }

        // Progression approximative : octets effectivement acquittés par l'agent.
        long acknowledged = Math.Max(0, _uploadOffset - (long)_uploadInFlight * ChunkSize);
        UpdateProgress(acknowledged, data.LongLength);

        if (_uploadOffset >= data.Length && _uploadInFlight == 0)
            _ = SendJsonAsync(new { action = "uploaddone", reqid = _uploadReqId });
    }

    public void CancelTransfer()
    {
        if (_uploadCompletion is not null)
        {
            _ = SendJsonAsync(new { action = "uploadcancel", reqid = _uploadReqId });
            _uploadCompletion.TrySetCanceled();
            _uploadCompletion = null;
            _uploadData = null;
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
        if (Transfer is null) return;
        var now = DateTime.UtcNow;
        bool finished = transferred >= total;
        if (!finished && (now - _lastProgressUpdate).TotalMilliseconds < 100) return;

        _lastProgressUpdate = now;
        Transfer.Transferred = transferred;
        if (total > 0) Transfer.Total = total;
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
                    _uploadCompletion?.TrySetException(
                        new MeshException("La connexion a été perdue pendant le transfert."));
                    _uploadCompletion = null;
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
            _entries.Clear();
            if (dir.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dir.EnumerateArray())
                    if (FileEntry.From(item) is { } entry) _entries.Add(entry);

                // Dossiers et lecteurs d'abord, puis tri alphabétique.
                _entries.Sort((x, y) =>
                {
                    bool xDir = x.Kind != FileKind.File;
                    bool yDir = y.Kind != FileKind.File;
                    if (xDir != yDir) return xDir ? -1 : 1;
                    return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
                });
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
                if (_uploadInFlight > 0) _uploadInFlight--;
                PumpUpload();
                break;

            case "uploaddone":
                _uploadCompletion?.TrySetResult(true);
                _uploadCompletion = null;
                break;

            case "uploaderror":
                _uploadCompletion?.TrySetException(
                    new MeshException("L'envoi a été refusé par la machine distante."));
                _uploadCompletion = null;
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
