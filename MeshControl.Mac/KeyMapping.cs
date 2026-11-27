using Avalonia.Input;

namespace MeshControl.Mac;

/// <summary>
/// Conversion des touches Avalonia en codes VK Windows, ceux qu'attend le
/// protocole KVM de MeshCentral (l'agent Linux ou macOS les traduit de son côté).
///
/// L'énumération Key d'Avalonia reprend celle de WPF, valeurs comprises : les
/// plages (A–Z, 0–9, F1–F24, pavé numérique) sont donc contiguës.
/// </summary>
internal static class KeyMapping
{
    public const byte VkShift = 0x10;
    public const byte VkControl = 0x11;
    public const byte VkAlt = 0x12;

    /// <summary>Code VK de la touche, ou 0 si elle n'a pas d'équivalent.</summary>
    public static byte ToVirtualKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return (byte)(0x41 + (key - Key.A));
        if (key is >= Key.D0 and <= Key.D9) return (byte)(0x30 + (key - Key.D0));
        if (key is >= Key.F1 and <= Key.F24) return (byte)(0x70 + (key - Key.F1));
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return (byte)(0x60 + (key - Key.NumPad0));

        return key switch
        {
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Clear => 0x0C,
            Key.Enter => 0x0D,
            Key.Pause => 0x13,
            Key.CapsLock => 0x14,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.End => 0x23,
            Key.Home => 0x24,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.PrintScreen => 0x2C,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.Help => 0x2F,
            Key.Apps => 0x5D,
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Separator => 0x6C,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,
            Key.NumLock => 0x90,
            Key.Scroll => 0x91,
            Key.LeftShift => 0xA0,
            Key.RightShift => 0xA1,
            Key.LeftCtrl => 0xA2,
            Key.RightCtrl => 0xA3,
            Key.LeftAlt => 0xA4,
            Key.RightAlt => 0xA5,
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            Key.Oem8 => 0xDF,
            Key.OemBackslash => 0xE2,
            _ => 0,
        };
    }

    /// <summary>
    /// Touches qui produisent un caractère. Sans Ctrl ni Cmd, elles passent par
    /// la saisie de texte (chemin Unicode du protocole) plutôt que par leur
    /// code VK : le caractère arrive tel que tapé, quelle que soit la
    /// disposition du clavier distant, accents et touches mortes compris. Sur
    /// Mac, Option sert justement à composer des caractères (@, [, | sur un
    /// clavier français) : il ne fait donc pas basculer vers les codes VK.
    /// </summary>
    public static bool IsCharacterKey(Key key) =>
        key is >= Key.A and <= Key.Z
            or >= Key.D0 and <= Key.D9
            or >= Key.NumPad0 and <= Key.NumPad9
            or Key.Multiply or Key.Add or Key.Subtract or Key.Decimal or Key.Divide
            or Key.OemSemicolon or Key.OemPlus or Key.OemComma or Key.OemMinus
            or Key.OemPeriod or Key.OemQuestion or Key.OemTilde or Key.OemOpenBrackets
            or Key.OemPipe or Key.OemCloseBrackets or Key.OemQuotes or Key.Oem8
            or Key.OemBackslash or Key.DeadCharProcessed;

    /// <summary>Modificateurs, gérés à part (envoi différé, voir DesktopWindow).</summary>
    public static bool IsModifier(Key key) =>
        key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;
}
