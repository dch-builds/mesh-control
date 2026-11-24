using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace MeshControl.UI;

/// <summary>
/// Bascule une fenêtre en plein écran, et rétablit son état d'origine.
///
/// L'état est mémorisé par fenêtre plutôt que par session : en mode onglets,
/// plusieurs sessions partagent la même fenêtre hôte, et c'est elle qui porte
/// réellement le plein écran.
/// </summary>
public static class FullscreenController
{
    private sealed record SavedState(
        WindowStyle Style, WindowState State, ResizeMode Resize, Rect Bounds,
        object Chrome);

    private static readonly Dictionary<Window, SavedState> Saved = new();

    public static bool IsFullscreen(Window window) => Saved.ContainsKey(window);

    /// <summary>Bascule l'état ; renvoie vrai si la fenêtre est désormais en plein écran.</summary>
    public static bool Toggle(Window window)
    {
        if (Saved.Remove(window, out var saved))
        {
            // Rétablir le style avant la géométrie : l'inverse laisserait
            // Windows recalculer les dimensions sans la bordure, et la fenêtre
            // restaurée dépasserait de l'écran.
            window.WindowStyle = saved.Style;
            window.ResizeMode = saved.Resize;
            RestoreChrome(window, saved.Chrome);
            ChromeWindow.SetIsFullscreen(window, false);
            ChromeWindow.SetRoundedCorners(window, true);

            // Toujours rendre la géométrie, même pour une fenêtre qui repart
            // agrandie : elle couvre en ce moment la dalle entière, et Windows
            // ne recalcule pas l'agrandissement d'une fenêtre qui occupe déjà
            // tout l'écran. Elle resterait posée par-dessus la barre des tâches,
            // sa propre barre d'état passée dessous.
            window.Left = saved.Bounds.Left;
            window.Top = saved.Bounds.Top;
            window.Width = saved.Bounds.Width;
            window.Height = saved.Bounds.Height;
            window.WindowState = saved.State;
            return false;
        }

        Saved[window] = new SavedState(
            window.WindowStyle, window.WindowState, window.ResizeMode,
            RestoreGeometry(window),
            window.ReadLocalValue(WindowChrome.WindowChromeProperty));
        // Une fenêtre fermée en plein écran laisserait une entrée orpheline.
        window.Closed += (_, _) => Saved.Remove(window);

        // Repasser par Normal : une fenêtre agrandie s'arrête à la ZONE DE
        // TRAVAIL, c'est-à-dire l'écran moins la barre des tâches, qui restait
        // alors posée par-dessus l'image. Posée exactement sur les bornes de la
        // dalle, la fenêtre est en revanche reconnue comme plein écran par le
        // shell, qui efface sa barre.
        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        // Le cadre dessiné par l'application déclare une zone de titre (44 px)
        // et une bordure de redimensionnement : Windows les traite comme hors
        // contenu, si bien que le haut de l'écran déplaçait la fenêtre au lieu
        // d'atteindre l'image distante — et que la barre flottante, qui
        // s'ouvre au bord haut, ne pouvait jamais apparaître.
        if (WindowChrome.GetWindowChrome(window) is not null)
        {
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
        }
        ChromeWindow.SetIsFullscreen(window, true);
        ChromeWindow.SetRoundedCorners(window, false);

        var bounds = MonitorBounds(window);
        window.Left = bounds.Left;
        window.Top = bounds.Top;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        return true;
    }

    /// <summary>
    /// Rend à la fenêtre le cadre qu'elle avait : sa valeur locale s'il y en
    /// avait une, sinon celle de son style.
    /// </summary>
    private static void RestoreChrome(Window window, object chrome)
    {
        if (chrome == DependencyProperty.UnsetValue)
            window.ClearValue(WindowChrome.WindowChromeProperty);
        else
            window.SetValue(WindowChrome.WindowChromeProperty, chrome);
    }

    /// <summary>Sort du plein écran ; sans effet si la fenêtre n'y est pas.</summary>
    public static void Exit(Window window)
    {
        if (IsFullscreen(window)) Toggle(window);
    }

    /// <summary>
    /// Géométrie à retrouver en sortant du plein écran. Sur une fenêtre
    /// agrandie, Left/Top/Width/Height décrivent la fenêtre telle qu'elle
    /// occupe l'écran ; c'est RestoreBounds qui porte sa taille d'origine.
    /// </summary>
    private static Rect RestoreGeometry(Window window)
    {
        var current = new Rect(window.Left, window.Top, window.Width, window.Height);
        if (window.WindowState == WindowState.Normal) return current;

        // RestoreBounds est vide tant que la fenêtre n'a pas été affichée.
        var restored = window.RestoreBounds;
        return restored.IsEmpty ? current : restored;
    }

    /// <summary>
    /// Bornes complètes du moniteur qui porte la fenêtre — barre des tâches
    /// comprise —, exprimées dans les unités de WPF.
    /// </summary>
    private static Rect MonitorBounds(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;

        // Repli sur la zone de travail : moins bien, mais la fenêtre reste
        // utilisable, ce qui vaut mieux qu'une géométrie aberrante.
        if (!MonitorInterop.TryGetBounds(handle, out var monitor, out _))
            return SystemParameters.WorkArea;

        // Les bornes arrivent en pixels physiques ; WPF raisonne en unités
        // indépendantes de la résolution.
        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?
            .TransformFromDevice ?? Matrix.Identity;

        return new Rect(
            transform.Transform(new Point(monitor.Left, monitor.Top)),
            transform.Transform(new Point(monitor.Right, monitor.Bottom)));
    }
}
