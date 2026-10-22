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

        const int CornerPreference = 33;   // DWMWA_WINDOW_CORNER_PREFERENCE
        const int Round = 2;               // DWMWCP_ROUND

        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int preference = Round;
            DwmSetWindowAttribute(handle, CornerPreference, ref preference, sizeof(int));
        }
        catch { /* antérieur à Windows 11 : les angles restent carrés */ }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
                                                    ref int value, int size);
}
