using System.Windows.Input;

namespace MeshControl.Ssh;

/// <summary>
/// Traduction des touches WPF en séquences ANSI attendues par un shell distant.
/// Partagé entre le terminal via agent et le terminal SSH, qui parlent le même
/// langage côté clavier.
/// </summary>
public static class TerminalKeys
{
    /// <summary>Séquences ANSI des touches spéciales (VT100 / xterm).</summary>
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

    /// <summary>
    /// Ctrl+lettre : caractères de contrôle 0x01-0x1A (Ctrl+C = 0x03, Ctrl+D = 0x04…).
    /// Renvoie null si la combinaison n'en est pas une.
    /// </summary>
    public static string? MapControlChar(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
            return ((char)(key - Key.A + 1)).ToString();
        return null;
    }
}
