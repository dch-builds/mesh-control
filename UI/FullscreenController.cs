using System.Windows;

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
    private sealed record SavedState(WindowStyle Style, WindowState State, ResizeMode Resize);

    private static readonly Dictionary<Window, SavedState> Saved = new();

    public static bool IsFullscreen(Window window) => Saved.ContainsKey(window);

    /// <summary>Bascule l'état ; renvoie vrai si la fenêtre est désormais en plein écran.</summary>
    public static bool Toggle(Window window)
    {
        if (Saved.Remove(window, out var saved))
        {
            // Rétablir le style avant l'état : l'inverse laisserait Windows
            // recalculer la taille maximisée sans la bordure, et la fenêtre
            // restaurée dépasserait de l'écran.
            window.WindowStyle = saved.Style;
            window.ResizeMode = saved.Resize;
            window.WindowState = saved.State;
            return false;
        }

        Saved[window] = new SavedState(window.WindowStyle, window.WindowState, window.ResizeMode);
        // Une fenêtre fermée en plein écran laisserait une entrée orpheline.
        window.Closed += (_, _) => Saved.Remove(window);

        // Repasser par Normal : une fenêtre déjà maximisée ne recalcule pas ses
        // dimensions quand son style change, et la barre des tâches resterait
        // affichée par-dessus l'image.
        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowState = WindowState.Maximized;
        return true;
    }

    /// <summary>Sort du plein écran ; sans effet si la fenêtre n'y est pas.</summary>
    public static void Exit(Window window)
    {
        if (IsFullscreen(window)) Toggle(window);
    }
}
