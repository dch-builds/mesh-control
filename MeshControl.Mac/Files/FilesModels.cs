using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using MeshControl.Files;

namespace MeshControl.Mac;

/// <summary>
/// Pictogramme et teintes d'un type de fichier — portage de FileIcon
/// (Files/FileRow.cs, version Windows) : mêmes familles, mêmes couleurs.
/// </summary>
public sealed record FileIcon(IconKind Glyph, string LightColor, string LightBackground)
{
    public IBrush Color => Palette.Brush(LightColor);
    public IBrush Background => Palette.Brush(LightBackground);

    public static readonly FileIcon Drive = new(IconKind.Drive, "#636366", "#EFEFF4");
    public static readonly FileIcon Folder = new(IconKind.Folder, "#E89B0C", "#FEF3DC");
    private static readonly FileIcon Image = new(IconKind.Image, "#248A3D", "#E4F8E8");
    private static readonly FileIcon Video = new(IconKind.Video, "#AF52DE", "#F5EAFB");
    private static readonly FileIcon Audio = new(IconKind.Audio, "#FF2D55", "#FFE8ED");
    private static readonly FileIcon Archive = new(IconKind.Archive, "#A2845E", "#F4EEE6");
    private static readonly FileIcon Code = new(IconKind.Code, "#5856D6", "#ECECFB");
    private static readonly FileIcon Program = new(IconKind.Terminal, "#30B0C7", "#E3F5F8");
    private static readonly FileIcon Document = new(IconKind.Document, "#0A84FF", "#E8F2FE");
    private static readonly FileIcon Pdf = new(IconKind.Document, "#E5383B", "#FDE8E7");
    private static readonly FileIcon Other = new(IconKind.FileGeneric, "#8A8A8E", "#F0F0F2");

    public static FileIcon ForName(string name)
        => Path.GetExtension(name).TrimStart('.').ToLowerInvariant() switch
        {
            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "heic" or "svg" or "ico" or "tif" or "tiff"
                => Image,
            "mp4" or "mkv" or "avi" or "mov" or "wmv" or "webm" or "m4v" => Video,
            "mp3" or "wav" or "flac" or "ogg" or "m4a" or "aac" or "wma" => Audio,
            "zip" or "7z" or "rar" or "tar" or "gz" or "tgz" or "bz2" or "xz" or "cab" or "iso" => Archive,
            "cs" or "js" or "ts" or "py" or "ps1" or "sh" or "bat" or "cmd" or "json" or "xml" or "yml"
                or "yaml" or "html" or "css" or "sql" or "ini" or "conf" or "cfg" or "log" => Code,
            "exe" or "msi" or "msix" or "appx" or "dll" or "deb" or "rpm" or "pkg" or "dmg" or "apk" => Program,
            "pdf" => Pdf,
            "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "odt" or "ods" or "odp" or "txt"
                or "rtf" or "csv" or "md" => Document,
            _ => Other,
        };
}

/// <summary>Ligne de la liste des fichiers (portage de Files/FileRow.cs).</summary>
public sealed class FileRow(FileEntry entry)
{
    public FileEntry Entry { get; } = entry;
    public string Name => Entry.Name;

    private FileIcon Icon => Entry.Kind switch
    {
        FileKind.Drive => FileIcon.Drive,
        FileKind.Directory => FileIcon.Folder,
        _ => FileIcon.ForName(Entry.Name),
    };

    public IconKind Glyph => Icon.Glyph;
    public IBrush GlyphColor => Icon.Color;
    public IBrush GlyphBackground => Icon.Background;

    public string KindText => Entry.Kind switch
    {
        FileKind.Drive => Entry.DriveType is { } dt ? $"Lecteur ({dt})" : "Lecteur",
        FileKind.Directory => "Dossier",
        _ => Path.GetExtension(Entry.Name) is { Length: > 0 } ext
            ? $"Fichier {ext.TrimStart('.').ToUpperInvariant()}"
            : "Fichier",
    };

    public string SizeText => Entry.Kind switch
    {
        FileKind.Drive => Entry.FreeBytes is { } free ? $"{FormatBytes(free)} libres" : "",
        FileKind.Directory => "",
        _ => FormatBytes(Entry.Size),
    };

    public string ModifiedText => Entry.Modified?.ToString("dd/MM/yyyy HH:mm") ?? "";
    public bool IsDirectory => Entry.Kind != FileKind.File;

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "";
        string[] units = ["o", "Ko", "Mo", "Go", "To"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} {units[0]}"
            : $"{value.ToString(value < 10 ? "0.0" : "0")} {units[unit]}";
    }

    public static string FormatSpeed(double bytesPerSecond)
        => bytesPerSecond < 1 ? "" : $"{FormatBytes((long)bytesPerSecond)}/s";

    public static string FormatRemaining(long remainingBytes, double bytesPerSecond)
    {
        if (bytesPerSecond < 1 || remainingBytes <= 0) return "";
        var seconds = remainingBytes / bytesPerSecond;
        if (seconds < 1) return "moins d'une seconde";
        if (seconds < 60) return $"{(int)Math.Ceiling(seconds)} s restantes";
        if (seconds < 3600)
        {
            var minutes = (int)(seconds / 60);
            var rest = (int)(seconds % 60);
            return minutes < 10 && rest > 0 ? $"{minutes} min {rest:00} s restantes" : $"{minutes} min restantes";
        }
        return $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60):00} restantes";
    }
}

