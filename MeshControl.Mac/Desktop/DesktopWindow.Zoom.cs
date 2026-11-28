using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace MeshControl.Mac;

/// <summary>Loupe : tracer un rectangle sur l'écran pour l'agrandir (portage de Desktop/DesktopWindow.xaml.cs).</summary>
public partial class DesktopWindow
{
    /// <summary>
    /// Zone agrandie, en fraction de l'image (0 à 1) : elle survit ainsi au
    /// redimensionnement de la fenêtre comme à un changement de résolution.
    /// Null quand l'écran est affiché en entier.
    /// </summary>
    private Rect? _zoomRect;

    /// <summary>Vrai pendant le tracé du rectangle : la souris reste locale.</summary>
    private bool _zoomSelecting;

    private Point _zoomStart;
    private bool _zoomDragging;

    /// <summary>Agrandissement maximal : au-delà, on ne voit plus que des pixels.</summary>
    private const double MaxZoom = 12;

    /// <summary>Côté minimal du rectangle tracé, en pixels de la fenêtre.</summary>
    private const double MinZoomBand = 12;

    private void InitializeZoom()
    {
        ZoomOverlay.PointerPressed += ZoomOverlay_PointerPressed;
        ZoomOverlay.PointerMoved += ZoomOverlay_PointerMoved;
        ZoomOverlay.PointerReleased += ZoomOverlay_PointerReleased;
        // La vue suit la fenêtre : la zone retenue garde sa place à l'écran.
        FitHost.SizeChanged += (_, _) => ApplyZoom();
        ScreenImage.SizeChanged += (_, _) => ApplyZoom();
        UpdateZoomButton();
    }

    private void Zoom_Click(object? sender, RoutedEventArgs e)
    {
        // Maj+clic pendant un zoom : nouveau tracé dans la zone déjà agrandie.
        ToggleZoom(refine: _heldModifiers.Contains(Key.LeftShift) || _heldModifiers.Contains(Key.RightShift));
        ScreenHost.Focus();
    }

    private void ZoomBadge_Click(object? sender, RoutedEventArgs e)
    {
        ResetZoom(quiet: false);
        ScreenHost.Focus();
    }

    /// <summary>
    /// Un même geste pour tout : annule un tracé en cours, sinon revient à la
    /// vue entière, sinon commence un tracé.
    /// </summary>
    private void ToggleZoom(bool refine = false)
    {
        if (_zoomSelecting)
        {
            CancelZoomSelection();
            ShowStatus("Zoom annulé.");
        }
        else if (_zoomRect is not null && !refine)
        {
            ResetZoom(quiet: false);
        }
        else
        {
            BeginZoomSelection();
        }
    }

    private void BeginZoomSelection()
    {
        if (_framebuffer.Bitmap is null)
        {
            ShowStatus("Aucune image à agrandir.");
            return;
        }

        // La loupe s'appuie sur la vue ajustée.
        if (FitCheck.IsChecked != true) FitCheck.IsChecked = true;

        _zoomSelecting = true;
        ZoomBand.IsVisible = false;
        ZoomOverlay.IsVisible = true;
        ZoomHint.IsVisible = true;
        UpdateZoomButton();
        StatsText.Text = "Tracez la zone à agrandir.";
    }

    private void CancelZoomSelection()
    {
        if (!_zoomSelecting) return;
        _zoomSelecting = false;
        _zoomDragging = false;
        ZoomOverlay.IsVisible = false;
        ZoomHint.IsVisible = false;
        ZoomBand.IsVisible = false;
        UpdateZoomButton();
    }

    private void ResetZoom(bool quiet)
    {
        if (_zoomRect is null) return;
        _zoomRect = null;
        ApplyZoom();
        if (!quiet) ShowStatus("Vue entière.");
    }

    private void ZoomOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        var point = e.GetCurrentPoint(ZoomOverlay);
        if (point.Properties.IsRightButtonPressed)
        {
            CancelZoomSelection();
            ShowStatus("Zoom annulé.");
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        _zoomStart = point.Position;
        _zoomDragging = true;
        e.Pointer.Capture(ZoomOverlay);
        UpdateZoomBand(_zoomStart);
        ZoomBand.IsVisible = true;
    }

