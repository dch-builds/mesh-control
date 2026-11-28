using System.Runtime.InteropServices;
using Avalonia.Threading;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>
/// Raccourci clavier global, actif même quand l'application n'a pas le focus
/// (portage de Core/GlobalHotkey.cs, qui repose sur RegisterHotKey de Windows).
///
/// Sous macOS, RegisterEventHotKey (Carbon) : la seule voie qui n'exige pas
/// l'autorisation « Surveillance de l'accessibilité » dans les réglages de
/// confidentialité. Le raccourci est ⌃⌥W, l'équivalent de Ctrl+Alt+W sous
/// Windows. Ailleurs, la classe ne fait rien.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    // Modificateurs Carbon (Events.h).
    private const uint ControlKey = 0x1000;
    private const uint OptionKey = 0x0800;

    /// <summary>Code de touche virtuel de W (kVK_ANSI_W).</summary>
    private const uint KeyW = 13;

    private const uint EventClassKeyboard = 0x6B657962;   // 'keyb'
    private const uint EventHotKeyPressed = 5;
    private const uint Signature = 0x4D534843;            // 'MSHC'

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

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
    private readonly EventHandlerProc _handler;
    private IntPtr _handlerRef;
    private IntPtr _hotKeyRef;

    /// <summary>Le raccourci a été pressé (déjà ramené sur le fil d'interface).</summary>
    public event Action? Pressed;

    /// <summary>Libellé du raccourci, pour l'affichage.</summary>
    public static string Label => OperatingSystem.IsMacOS() ? "⌃⌥W" : "Ctrl+Alt+W";

    public bool IsRegistered => _hotKeyRef != IntPtr.Zero;

    public GlobalHotkey()
    {
        _handler = OnHotKey;
        if (!OperatingSystem.IsMacOS()) return;

        try
        {
            var target = GetApplicationEventTarget();
            var types = new[] { new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed } };
            var status = InstallEventHandler(target, _handler, 1, types, IntPtr.Zero, out _handlerRef);
            if (status != 0)
            {
                Log.Warning($"Raccourci global indisponible (InstallEventHandler {status}).", "Système");
                return;
            }

            status = RegisterEventHotKey(KeyW, ControlKey | OptionKey,
                new EventHotKeyId { Signature = Signature, Id = 1 }, target, 0, out _hotKeyRef);
            if (status != 0)
            {
                _hotKeyRef = IntPtr.Zero;
                Log.Warning($"Raccourci {Label} déjà utilisé par une autre application (erreur {status}).", "Système");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Raccourci global indisponible : {ex.Message}", "Système");
        }
    }

    private int OnHotKey(IntPtr nextHandler, IntPtr theEvent, IntPtr userData)
    {
        Dispatcher.UIThread.Post(() => Pressed?.Invoke());
        return 0;
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (_hotKeyRef != IntPtr.Zero) UnregisterEventHotKey(_hotKeyRef);
        if (_handlerRef != IntPtr.Zero) RemoveEventHandler(_handlerRef);
        _hotKeyRef = _handlerRef = IntPtr.Zero;
    }
}
