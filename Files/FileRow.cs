namespace MeshControl.Files;

/// <summary>Ligne de fichier affichée dans la liste (adaptateur pour le binding WPF).</summary>
public sealed class FileRow(FileEntry entry)
{
    public FileEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public string Glyph => Entry.Kind switch
    {
        FileKind.Drive => "▤",
        FileKind.Directory => "▸",
        _ => "·",
    };

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
}