    private void ZoomOverlay_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_zoomDragging) UpdateZoomBand(e.GetPosition(ZoomOverlay));
    }

    private void ZoomOverlay_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_zoomDragging) return;
        e.Handled = true;
        _zoomDragging = false;
        e.Pointer.Capture(null);

        var end = e.GetPosition(ZoomOverlay);
        if (Math.Abs(end.X - _zoomStart.X) < MinZoomBand || Math.Abs(end.Y - _zoomStart.Y) < MinZoomBand)
        {
            // Simple clic ou rectangle minuscule : on reste en mode tracé.
            ZoomBand.IsVisible = false;
            StatsText.Text = "Zone trop petite : faites glisser pour tracer un rectangle.";
            return;
        }

        // Coins ramenés dans le repère de l'image, transformation actuelle
        // comprise : tracer pendant un zoom affine la zone déjà agrandie.
        var a = ZoomOverlay.TranslatePoint(_zoomStart, ScreenImage);
        var b = ZoomOverlay.TranslatePoint(end, ScreenImage);
        double w = ScreenImage.Bounds.Width, h = ScreenImage.Bounds.Height;
        CancelZoomSelection();
        if (a is not { } pa || b is not { } pb || w <= 0 || h <= 0) return;

        double x1 = Math.Clamp(Math.Min(pa.X, pb.X), 0, w), x2 = Math.Clamp(Math.Max(pa.X, pb.X), 0, w);
        double y1 = Math.Clamp(Math.Min(pa.Y, pb.Y), 0, h), y2 = Math.Clamp(Math.Max(pa.Y, pb.Y), 0, h);
        if (x2 - x1 < 1 || y2 - y1 < 1)
        {
            ShowStatus("La zone tracée est hors de l'écran distant.");
            return;
        }

        _zoomRect = new Rect(x1 / w, y1 / h, (x2 - x1) / w, (y2 - y1) / h);
        ApplyZoom();
    }

    private void UpdateZoomBand(Point current)
    {
        Canvas.SetLeft(ZoomBand, Math.Min(_zoomStart.X, current.X));
        Canvas.SetTop(ZoomBand, Math.Min(_zoomStart.Y, current.Y));
        ZoomBand.Width = Math.Abs(current.X - _zoomStart.X);
        ZoomBand.Height = Math.Abs(current.Y - _zoomStart.Y);
    }

    /// <summary>
    /// Agrandit l'image pour que la zone retenue remplisse la vue, en gardant
    /// ses proportions. Tout passe par une transformation de rendu : les
    /// coordonnées de souris, relevées dans le repère de l'image, restent donc
    /// justes sans calcul supplémentaire.
    /// </summary>
    private void ApplyZoom()
    {
        if (!_ready) return;

        double iw = ScreenImage.Bounds.Width, ih = ScreenImage.Bounds.Height;
        double hw = FitHost.Bounds.Width, hh = FitHost.Bounds.Height;

        if (_zoomRect is not { } r || iw <= 0 || ih <= 0 || hw <= 0 || hh <= 0)
        {
            ScreenImage.RenderTransform = null;
            ZoomBadge.IsVisible = false;
            UpdateZoomButton();
            return;
        }

        // Position de l'image dans son hôte, hors transformation : centrée.
        double ox = (hw - iw) / 2, oy = (hh - ih) / 2;

        double scale = Math.Min(hw / (r.Width * iw), hh / (r.Height * ih));
        scale = Math.Clamp(scale, 1, MaxZoom);

        double cx = (r.X + r.Width / 2) * iw, cy = (r.Y + r.Height / 2) * ih;
        double tx = ClampOffset(hw / 2 - ox - scale * cx, ox, scale * iw, hw);
        double ty = ClampOffset(hh / 2 - oy - scale * cy, oy, scale * ih, hh);

        ScreenImage.RenderTransform = new MatrixTransform(new Matrix(scale, 0, 0, scale, tx, ty));

        ZoomBadge.Content = $"Zoom ×{scale:0.#}";
        ZoomBadge.IsVisible = true;
        UpdateZoomButton();
    }

    /// <summary>
    /// Décalage le long d'un axe : l'image agrandie ne laisse pas de bande vide
    /// quand elle déborde de la vue, et reste centrée quand elle y tient.
    /// </summary>
    private static double ClampOffset(double offset, double origin, double size, double host)
    {
        if (size <= host) return (host - size) / 2 - origin;
        return Math.Clamp(offset, host - size - origin, -origin);
    }

    private void UpdateZoomButton()
    {
        // Loupe « moins » quand un clic ramène à la vue entière.
        var zoomed = _zoomRect is not null && !_zoomSelecting;
        ZoomButtonGlyph.Kind = zoomed ? IconKind.ZoomOut : IconKind.ZoomIn;
        ToolTip.SetTip(ZoomButton, zoomed
            ? "Revenir à la vue entière (Ctrl+Maj+Z) — Maj+clic pour agrandir encore"
            : "Agrandir une zone : tracer un rectangle sur l'écran (Ctrl+Maj+Z)");
    }
}
