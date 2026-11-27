using Avalonia.Controls;
using Avalonia.Input;

namespace MeshControl.Mac;

/// <summary>
/// Séquences envoyées au shell pour les touches spéciales — portage de
/// SSH/TerminalKeys.cs (les noms de touches d'Avalonia sont ceux de WPF).
/// </summary>
public static class TerminalKeys
{
    public static string? MapSpecialKey(Key key) => key switch
    {
        Key.Enter => "\r",
        Key.Back => "\u007F",          // DEL : convention des shells Unix
        Key.Tab => "\t",
        Key.Escape => "\u001B",
        Key.Up => "\u001B[A",
        Key.Down => "\u001B[B",
        Key.Right => "\u001B[C",
        Key.Left => "\u001B[D",
        Key.Home => "\u001B[H",
        Key.End => "\u001B[F",
        Key.PageUp => "\u001B[5~",
        Key.PageDown => "\u001B[6~",
        Key.Insert => "\u001B[2~",
        Key.Delete => "\u001B[3~",
        Key.F1 => "\u001BOP",
        Key.F2 => "\u001BOQ",
        Key.F3 => "\u001BOR",
        Key.F4 => "\u001BOS",
        Key.F5 => "\u001B[15~",
        Key.F6 => "\u001B[17~",
        Key.F7 => "\u001B[18~",
        Key.F8 => "\u001B[19~",
        Key.F9 => "\u001B[20~",
        Key.F10 => "\u001B[21~",
        Key.F11 => "\u001B[23~",
        Key.F12 => "\u001B[24~",
        _ => null,
    };

    /// <summary>Ctrl+lettre : caractère de contrôle (Ctrl+C interrompt, etc.).</summary>
    public static string? MapControlChar(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)(key - Key.A + 1)).ToString();
        return null;
    }
}

/// <summary>
/// Collage dans le terminal — portage de SSH/TerminalPaste.cs : collage
/// protégé si le shell le permet, confirmation avant d'exécuter plusieurs
/// lignes sinon.
/// </summary>
public static class TerminalPaste
{
    private const string BracketStart = "\u001B[200~";
    private const string BracketEnd = "\u001B[201~";

    public static async Task<string?> PrepareAsync(string? text, bool bracketed, Window owner)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var normalized = RemoveControlCharacters(text.Replace("\r\n", "\n").Replace('\r', '\n'));

        if (bracketed) return BracketStart + normalized.Replace("\n", "\r") + BracketEnd;

        var trimmed = normalized.TrimEnd('\n');
        if (trimmed.Length == 0) return null;

        var lineCount = trimmed.Split('\n').Length;
        if (lineCount == 1) return trimmed;

        var confirmed = await Dialogs.ConfirmAsync(owner, $"Coller {lineCount} lignes ?",
            "Le shell exécutera chaque ligne comme une commande, dès sa réception.", "Coller");
        return confirmed ? normalized.Replace("\n", "\r") : null;
    }

    public static string RemoveControlCharacters(string text)
    {
        if (!text.Any(IsUnwantedControl)) return text;
        return new string(text.Where(c => !IsUnwantedControl(c)).ToArray());
    }

    private static bool IsUnwantedControl(char c) =>
        (c < 0x20 && c is not '\t' and not '\n') || (c >= 0x7F && c <= 0x9F);
}
