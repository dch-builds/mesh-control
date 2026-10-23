using System.Runtime.InteropServices;

namespace MeshControl.UI;

/// <summary>
/// Accès aux dimensions du moniteur qui porte une fenêtre. Windows distingue
/// deux rectangles : la DALLE entière, et la ZONE DE TRAVAIL, qui en retranche
/// la barre des tâches. Confondre les deux est l'erreur classique des fenêtres
/// dont l'application dessine le cadre.
/// </summary>
internal static class MonitorInterop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left, Top, Right, Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>
    /// Dimensions du moniteur le plus proche de la fenêtre. Renvoie faux si
    /// Windows ne les donne pas : à l'appelant de s'en passer.
    /// </summary>
    internal static bool TryGetBounds(IntPtr window, out NativeRect monitor, out NativeRect workArea)
    {
        monitor = workArea = default;
        if (window == IntPtr.Zero) return false;

        var handle = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (handle == IntPtr.Zero) return false;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(handle, ref info)) return false;

        monitor = info.Monitor;
        workArea = info.WorkArea;
        return true;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
