using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace MeshControl.Core;

/// <summary>
/// Enregistre un raccourci clavier **global** auprès de Windows : il se déclenche
/// même quand l'application n'a pas le focus. Repose sur RegisterHotKey (user32),
/// que WPF n'expose pas nativement.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xB01D;   // identifiant arbitraire, unique dans notre fenêtre

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [Flags]
    private enum HotkeyModifiers : uint
    {
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000,
    }

    private readonly Window _owner;
    private HwndSource? _source;
    private bool _registered;

    /// <summary>Déclenché (sur le thread UI) quand le raccourci est pressé.</summary>
    public event Action? Pressed;

    public GlobalHotkey(Window owner) => _owner = owner;

    /// <summary>
    /// (Ré)enregistre le raccourci. Renvoie false si Windows le refuse — typiquement
    /// parce qu'une autre application l'a déjà réservé.
    /// </summary>
    public bool Register(ModifierKeys modifiers, Key key)
    {
        Unregister();

        var handle = new WindowInteropHelper(_owner).Handle;
        if (handle == IntPtr.Zero) return false;   // fenêtre pas encore créée

        _source ??= HwndSource.FromHwnd(handle);
        if (_source is null) return false;
        _source.AddHook(WndProc);

        uint mods = (uint)HotkeyModifiers.NoRepeat;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= (uint)HotkeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= (uint)HotkeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= (uint)HotkeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= (uint)HotkeyModifiers.Win;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return false;

        _registered = RegisterHotKey(handle, HotkeyId, mods, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            var handle = new WindowInteropHelper(_owner).Handle;
            if (handle != IntPtr.Zero) UnregisterHotKey(handle, HotkeyId);
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source = null;
    }
}
