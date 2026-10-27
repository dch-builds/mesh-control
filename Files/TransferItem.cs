using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace MeshControl.Files;

public enum TransferState { Pending, Running, Done, Failed, Cancelled }

/// <summary>
/// Un transfert de la file, tel qu'affiché dans le panneau des transferts.
///
/// Les chemins sont figés à la mise en file : l'utilisateur peut changer de
/// dossier pendant qu'un lot s'envoie, et les fichiers doivent malgré tout
/// arriver là où il les a déposés.
/// </summary>
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

    /// <param name="remoteDirectory">Dossier distant où déposer le fichier.</param>
    /// <param name="ensureRemoteDirectory">
    /// Le dossier distant reste à créer (envoi d'un dossier entier) : il sera
    /// créé juste avant l'envoi.
    /// </param>
    public static TransferItem Upload(string localPath, string remoteDirectory, long size,
                                      bool ensureRemoteDirectory = false)
        => new(true, localPath, remoteDirectory, null, size, ensureRemoteDirectory);

    /// <param name="remotePath">Chemin complet du fichier sur la machine.</param>
    /// <param name="destination">Chemin local d'arrivée.</param>
    public static TransferItem Download(FileEntry entry, string remotePath, string destination)
        => new(false, destination, remotePath, entry, entry.Size, false);

    public bool IsUpload { get; }

    /// <summary>Envoi : fichier source. Téléchargement : fichier d'arrivée.</summary>
    public string LocalPath { get; }

    /// <summary>Envoi : dossier distant. Téléchargement : fichier distant.</summary>
    public string RemotePath { get; }

    public FileEntry? Entry { get; }
    public long Size { get; }
    public bool EnsureRemoteDirectory { get; }
    public string Name { get; }

    /// <summary>Annulation de ce seul transfert, posée pendant qu'il tourne.</summary>
    internal CancellationTokenSource? Cancellation { get; set; }

    // MARK: - État

    private TransferState _state = TransferState.Pending;
    public TransferState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            NotifyAll();
        }
    }

    private long _transferred;
    public long Transferred => _transferred;
    private double _speed;
    public double BytesPerSecond => _speed;
    private string? _error;

    public double Fraction => State == TransferState.Done ? 1
                            : Size > 0 ? Math.Min(1, (double)_transferred / Size) : 0;

    public bool IsFinished => State is TransferState.Done or TransferState.Failed
                                     or TransferState.Cancelled;

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

    /// <summary>Remet en file un transfert échoué ou annulé.</summary>
    public void Requeue()
    {
        _transferred = 0;
        _error = null;
        State = TransferState.Pending;
    }

    // MARK: - Affichage

    private readonly FileIcon _icon;
    public string Glyph => _icon.Glyph;
    public string GlyphColor => _icon.Color;
    public string GlyphBackground => _icon.Background;

    /// <summary>Petite flèche posée sur la pastille : le sens du transfert.</summary>
    public string DirectionGlyph => IsUpload ? "\uE898" : "\uE896";

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
        var parts = new List<string>
        {
            $"{FileRow.FormatBytes(_transferred)} sur {FileRow.FormatBytes(Size)}",
        };
        if (FileRow.FormatSpeed(_speed) is { Length: > 0 } speed) parts.Add(speed);
        if (FileRow.FormatRemaining(Size - _transferred, _speed) is { Length: > 0 } eta) parts.Add(eta);
        return string.Join(" · ", parts);
    }

    public string DetailColor => MeshControl.UI.Palette.Adapt(State == TransferState.Failed ? "#C0392B" : "#8A8A8E");

    /// <summary>Où va le fichier, pour l'info-bulle.</summary>
    public string Tooltip => IsUpload ? $"{LocalPath}\n→ {RemotePath}" : $"{RemotePath}\n→ {LocalPath}";

    public Visibility ProgressVisibility =>
        State == TransferState.Running ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Pictogramme d'état, à droite de la ligne.</summary>
    public string StateGlyph => State switch
    {
        TransferState.Done => "\uE73E",
        TransferState.Failed => "\uE783",
        _ => "",
    };

    public string StateColor => MeshControl.UI.Palette.Adapt(State == TransferState.Failed ? "#E5383B" : "#34C759");

    /// <summary>
    /// Action proposée sur la ligne : annuler ce qui attend ou tourne, relancer
    /// ce qui a échoué, ouvrir l'emplacement d'un fichier reçu.
    /// </summary>
    public string ActionGlyph => State switch
    {
        TransferState.Pending or TransferState.Running => "\uE711",
        TransferState.Failed or TransferState.Cancelled => "\uE72C",
        TransferState.Done when !IsUpload => "\uED25",
        _ => "",
    };

    public string ActionTooltip => State switch
    {
        TransferState.Pending or TransferState.Running => "Annuler ce transfert",
        TransferState.Failed or TransferState.Cancelled => "Réessayer",
        TransferState.Done when !IsUpload => "Afficher dans l'Explorateur",
        _ => "",
    };

    public Visibility ActionVisibility =>
        ActionGlyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // MARK: - Notification de changement

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Un changement d'état touche presque tout l'affichage de la ligne.</summary>
    private void NotifyAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
