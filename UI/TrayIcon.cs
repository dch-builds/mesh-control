using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace MeshControl.UI;

/// <summary>
/// Icône dans la zone de notification Windows, posée directement sur l'API
/// Shell_NotifyIcon.
///
/// Pourquoi ne pas utiliser NotifyIcon de WinForms : ce n'est qu'une enveloppe
/// autour de cette même API, mais elle impose de charger System.Windows.Forms et
/// System.Drawing — une vingtaine de mégaoctets lus à chaque démarrage pour une
/// icône. Le passage en direct supprime cette dépendance sans rien changer au
/// comportement visible.
///
/// Le menu contextuel est un ContextMenu WPF : il suit le style du reste de
/// l'application, là où le menu WinForms gardait l'apparence héritée de Windows.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public event Action? OpenRequested;
    public event Action? QuickSearchRequested;
    public event Action? ExitRequested;

    /// <summary>Clic sur la dernière bulle de notification affichée.</summary>
    public event Action? BalloonClicked;

    /// <summary>Identifiant de notre icône auprès du shell (arbitraire mais stable).</summary>
    private const int IconId = 1;

    /// <summary>Message que le shell nous renvoie pour les clics sur l'icône.</summary>
    private const int WM_TRAYICON = NativeMethods.WM_APP + 1;

    private readonly HwndSource _window;
    private readonly ContextMenu _menu;
    private IntPtr _iconHandle;
    private string _tooltip = "Mesh Control";
    private bool _added;
    private bool _disposed;

    /// <summary>
    /// Message diffusé par l'Explorateur quand il recrée la zone de notification.
    /// Sans le traiter, l'icône disparaîtrait définitivement après un plantage ou
    /// un redémarrage d'explorer.exe — le piège classique de cette API.
    /// </summary>
    private readonly uint _taskbarCreatedMessage;

    public TrayIcon()
    {
        // Fenêtre invisible dédiée aux messages du shell. HwndSource est le moyen
        // natif de posséder un HWND depuis WPF, sans dépendre de WinForms.
        //
        // Volontairement PAS une fenêtre « message-only » (HWND_MESSAGE) : celles-ci
        // ne reçoivent pas les messages DIFFUSÉS, or c'est ainsi que l'Explorateur
        // annonce « TaskbarCreated » après un redémarrage. On crée donc une vraie
        // fenêtre de niveau supérieur, de taille nulle et jamais affichée, marquée
        // ToolWindow pour n'apparaître ni dans la barre des tâches ni au Alt+Tab.
        var parameters = new HwndSourceParameters("Mesh Control — zone de notification")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)NativeMethods.WS_POPUP),
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW,
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);

        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        _iconHandle = LoadApplicationIcon();
        _menu = BuildMenu();

        AddIcon();
    }

    // MARK: - Menu

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Ouvrir Mesh Control", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => OpenRequested?.Invoke();
        menu.Items.Add(open);

        var search = new MenuItem { Header = "Recherche rapide…" };
        search.Click += (_, _) => QuickSearchRequested?.Invoke();
        menu.Items.Add(search);

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Quitter" };
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        return menu;
    }

    private void ShowMenu()
    {
        // Le shell attend que la fenêtre propriétaire passe au premier plan avant
        // l'affichage du menu : sans cela, le menu resterait ouvert après un clic
        // à l'extérieur.
        NativeMethods.SetForegroundWindow(_window.Handle);

        _menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    // MARK: - Messages du shell

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // L'Explorateur vient de redémarrer : la zone de notification est vierge,
        // il faut y réinscrire notre icône.
        if (msg == _taskbarCreatedMessage)
        {
            _added = false;
            AddIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if (msg != WM_TRAYICON) return IntPtr.Zero;

        // Avec NOTIFYICON_VERSION_4, lParam n'est pas le message brut : le shell y
        // place l'événement dans le mot de poids faible et l'identifiant de
        // l'icône dans le mot de poids fort. Comparer lParam entier ne
        // correspondrait donc jamais — il faut isoler le mot faible.
        switch ((int)(lParam.ToInt64() & 0xFFFF))
        {
            case NativeMethods.WM_LBUTTONDBLCLK:
                OpenRequested?.Invoke();
                handled = true;
                break;

            case NativeMethods.WM_RBUTTONUP:
            case NativeMethods.WM_CONTEXTMENU:
                ShowMenu();
                handled = true;
                break;

            case NativeMethods.NIN_BALLOONUSERCLICK:
                BalloonClicked?.Invoke();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    // MARK: - Inscription auprès du shell

    private void AddIcon()
    {
        if (_disposed || _added) return;

        var data = CreateData();
        data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = _iconHandle;
        data.szTip = _tooltip;

        _added = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        if (!_added)
        {
            Core.Log.Warning("Icône de notification non ajoutée.", "Interface");
            return;
        }

        // Demander le comportement moderne (info-bulles enrichies).
        data = CreateData();
        data.uVersion = NativeMethods.NOTIFYICON_VERSION_4;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref data);
    }

    private NativeMethods.NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
    };

    /// <summary>Met à jour l'infobulle (état de connexion, nombre de machines…).</summary>
    public void SetTooltip(string text)
    {
        // Le champ est limité à 128 caractères, terminaison comprise.
        _tooltip = text.Length <= 127 ? text : text[..127];
        if (!_added) return;

        var data = CreateData();
        data.uFlags = NativeMethods.NIF_TIP;
        data.szTip = _tooltip;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    /// <summary>Notification discrète (utilisée à la première réduction).</summary>
    public void ShowMessage(string title, string message)
    {
        if (!_added) return;

        var data = CreateData();
        data.uFlags = NativeMethods.NIF_INFO;
        data.szInfoTitle = title.Length <= 63 ? title : title[..63];
        data.szInfo = message.Length <= 255 ? message : message[..255];
        data.dwInfoFlags = NativeMethods.NIIF_INFO;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    // MARK: - Icône

    /// <summary>
    /// Reprend l'icône de l'exécutable lui-même (app.ico, déjà embarquée par la
    /// compilation). Évite de la redessiner à l'exécution, ce qui imposait
    /// System.Drawing.
    /// </summary>
    private static IntPtr LoadApplicationIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                // Index 0 = icône principale de l'application.
                var extracted = NativeMethods.ExtractIcon(
                    NativeMethods.GetModuleHandle(null), exe, 0);

                // ExtractIcon renvoie 1 quand le fichier n'a aucune icône, et 0
                // en cas d'échec : les deux sont invalides comme handle.
                if (extracted != IntPtr.Zero && extracted != new IntPtr(1))
                    return extracted;
            }
        }
        catch { /* on retombe sur l'icône générique ci-dessous */ }

        // Repli : icône d'application par défaut de Windows. L'application reste
        // pilotable même si son icône n'a pas pu être chargée.
        return NativeMethods.LoadIcon(IntPtr.Zero, NativeMethods.IDI_APPLICATION);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            var data = CreateData();
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }

        if (_iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        _window.RemoveHook(WndProc);
        _window.Dispose();
    }
}

