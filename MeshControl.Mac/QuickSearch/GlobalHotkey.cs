using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Raccourci clavier global, actif même quand l'application n'a pas le focus
/// (portage de Core/GlobalHotkey.cs). La combinaison se règle dans les réglages.
///
/// Sous macOS, RegisterEventHotKey (Carbon) : la seule voie qui n'exige pas
/// l'autorisation « Surveillance de l'accessibilité » dans les réglages de
/// confidentialité. Sous Windows, RegisterHotKey comme la version WPF, reçu
/// par la procédure de la fenêtre principale.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private readonly Window _owner;

    /// <summary>Le raccourci a été pressé (déjà ramené sur le fil d'interface).</summary>
    public event Action? Pressed;

    /// <summary>Combinaison enregistrée, ou null.</summary>
    public HotkeyCombo? Current { get; private set; }

    public bool IsRegistered => Current is not null;

    public GlobalHotkey(Window owner) => _owner = owner;

    /// <summary>
    /// (Ré)enregistre le raccourci. Faux si le système le refuse — presque
    /// toujours parce qu'une autre application l'a déjà réservé.
    /// </summary>
    public bool Register(HotkeyCombo combo)
    {
        Unregister();
        var ok = OperatingSystem.IsMacOS() ? RegisterMac(combo)
               : OperatingSystem.IsWindows() && RegisterWindows(combo);
        if (ok) Current = combo;
        else Log.Warning($"Raccourci {combo.Display} indisponible (déjà utilisé ?).", "Système");
        return ok;
    }

    public void Unregister()
    {
        if (OperatingSystem.IsMacOS()) UnregisterMac();
        else if (OperatingSystem.IsWindows()) UnregisterWindows();
        Current = null;
    }

    public void Dispose() => Unregister();

    private void Raise() => Dispatcher.UIThread.Post(() => Pressed?.Invoke());

    // MARK: - macOS (Carbon)

    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint CmdKey = 0x0100, ShiftKey = 0x0200, OptionKey = 0x0800, ControlKey = 0x1000;
    private const uint EventClassKeyboard = 0x6B657962;   // 'keyb'
    private const uint EventHotKeyPressed = 5;
    private const uint Signature = 0x4D534843;            // 'MSHC'

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec { public uint EventClass; public uint EventKind; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyId { public uint Signature; public uint Id; }

    private delegate int EventHandlerProc(IntPtr nextHandler, IntPtr theEvent, IntPtr userData);

    [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();
    [DllImport(Carbon)]
    private static extern int InstallEventHandler(IntPtr target, EventHandlerProc handler, uint numTypes,
                                                  EventTypeSpec[] list, IntPtr userData, out IntPtr handlerRef);
    [DllImport(Carbon)] private static extern int RemoveEventHandler(IntPtr handlerRef);
    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyId id, IntPtr target,
                                                  uint options, out IntPtr hotKeyRef);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    /// <summary>Gardé en champ : le délégué ne doit pas être collecté tant qu'il est enregistré.</summary>
    private EventHandlerProc? _macHandler;
    private IntPtr _macHandlerRef;
    private IntPtr _macHotKeyRef;

    private bool RegisterMac(HotkeyCombo combo)
    {
        if (HotkeyCombo.MacKeyCode(combo.Key) is not { } keyCode) return false;
        try
        {
            var target = GetApplicationEventTarget();
            if (_macHandlerRef == IntPtr.Zero)
            {
                _macHandler = (_, _, _) => { Raise(); return 0; };
                var types = new[] { new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed } };
                if (InstallEventHandler(target, _macHandler, 1, types, IntPtr.Zero, out _macHandlerRef) != 0) return false;
            }

            uint mods = 0;
            if (combo.Modifiers.HasFlag(KeyModifiers.Control)) mods |= ControlKey;
            if (combo.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= OptionKey;
            if (combo.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= ShiftKey;
            if (combo.Modifiers.HasFlag(KeyModifiers.Meta)) mods |= CmdKey;

            if (RegisterEventHotKey(keyCode, mods, new EventHotKeyId { Signature = Signature, Id = 1 }, target, 0,
                                    out _macHotKeyRef) == 0) return true;
            _macHotKeyRef = IntPtr.Zero;
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning($"Raccourci global indisponible : {ex.Message}", "Système");
            return false;
        }
    }

    private void UnregisterMac()
    {
        if (_macHotKeyRef != IntPtr.Zero) UnregisterEventHotKey(_macHotKeyRef);
        _macHotKeyRef = IntPtr.Zero;
    }

    // MARK: - Windows (RegisterHotKey)

    private const uint WmHotkey = 0x0312;
    private const int WindowsHotkeyId = 0xB01D;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008, ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _windowsHandle;
    private bool _windowsRegistered;
    private Win32Properties.CustomWndProcHookCallback? _wndProc;

    private bool RegisterWindows(HotkeyCombo combo)
    {
        try
        {
            _windowsHandle = _owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (_windowsHandle == IntPtr.Zero) return false;
            if (_wndProc is null)
            {
                _wndProc = (IntPtr _, uint msg, IntPtr wParam, IntPtr _, ref bool handled) =>
                {
                    if (msg == WmHotkey && wParam.ToInt32() == WindowsHotkeyId)
                    {
                        handled = true;
                        Raise();
                    }
                    return IntPtr.Zero;
                };
                Win32Properties.AddWndProcHookCallback(_owner, _wndProc);
            }

            uint mods = ModNoRepeat;
            if (combo.Modifiers.HasFlag(KeyModifiers.Control)) mods |= ModControl;
            if (combo.Modifiers.HasFlag(KeyModifiers.Alt)) mods |= ModAlt;
            if (combo.Modifiers.HasFlag(KeyModifiers.Shift)) mods |= ModShift;
            if (combo.Modifiers.HasFlag(KeyModifiers.Meta)) mods |= ModWin;

            _windowsRegistered = RegisterHotKey(_windowsHandle, WindowsHotkeyId, mods, HotkeyCombo.VirtualKey(combo.Key));
            return _windowsRegistered;
        }
        catch (Exception ex)
        {
            Log.Warning($"Raccourci global indisponible : {ex.Message}", "Système");
            return false;
        }
    }

    private void UnregisterWindows()
    {
        if (_windowsRegistered) UnregisterHotKey(_windowsHandle, WindowsHotkeyId);
        _windowsRegistered = false;
    }
}
