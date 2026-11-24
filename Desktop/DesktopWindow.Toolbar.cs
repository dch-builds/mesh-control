using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeshControl.Desktop;

/// <summary>
/// Barre d'outils qui se resserre quand la fenêtre rétrécit.
///
/// Logée dans la barre de titre, elle n'a que la largeur laissée par les
/// boutons de fenêtre : au-delà, ses derniers boutons passaient dessous, à
/// moitié cachés. Plutôt que de les perdre, elle se replie par paliers.
/// </summary>
public partial class DesktopWindow
{
    /// <summary>
    /// Paliers, du plus large au plus serré. Chacun inclut les précédents.
    /// </summary>
    private enum ToolbarCompactness
    {
        /// <summary>Tout est affiché, avec les libellés.</summary>
        Full,
        /// <summary>Rafraîchir, Coller, Taper et Copier depuis réduits à leur icône.</summary>
        Icons,
        /// <summary>Le texte d'état de la barre disparaît.</summary>
        NoStats,
        /// <summary>
        /// Copier depuis, capture, loupe et déblocage du clavier passent dans
        /// le menu « ⋯ » ; leurs raccourcis restent actifs.
        /// </summary>
        Overflow,
    }

    /// <summary>
    /// Largeur laissée au nom de la machine, à gauche des contrôles, avant de
    /// commencer à les resserrer.
    /// </summary>
    private const double ToolbarTitleReserve = 140;

    private ToolbarCompactness _toolbarCompactness = ToolbarCompactness.Full;
    private bool _toolbarFitting;

    private object? _refreshLabel;
    private object? _pasteLabel;
    private object? _copyFromRemoteLabel;

    private void InitializeToolbarLayout()
    {
        _refreshLabel = RefreshButton.Content;
        _pasteLabel = PasteButton.Content;
        _copyFromRemoteLabel = CopyFromRemoteButton.Content;

        // Largeur offerte (fenêtre redimensionnée) ou largeur demandée (bouton
        // qui apparaît, message plus long) : dans les deux cas, on réévalue.
        Toolbar.SizeChanged += (_, _) => FitToolbar();
        ToolbarControls.SizeChanged += (_, _) => FitToolbar();

        // À l'ouverture, la première mesure a lieu avant que la barre soit
        // affichée : sa largeur n'est alors pas encore celle de la barre de
        // titre. On recale donc une fois l'affichage fait, et chaque fois
        // qu'elle réapparaît (sortie du plein écran, changement d'onglet).
        Toolbar.Loaded += (_, _) => ScheduleFitToolbar();
        Toolbar.IsVisibleChanged += (_, _) => ScheduleFitToolbar();
        ContentRendered += (_, _) => ScheduleFitToolbar();
    }

    private void ScheduleFitToolbar() =>
        Dispatcher.BeginInvoke(FitToolbar, System.Windows.Threading.DispatcherPriority.Loaded);

    /// <summary>
    /// Choisit le palier le plus large qui tient dans la place disponible.
    /// Le calcul part toujours du palier le plus large : le résultat ne
    /// dépend que de la place et du contenu, et ne peut donc pas osciller.
    /// </summary>
    private void FitToolbar()
    {
        if (_toolbarFitting || !Toolbar.IsVisible || Toolbar.ActualWidth <= 0) return;
        _toolbarFitting = true;
        try
        {
            var available = Toolbar.ActualWidth - Toolbar.Padding.Left - Toolbar.Padding.Right
                            - ToolbarTitleReserve;

            var chosen = ToolbarCompactness.Overflow;
            foreach (var level in Enum.GetValues<ToolbarCompactness>())
            {
                ApplyToolbarCompactness(level);
                ToolbarControls.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (ToolbarControls.DesiredSize.Width <= available)
                {
                    chosen = level;
                    break;
                }
            }

            ApplyToolbarCompactness(chosen);
            _toolbarCompactness = chosen;
        }
        finally
        {
            _toolbarFitting = false;
        }
    }

    private void ApplyToolbarCompactness(ToolbarCompactness level)
    {
        var icons = level >= ToolbarCompactness.Icons;
        SetIconOnly(RefreshButton, icons, _refreshLabel, 0xE72C);
        SetIconOnly(PasteButton, icons, _pasteLabel, 0xE77F);
        SetIconOnly(CopyFromRemoteButton, icons, _copyFromRemoteLabel, 0xE8C8);
        PasteTypingLabel.Visibility = icons ? Visibility.Collapsed : Visibility.Visible;
        PasteTypingGlyph.Margin = icons ? new Thickness(0, 1, 0, 0) : new Thickness(0, 1, 6, 0);
        if (icons) PasteTypingButton.Padding = new Thickness(9, 5, 9, 5);
        else PasteTypingButton.ClearValue(PaddingProperty);

        StatsText.Visibility = level >= ToolbarCompactness.NoStats
            ? Visibility.Collapsed
            : Visibility.Visible;

        var overflow = level >= ToolbarCompactness.Overflow;
        var inBar = overflow ? Visibility.Collapsed : Visibility.Visible;
        var inMenu = overflow ? Visibility.Visible : Visibility.Collapsed;
        CopyFromRemoteButton.Visibility = inBar;
        ScreenshotButton.Visibility = inBar;
        ZoomButton.Visibility = inBar;
        ReleaseModifiersButton.Visibility = inBar;
        OverflowCopyItem.Visibility = inMenu;
        OverflowScreenshotItem.Visibility = inMenu;
        OverflowZoomItem.Visibility = inMenu;
        OverflowReleaseItem.Visibility = inMenu;
        OverflowSeparator.Visibility = inMenu;
    }

    /// <summary>
    /// Bouton à libellé réduit à une icône, ou rendu à son libellé.
    /// L'info-bulle, inchangée, continue de dire ce qu'il fait.
    /// </summary>
    private void SetIconOnly(Button button, bool iconOnly, object? label, int glyph)
    {
        if (iconOnly)
        {
            if (button.Content is TextBlock { Tag: "glyph" }) return;
            button.Content = new TextBlock
            {
                Text = ((char)glyph).ToString(),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Tag = "glyph",
            };
            button.Padding = new Thickness(9, 5, 9, 5);
        }
        else
        {
            if (button.Content is not TextBlock { Tag: "glyph" }) return;
            button.Content = label;
            button.ClearValue(PaddingProperty);
        }
    }
}
