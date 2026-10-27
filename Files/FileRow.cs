namespace MeshControl.Files;

/// <summary>Ligne de fichier affichée dans la liste (adaptateur pour le binding WPF).</summary>
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

    /// <summary>Pictogramme Segoe MDL2 de la pastille.</summary>
    public string Glyph => Icon.Glyph;
    public string GlyphColor => Icon.Color;
    public string GlyphBackground => Icon.Background;

    public string KindText => Entry.Kind switch
    {
        FileKind.Drive => Entry.DriveType is { } dt ? $"Lecteur ({dt})" : "Lecteur",
        FileKind.Directory => "Dossier",
        _ => ExtensionLabel(),
    };

    private string ExtensionLabel()
    {
        var ext = System.IO.Path.GetExtension(Entry.Name);
        return string.IsNullOrEmpty(ext) ? "Fichier" : $"Fichier {ext.TrimStart('.').ToUpperInvariant()}";
    }

    public string SizeText => Entry.Kind switch
    {
        // Pour un lecteur, l'espace libre est plus parlant que la taille totale.
        FileKind.Drive => Entry.FreeBytes is { } free
            ? $"{FormatBytes(free)} libres" : "",
        FileKind.Directory => "",
        _ => FormatBytes(Entry.Size),
    };

    public string ModifiedText =>
        Entry.Modified?.ToString("dd/MM/yyyy HH:mm") ?? "";

    public bool IsDirectory => Entry.Kind != FileKind.File;

    /// <summary>Taille lisible : 1,4 Mo plutôt que 1468006 octets.</summary>
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

    /// <summary>Débit lisible, vide tant qu'il n'est pas mesurable.</summary>
    public static string FormatSpeed(double bytesPerSecond)
        => bytesPerSecond < 1 ? "" : $"{FormatBytes((long)bytesPerSecond)}/s";

    /// <summary>
    /// Durée restante, arrondie à ce qui sert : personne ne lit les secondes
    /// d'une attente qui se compte en heures.
    /// </summary>
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
            return minutes < 10 && rest > 0 ? $"{minutes} min {rest:00} s restantes"
                                            : $"{minutes} min restantes";
        }
        return $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60):00} restantes";
    }
}

/// <summary>
/// Pastille d'un fichier : pictogramme Segoe MDL2 et teinte selon la nature du
/// contenu. Reconnaître une image ou une archive d'un coup d'œil évite de lire
/// chaque extension dans une longue liste.
/// </summary>
/// <param name="LightColor">Teinte du pictogramme, en thème clair.</param>
/// <param name="LightBackground">Fond de la pastille, en thème clair.</param>
public sealed record FileIcon(string Glyph, string LightColor, string LightBackground)
{
    /// <summary>Teintes du thème courant (voir Palette.Adapt).</summary>
    public string Color => MeshControl.UI.Palette.Adapt(LightColor);
    public string Background => MeshControl.UI.Palette.Adapt(LightBackground);

    public static readonly FileIcon Drive = new("\uEDA2", "#636366", "#EFEFF4");
    public static readonly FileIcon Folder = new("\uF12B", "#E89B0C", "#FEF3DC");

    private static readonly FileIcon Image = new("\uEB9F", "#248A3D", "#E4F8E8");
    private static readonly FileIcon Video = new("\uE714", "#AF52DE", "#F5EAFB");
    private static readonly FileIcon Audio = new("\uE8D6", "#FF2D55", "#FFE8ED");
    private static readonly FileIcon Archive = new("\uE7B8", "#A2845E", "#F4EEE6");
    private static readonly FileIcon Code = new("\uE943", "#5856D6", "#ECECFB");
    private static readonly FileIcon Program = new("\uE756", "#30B0C7", "#E3F5F8");
    private static readonly FileIcon Document = new("\uE8A5", "#0A84FF", "#E8F2FE");
    private static readonly FileIcon Pdf = new("\uE8A5", "#E5383B", "#FDE8E7");
    private static readonly FileIcon Other = new("\uE7C3", "#8A8A8E", "#F0F0F2");

    public static FileIcon ForName(string name)
        => System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant() switch
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