public enum TransferState { Pending, Running, Done, Failed, Cancelled }

/// <summary>Un fichier de la file de transferts (portage de Files/TransferItem.cs).</summary>
public sealed class TransferItem : INotifyPropertyChanged
{
    private TransferItem(bool isUpload, string localPath, string remotePath, FileEntry? entry,
                         long size, bool ensureRemoteDirectory)
    {
        IsUpload = isUpload;
        LocalPath = localPath;
        RemotePath = remotePath;
        Entry = entry;
        Size = size;
        EnsureRemoteDirectory = ensureRemoteDirectory;
        Name = isUpload ? Path.GetFileName(localPath) : entry!.Name;
        _icon = FileIcon.ForName(Name);
    }

    public static TransferItem Upload(string localPath, string remoteDirectory, long size,
                                      bool ensureRemoteDirectory = false)
        => new(true, localPath, remoteDirectory, null, size, ensureRemoteDirectory);

    public static TransferItem Download(FileEntry entry, string remotePath, string destination)
        => new(false, destination, remotePath, entry, entry.Size, false);

    public bool IsUpload { get; }
    public string LocalPath { get; }
    public string RemotePath { get; }
    public FileEntry? Entry { get; }
    public long Size { get; }
    public bool EnsureRemoteDirectory { get; }
    public string Name { get; }
    internal CancellationTokenSource? Cancellation { get; set; }

    private TransferState _state = TransferState.Pending;
    public TransferState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    private long _transferred;
    public long Transferred => _transferred;
    private double _speed;
    public double BytesPerSecond => _speed;
    private string? _error;

    public double Fraction => State == TransferState.Done ? 1
                            : Size > 0 ? Math.Min(1, (double)_transferred / Size) : 0;

    public bool IsFinished => State is TransferState.Done or TransferState.Failed or TransferState.Cancelled;

    public void Start()
    {
        _transferred = 0;
        _speed = 0;
        _error = null;
        State = TransferState.Running;
    }

    public void Report(TransferProgress progress)
    {
        if (State != TransferState.Running) return;
        _transferred = progress.Transferred;
        _speed = progress.BytesPerSecond;
        Notify(nameof(Fraction));
        Notify(nameof(Detail));
    }

    public void Complete()
    {
        _transferred = Size;
        State = TransferState.Done;
    }

    public void Fail(string message)
    {
        _error = message;
        State = TransferState.Failed;
    }

    public void Cancel() => State = TransferState.Cancelled;

    public void Requeue()
    {
        _transferred = 0;
        _error = null;
        State = TransferState.Pending;
    }

    // MARK: - Affichage

    private readonly FileIcon _icon;
    public IconKind Glyph => _icon.Glyph;
    public IBrush GlyphColor => _icon.Color;
    public IBrush GlyphBackground => _icon.Background;
    public IconKind DirectionGlyph => IsUpload ? IconKind.Upload : IconKind.Download;

    public string Detail => State switch
    {
        TransferState.Pending => $"En attente · {FileRow.FormatBytes(Size)}",
        TransferState.Running => RunningDetail(),
        TransferState.Done => IsUpload ? $"Envoyé · {FileRow.FormatBytes(Size)}"
                                       : $"Téléchargé · {FileRow.FormatBytes(Size)}",
        TransferState.Failed => _error ?? "Échec",
        _ => "Annulé",
    };

    private string RunningDetail()
    {
        var parts = new List<string> { $"{FileRow.FormatBytes(_transferred)} sur {FileRow.FormatBytes(Size)}" };
        if (FileRow.FormatSpeed(_speed) is { Length: > 0 } speed) parts.Add(speed);
        if (FileRow.FormatRemaining(Size - _transferred, _speed) is { Length: > 0 } eta) parts.Add(eta);
        return string.Join(" · ", parts);
    }

    public IBrush DetailColor => Palette.Brush(State == TransferState.Failed ? "#C0392B" : "#8A8A8E");
    public string Tooltip => IsUpload ? $"{LocalPath}\n→ {RemotePath}" : $"{RemotePath}\n→ {LocalPath}";
    public bool ProgressVisible => State == TransferState.Running;

    public IconKind StateGlyph => State switch
    {
        TransferState.Done => IconKind.Check,
        TransferState.Failed => IconKind.Error,
        _ => IconKind.None,
    };

    public IBrush StateColor => Palette.Brush(State == TransferState.Failed ? "#E5383B" : "#34C759");

    public IconKind ActionGlyph => State switch
    {
        TransferState.Pending or TransferState.Running => IconKind.Close,
        TransferState.Failed or TransferState.Cancelled => IconKind.Refresh,
        TransferState.Done when !IsUpload => IconKind.Folder,
        _ => IconKind.None,
    };

    public string? ActionTooltip => State switch
    {
        TransferState.Pending or TransferState.Running => "Annuler ce transfert",
        TransferState.Failed or TransferState.Cancelled => "Réessayer",
        TransferState.Done when !IsUpload => OperatingSystem.IsMacOS() ? "Afficher dans le Finder" : "Afficher dans l'Explorateur",
        _ => null,
    };

    public bool ActionVisible => ActionGlyph != IconKind.None;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
