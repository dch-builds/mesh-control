using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using MeshControl.Core;
using MeshControl.UI;

namespace MeshControl.Files;

/// <summary>Un fichier à envoyer, et le sous-dossier où le ranger.</summary>
/// <param name="RelativeDirectory">
/// Segments du dossier d'arrivée sous la destination, vide pour un fichier
/// déposé seul. Un dossier glissé garde ainsi son arborescence.
/// </param>
public sealed record OutgoingFile(string LocalPath, IReadOnlyList<string> RelativeDirectory, long Size);

/// <summary>
/// Un élément choisi par l'utilisateur : un fichier, ou un dossier entier avec
/// tout son contenu.
/// </summary>
public sealed class OutgoingEntry
{
    private OutgoingEntry(string path, bool isDirectory, List<OutgoingFile> files)
    {
        Path = path;
        IsDirectory = isDirectory;
        Files = files;
        TotalSize = files.Sum(f => f.Size);
        _icon = isDirectory ? FileIcon.Folder : FileIcon.ForName(path);
    }

    public string Path { get; }
    public bool IsDirectory { get; }
    public IReadOnlyList<OutgoingFile> Files { get; }
    public long TotalSize { get; }

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));

    public string Detail => IsDirectory
        ? $"Dossier · {Files.Count} fichier{(Files.Count > 1 ? "s" : "")} · {FileRow.FormatBytes(TotalSize)}"
        : FileRow.FormatBytes(TotalSize);

    private readonly FileIcon _icon;
    public string Glyph => _icon.Glyph;
    public string GlyphColor => _icon.Color;
    public string GlyphBackground => _icon.Background;

    /// <summary>
    /// Prépare un élément. Un dossier est parcouru en entier, fichiers cachés
    /// compris, sans suivre les liens qui pourraient boucler.
    /// </summary>
    /// <returns>Null si le chemin n'existe plus ou n'est pas lisible.</returns>
    public static OutgoingEntry? From(string path)
    {
        try
        {
            if (File.Exists(path))
                return new OutgoingEntry(path, false,
                    [new OutgoingFile(path, [], new FileInfo(path).Length)]);

            if (!Directory.Exists(path)) return null;

            var root = new DirectoryInfo(path);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
            };

            var files = new List<OutgoingFile>();
            foreach (var file in root.EnumerateFiles("*", options))
            {
                var segments = new List<string> { root.Name };
                var relative = System.IO.Path.GetRelativePath(root.FullName, file.DirectoryName!);
                if (relative != ".") segments.AddRange(relative.Split(System.IO.Path.DirectorySeparatorChar));
                files.Add(new OutgoingFile(file.FullName, segments, file.Length));
            }
            return new OutgoingEntry(path, true, files);
        }
        catch
        {
            return null;
        }
    }
}

public enum DeviceSendState { Waiting, Connecting, Sending, Done, Failed, Cancelled }

/// <summary>
/// Une machine de la liste : candidate à la sélection d'abord, puis carte de
/// suivi de son envoi.
/// </summary>
public sealed class TargetDevice : INotifyPropertyChanged
{
    public TargetDevice(MeshNode node, string groupName)
    {
        Node = node;
        GroupName = groupName;
        Row = new DeviceRow(node, groupName);
    }

    public MeshNode Node { get; }
    public string GroupName { get; }

    /// <summary>Ligne d'appareil, pour reprendre ses pastilles d'OS à l'identique.</summary>
    public DeviceRow Row { get; }

    public string Name => Node.Name;
    public bool IsWindows => Node.OsFamily == OSFamily.Windows;

    public string Subtitle => string.IsNullOrEmpty(Node.OsDescription)
        ? GroupName
        : $"{GroupName} · {Node.OsDescription}";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Notify();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Prévient la fenêtre, qui recompte la sélection.</summary>
    public event Action? SelectionChanged;

    // MARK: - Suivi de l'envoi

    private DeviceSendState _state = DeviceSendState.Waiting;
    public DeviceSendState State
    {
        get => _state;
        private set
        {
            _state = value;
            NotifyAll();
        }
    }

    /// <summary>Fichiers déjà arrivés : une relance reprend après eux.</summary>
    public HashSet<OutgoingFile> Delivered { get; } = new();

    public int FilesTotal { get; private set; }
    public long BytesTotal { get; private set; }

    private long _bytesDelivered;
    private long _currentTransferred;
    private double _speed;
    private string? _currentFile;
    private string? _error;

