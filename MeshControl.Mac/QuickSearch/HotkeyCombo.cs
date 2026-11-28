using Avalonia.Input;

namespace MeshControl.Mac;

/// <summary>
/// Combinaison du raccourci global (portage de AppSettings.ParseHotkey et
/// FormatHotkey). Enregistrée sous une forme commune aux deux systèmes —
/// « Ctrl+Alt+W » — et affichée à la manière de chacun : « ⌃⌥W » sous macOS.
/// Alt désigne la touche Option du Mac, Win la touche Commande.
/// </summary>
public readonly record struct HotkeyCombo(KeyModifiers Modifiers, Key Key)
{
    public static readonly HotkeyCombo Default = new(KeyModifiers.Control | KeyModifiers.Alt, Key.W);

    /// <summary>Forme enregistrée dans les réglages.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Maj");
        if (Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    /// <summary>Forme affichée : symboles sous macOS, noms ailleurs.</summary>
    public string Display
    {
        get
        {
            if (!OperatingSystem.IsMacOS()) return ToString();
            var text = "";
            if (Modifiers.HasFlag(KeyModifiers.Control)) text += "⌃";
            if (Modifiers.HasFlag(KeyModifiers.Alt)) text += "⌥";
            if (Modifiers.HasFlag(KeyModifiers.Shift)) text += "⇧";
            if (Modifiers.HasFlag(KeyModifiers.Meta)) text += "⌘";
            return text + KeyName(Key);
        }
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Space => "Espace",
        _ => key.ToString(),
    };

    public static HotkeyCombo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return null;

        var modifiers = KeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => KeyModifiers.Control,
                "alt" or "option" => KeyModifiers.Alt,
                "maj" or "shift" => KeyModifiers.Shift,
                "win" or "cmd" or "meta" => KeyModifiers.Meta,
                _ => KeyModifiers.None,
            };
        }
        if (modifiers == KeyModifiers.None) return null;

        var last = parts[^1];
        Key key;
        if (last.Length == 1 && char.IsDigit(last[0])) key = Key.D0 + (last[0] - '0');
        else if (last.Equals("Espace", StringComparison.OrdinalIgnoreCase)) key = Key.Space;
        else if (!Enum.TryParse(last, ignoreCase: true, out key)) return null;

        return VirtualKey(key) == 0 ? null : new HotkeyCombo(modifiers, key);
    }

    /// <summary>Code de touche Windows (RegisterHotKey), 0 si la touche n'est pas prise en charge.</summary>
    public static uint VirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (uint)(0x41 + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (uint)(0x30 + (key - Key.D0)),
        >= Key.F1 and <= Key.F12 => (uint)(0x70 + (key - Key.F1)),
        Key.Space => 0x20,
        _ => 0,
    };

    /// <summary>Code de touche Carbon (kVK_…), null si la touche n'est pas prise en charge.</summary>
    public static uint? MacKeyCode(Key key) => key switch
    {
        Key.A => 0x00, Key.S => 0x01, Key.D => 0x02, Key.F => 0x03, Key.H => 0x04, Key.G => 0x05,
        Key.Z => 0x06, Key.X => 0x07, Key.C => 0x08, Key.V => 0x09, Key.B => 0x0B, Key.Q => 0x0C,
        Key.W => 0x0D, Key.E => 0x0E, Key.R => 0x0F, Key.Y => 0x10, Key.T => 0x11, Key.O => 0x1F,
        Key.U => 0x20, Key.I => 0x22, Key.P => 0x23, Key.L => 0x25, Key.J => 0x26, Key.K => 0x28,
        Key.N => 0x2D, Key.M => 0x2E,
        Key.D1 => 0x12, Key.D2 => 0x13, Key.D3 => 0x14, Key.D4 => 0x15, Key.D6 => 0x16, Key.D5 => 0x17,
        Key.D9 => 0x19, Key.D7 => 0x1A, Key.D8 => 0x1C, Key.D0 => 0x1D,
        Key.Space => 0x31,
        Key.F1 => 0x7A, Key.F2 => 0x78, Key.F3 => 0x63, Key.F4 => 0x76, Key.F5 => 0x60, Key.F6 => 0x61,
        Key.F7 => 0x62, Key.F8 => 0x64, Key.F9 => 0x65, Key.F10 => 0x6D, Key.F11 => 0x67, Key.F12 => 0x6F,
        _ => null,
    };
}
