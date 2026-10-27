using System.Windows;
using System.Windows.Input;

namespace MeshControl.UI;

/// <summary>
/// Fenêtre dont l'application dessine le cadre, barre de titre comprise.
///
/// Toute l'apparence vient du style « AppWindow » (Theme.xaml) : une fenêtre qui
/// hérite d'ici n'a rien à déclarer, pas même son gabarit. Ne reste au code que
/// ce que le XAML ne sait pas faire — câbler les commandes système aux boutons,
/// et demander à Windows 11 d'arrondir les angles.
///
/// Le style se règle sur la fenêtre : il masque les boutons de réduction et
/// d'agrandissement quand <see cref="Window.ResizeMode"/> vaut NoResize, ce qui
/// convient aux boîtes de dialogue sans les traiter à part.
/// </summary>
public class ChromeWindow : Window
{
    public ChromeWindow()
    {
        // SetResourceReference plutôt qu'une affectation : le style est ainsi
        // résolu à l'ouverture, quand les dictionnaires de l'application sont
        // chargés, et une fenêtre reste libre de poser le sien.
        SetResourceReference(StyleProperty, "AppWindow");

        CommandBindings.Add(new CommandBinding(
            SystemCommands.MinimizeWindowCommand,
            (_, _) => SystemCommands.MinimizeWindow(this)));

        // Un seul bouton pour agrandir et restaurer : le gabarit change son
        // pictogramme selon l'état, le geste reste le même.
        CommandBindings.Add(new CommandBinding(
            SystemCommands.MaximizeWindowCommand,
            (_, _) =>
            {
                if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
                else SystemCommands.MaximizeWindow(this);
            }));

        // Close() et non SystemCommands.CloseWindow : la fermeture doit passer
        // par OnClosing, dont dépend le repli dans la zone de notification.
        CommandBindings.Add(new CommandBinding(
            SystemCommands.CloseWindowCommand, (_, _) => Close()));
    }

    /// <summary>
    /// Plein écran : le gabarit escamote alors la barre de titre et annule la
    /// marge qu'il réserve aux fenêtres agrandies, pour que l'image distante
    /// occupe la dalle entière.
    ///
    /// Propriété attachée plutôt qu'ordinaire : FullscreenController travaille
    /// sur des Window, sans savoir si elles portent ce cadre.
    /// </summary>
    public static readonly DependencyProperty IsFullscreenProperty =
        DependencyProperty.RegisterAttached(
            "IsFullscreen", typeof(bool), typeof(ChromeWindow),
            new PropertyMetadata(false));

    public static void SetIsFullscreen(Window window, bool value)
        => window.SetValue(IsFullscreenProperty, value);

    public static bool GetIsFullscreen(Window window)
        => (bool)window.GetValue(IsFullscreenProperty);

    /// <summary>
    /// Windows 11 arrondit les angles des fenêtres, mais pas celles dont
    /// l'application dessine le cadre : il faut le lui demander. Sans cela, la
    /// fenêtre tranche par ses angles carrés au milieu d'un bureau qui les
    /// arrondit tous. L'attribut n'existe pas avant Windows 11, où l'appel
    /// échoue sans conséquence.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetRoundedCorners(this, true);
        if (Palette.IsDark) UseDarkFrame(this);

        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(LimitMaximizeToWorkArea);
    }

    private const int WmGetMinMaxInfo = 0x0024;

    /// <summary>
    /// Borne l'agrandissement à la zone de travail du moniteur.
    ///
    /// Sans cadre système, une fenêtre agrandie prend par défaut la DALLE
    /// entière : son bas passe alors sous la barre des tâches, qui reste au
    /// premier plan — et c'est la dernière barre de l'interface qui disparaît.
    /// Windows réclame ces dimensions par WM_GETMINMAXINFO ; on lui répond la
    /// zone de travail.
    ///
    /// MaxTrackSize n'est pas touché : il borne le redimensionnement, dont le
    /// plein écran a besoin pour couvrir la dalle entière.
    /// </summary>
    private static IntPtr LimitMaximizeToWorkArea(IntPtr handle, int message, IntPtr wParam,
                                                  IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;
        if (!MonitorInterop.TryGetBounds(handle, out var monitor, out var work)) return IntPtr.Zero;

        var info = System.Runtime.InteropServices.Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // Position relative au coin du moniteur, comme l'attend Windows : sur un
        // écran secondaire, des coordonnées absolues y placeraient la fenêtre de
        // travers.
        info.MaxPosition.X = work.Left - monitor.Left;
        info.MaxPosition.Y = work.Top - monitor.Top;
        info.MaxSize.X = work.Width;
        info.MaxSize.Y = work.Height;
        System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, true);

        // Traité : sans cela, le traitement par défaut réécrirait la structure
        // avec les dimensions de la dalle.
        handled = true;
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    /// <summary>
    /// Arrondit ou non les angles de la fenêtre. Le plein écran les redresse :
    /// une fenêtre qui couvre la dalle entière laisserait sinon voir le bureau
    /// par ses quatre coins.
    /// </summary>
    public static void SetRoundedCorners(Window window, bool rounded)
    {
        const int CornerPreference = 33;   // DWMWA_WINDOW_CORNER_PREFERENCE
        const int Round = 2;               // DWMWCP_ROUND
        const int DoNotRound = 1;          // DWMWCP_DONOTROUND

        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            int preference = rounded ? Round : DoNotRound;
            DwmSetWindowAttribute(handle, CornerPreference, ref preference, sizeof(int));
        }
        catch { /* antérieur à Windows 11 : les angles restent carrés */ }
    }

    /// <summary>
    /// Bordure et ombre de fenêtre sombres. Windows 11 trace un fin liseré
    /// autour des fenêtres ; clair par défaut, il cernerait d'un trait blanc
    /// une interface sombre. Sans effet avant Windows 10 20H1.
    /// </summary>
    private static void UseDarkFrame(Window window)
    {
        const int ImmersiveDarkMode = 20;   // DWMWA_USE_IMMERSIVE_DARK_MODE

        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            int enabled = 1;
            DwmSetWindowAttribute(handle, ImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch { /* système trop ancien : bordure claire, sans conséquence */ }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
                                                    ref int value, int size);
}