    /// <summary>Destination effective, pour l'info-bulle de la carte.</summary>
    public string Destination { get; set; } = "";

    public long BytesDone => _bytesDelivered + _currentTransferred;
    public double BytesPerSecond => State == DeviceSendState.Sending ? _speed : 0;

    public double Fraction => State == DeviceSendState.Done ? 1
                            : BytesTotal > 0 ? Math.Min(1, (double)BytesDone / BytesTotal) : 0;

    public bool IsFinished => State is DeviceSendState.Done or DeviceSendState.Failed
                                     or DeviceSendState.Cancelled;

    public void Prepare(IReadOnlyList<OutgoingFile> files)
    {
        FilesTotal = files.Count;
        BytesTotal = files.Sum(f => f.Size);
        _bytesDelivered = files.Where(Delivered.Contains).Sum(f => f.Size);
        _currentTransferred = 0;
        _speed = 0;
        _currentFile = null;
        _error = null;
        State = DeviceSendState.Waiting;
    }

    public void Connecting() => State = DeviceSendState.Connecting;

    public void BeginFile(OutgoingFile file)
    {
        _currentFile = System.IO.Path.GetFileName(file.LocalPath);
        _currentTransferred = 0;
        if (State != DeviceSendState.Sending) State = DeviceSendState.Sending;
        else NotifyProgress();
    }

    public void Report(TransferProgress progress)
    {
        if (State != DeviceSendState.Sending) return;
        _currentTransferred = progress.Transferred;
        if (progress.BytesPerSecond > 0) _speed = progress.BytesPerSecond;
        NotifyProgress();
    }

    public void EndFile(OutgoingFile file)
    {
        Delivered.Add(file);
        _bytesDelivered += file.Size;
        _currentTransferred = 0;
        NotifyProgress();
    }

    public void Complete() => State = DeviceSendState.Done;

    public void Fail(string message)
    {
        _error = message;
        State = DeviceSendState.Failed;
    }

    public void Cancel() => State = DeviceSendState.Cancelled;

    // MARK: - Affichage de la carte

    public string StatusText => State switch
    {
        DeviceSendState.Waiting => "En attente",
        DeviceSendState.Connecting => "Connexion…",
        DeviceSendState.Sending => $"{Delivered.Count + 1} / {FilesTotal} · {_currentFile}",
        DeviceSendState.Done => FilesTotal == 1 ? "Fichier reçu" : $"{FilesTotal} fichiers reçus",
        DeviceSendState.Failed => _error ?? "Échec",
        _ => "Annulé",
    };

    public string DetailText
    {
        get
        {
            if (State == DeviceSendState.Done) return FileRow.FormatBytes(BytesTotal);
            if (State != DeviceSendState.Sending)
                return Delivered.Count > 0
                    ? $"{Delivered.Count} sur {FilesTotal} déjà reçus"
                    : FileRow.FormatBytes(BytesTotal);

            var parts = new List<string>
            {
                $"{FileRow.FormatBytes(BytesDone)} sur {FileRow.FormatBytes(BytesTotal)}",
            };
            if (FileRow.FormatSpeed(_speed) is { Length: > 0 } rate) parts.Add(rate);
            return string.Join(" · ", parts);
        }
    }

    public string StatusColor => State switch
    {
        DeviceSendState.Failed => "#C0392B",
        DeviceSendState.Done => "#248A3D",
        _ => "#8A8A8E",
    };

    public string BarColor => State switch
    {
        DeviceSendState.Failed => "#FF9F0A",
        DeviceSendState.Done => "#34C759",
        DeviceSendState.Cancelled => "#C7C7CC",
        _ => "#0A84FF",
    };

    public bool IsIndeterminate => State == DeviceSendState.Connecting;

    public string StateGlyph => State switch
    {
        DeviceSendState.Done => "\uE73E",
        DeviceSendState.Failed => "\uE783",
        DeviceSendState.Cancelled => "\uE711",
        _ => "",
    };

    public Visibility RetryVisibility =>
        State is DeviceSendState.Failed or DeviceSendState.Cancelled
            ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StateGlyphVisibility =>
        StateGlyph.Length > 0 && RetryVisibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

    // MARK: - Notification de changement

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    private void NotifyProgress()
    {
        Notify(nameof(Fraction));
        Notify(nameof(StatusText));
        Notify(nameof(DetailText));
    }
}
