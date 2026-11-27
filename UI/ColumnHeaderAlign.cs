using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MeshControl.UI;

/// <summary>
/// Aligne un en-tête de colonnes, posé au-dessus d'une liste, sur les lignes de
/// cette liste : la marge droite de l'en-tête suit la largeur réelle de la
/// barre de défilement verticale, et s'annule quand elle disparaît. Une largeur
/// supposée décalait les colonnes de plusieurs pixels.
/// </summary>
public static class ColumnHeaderAlign
{
    /// <summary>
    /// Branche l'alignement : <paramref name="apply"/> reçoit la largeur à
    /// réserver à droite (0 sans barre de défilement), à chaque changement.
    /// </summary>
    public static void Attach(ItemsControl list, Action<double> apply)
    {
        ScrollViewer? viewer = null;
        ScrollBar? bar = null;

        void Update()
        {
            var width = viewer is { ComputedVerticalScrollBarVisibility: Visibility.Visible } && bar is not null
                ? bar.ActualWidth + bar.Margin.Left + bar.Margin.Right
                : 0;
            apply(width);
        }

        void Hook()
        {
            if (viewer is not null) return;
            viewer = FindDescendant<ScrollViewer>(list);
            if (viewer is null) return;
            viewer.ApplyTemplate();
            bar = FindDescendant<ScrollBar>(viewer, b => b.Orientation == Orientation.Vertical);
            viewer.ScrollChanged += (_, _) => Update();
            if (bar is not null) bar.SizeChanged += (_, _) => Update();
            Update();
        }

        list.Loaded += (_, _) => Hook();
        list.LayoutUpdated += (_, _) => Hook();
    }

    private static T? FindDescendant<T>(DependencyObject parent, Func<T, bool>? match = null) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found && (match is null || match(found))) return found;
            if (FindDescendant(child, match) is { } deeper) return deeper;
        }
        return null;
    }
}