/// <summary>Déclarations Win32 nécessaires à la zone de notification.</summary>
internal static class NativeMethods
{
    public const int WM_APP = 0x8000;
    public const int WM_LBUTTONDBLCLK = 0x0203;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_CONTEXTMENU = 0x007B;
    /// <summary>Clic sur une bulle (WM_USER + 5), reçu dans le mot faible de lParam.</summary>
    public const int NIN_BALLOONUSERCLICK = 0x0405;

    public const uint WS_POPUP = 0x80000000;
    /// <summary>Fenêtre outil : absente de la barre des tâches et du Alt+Tab.</summary>
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    public const int NIM_ADD = 0x00;
    public const int NIM_MODIFY = 0x01;
    public const int NIM_DELETE = 0x02;
    public const int NIM_SETVERSION = 0x04;

    public const int NIF_MESSAGE = 0x01;
    public const int NIF_ICON = 0x02;
    public const int NIF_TIP = 0x04;
    public const int NIF_INFO = 0x10;

    public const int NIIF_INFO = 0x01;
    public const int NOTIFYICON_VERSION_4 = 4;

    public static readonly IntPtr IDI_APPLICATION = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public int dwState;
        public int dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        // Union avec uTimeout dans l'API : on n'utilise que la version.
        public int uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ExtractIcon(IntPtr hInst, string exeFileName, int iconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr iconName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);
}
