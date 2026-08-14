using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MeshControl.Ssh;

/// <summary>
/// Rendu d'un <see cref="TerminalEmulator"/>. Dessine la grille de cellules
/// directement dans OnRender : créer un élément visuel par caractère serait
/// bien trop coûteux (80×24 = 1920 cellules redessinées en continu).
/// </summary>
public sealed class TerminalControl : Control
{
    private readonly TerminalEmulator _emulator;
    private Typeface _typeface = new("Consolas");
    private double _charWidth;
    private double _charHeight;

    /// <summary>Déclenché quand la taille en caractères change (pour informer le serveur).</summary>
    public event Action<int, int>? GridSizeChanged;

    public TerminalEmulator Emulator => _emulator;

    /// <summary>Palette ANSI 16 couleurs, sur fond sombre.</summary>
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x1E, 0x1E, 0x1E), // 0 noir
        Color.FromRgb(0xCD, 0x31, 0x31), // 1 rouge
        Color.FromRgb(0x0D, 0xBC, 0x79), // 2 vert
        Color.FromRgb(0xE5, 0xE5, 0x10), // 3 jaune
        Color.FromRgb(0x24, 0x72, 0xC8), // 4 bleu
        Color.FromRgb(0xBC, 0x3F, 0xBC), // 5 magenta
        Color.FromRgb(0x11, 0xA8, 0xCD), // 6 cyan
        Color.FromRgb(0xD4, 0xD4, 0xD4), // 7 gris clair
        Color.FromRgb(0x66, 0x66, 0x66), // 8 gris foncé
        Color.FromRgb(0xF1, 0x4C, 0x4C), // 9 rouge vif
        Color.FromRgb(0x23, 0xD1, 0x8B), // 10 vert vif
        Color.FromRgb(0xF5, 0xF5, 0x43), // 11 jaune vif
        Color.FromRgb(0x3B, 0x8E, 0xEA), // 12 bleu vif
        Color.FromRgb(0xD6, 0x70, 0xD6), // 13 magenta vif
        Color.FromRgb(0x29, 0xB8, 0xDB), // 14 cyan vif
        Color.FromRgb(0xFF, 0xFF, 0xFF), // 15 blanc
    ];

    private static readonly Brush[] Brushes =
        Palette.Select(c => (Brush)new SolidColorBrush(c)).ToArray();

    private static readonly Brush DefaultBackground =
        new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));

    static TerminalControl()
    {
        // Les brosses statiques sont figées : elles peuvent alors être partagées
        // entre threads et WPF les rend plus efficacement.
        foreach (var b in Brushes) b.Freeze();
        DefaultBackground.Freeze();
    }

    public TerminalControl()
    {
        _emulator = new TerminalEmulator();
        Background = DefaultBackground;

        // Focus clavier : un Control sans template n'est pas naturellement
        // focusable au clic. Il faut l'autoriser explicitement ET garantir que la
        // surface soit testée au survol (voir HitTestCore et le fond dessiné dans
        // OnRender), sinon Focus() échoue en silence et rien n'est saisissable.
        Focusable = true;
        IsTabStop = true;
        FocusVisualStyle = null;   // pas de cadre pointillé sur un terminal

        FontFamily = new FontFamily("Consolas");
        FontSize = 13;
        ClipToBounds = true;
        MeasureCharacter();

        // Le contrôle est créé avant d'être dans l'arbre visuel : on prend le
        // focus dès qu'il y est réellement attaché.
        Loaded += (_, _) => Focus();
    }

    /// <summary>
    /// Rend toute la surface cliquable. Sans cela, WPF ne considère « touchés »
    /// que les pixels effectivement dessinés, et un clic dans une zone vide du
    /// terminal ne donnerait pas le focus.
    /// </summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        return bounds.Contains(parameters.HitPoint)
            ? new PointHitTestResult(this, parameters.HitPoint)
            : null;
    }

    /// <summary>Mesure la cellule à partir de la police monospace choisie.</summary>
    private void MeasureCharacter()
    {
        _typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal,
                                 FontStretches.Normal);
        var sample = new FormattedText("M", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _typeface, FontSize, System.Windows.Media.Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        _charWidth = sample.WidthIncludingTrailingWhitespace;
        _charHeight = Math.Ceiling(sample.Height);
    }

    /// <summary>Écrit la sortie du shell dans l'émulateur et redessine.</summary>
    public void Write(string text)
    {
        _emulator.Write(text);
        InvalidateVisual();
    }

    public void Clear()
    {
        _emulator.Write("\u001B[2J\u001B[H");
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        RecalculateGrid();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        MeasureCharacter();
        RecalculateGrid();
    }

    private void RecalculateGrid()
    {
        if (_charWidth <= 0 || _charHeight <= 0) return;
        int cols = Math.Max(2, (int)(ActualWidth / _charWidth));
        int rows = Math.Max(2, (int)(ActualHeight / _charHeight));
        if (cols == _emulator.Cols && rows == _emulator.Rows) return;

        _emulator.Resize(cols, rows);
        GridSizeChanged?.Invoke(cols, rows);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_charWidth <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var grid = _emulator.Grid;

        for (int row = 0; row < grid.Length; row++)
        {
            double y = row * _charHeight;
            var line = grid[row];

            // Les fonds sont dessinés par plages contiguës de même couleur :
            // un rectangle par cellule serait inutilement coûteux.
            int runStart = 0;
            for (int col = 1; col <= line.Length; col++)
            {
                bool endOfRun = col == line.Length
                    || !SameBackground(line[col], line[runStart]);
                if (!endOfRun) continue;

                var cell = line[runStart];
                var background = ResolveBackground(cell);
                if (background is not null)
                {
                    dc.DrawRectangle(background, null,
                        new Rect(runStart * _charWidth, y,
                                 (col - runStart) * _charWidth, _charHeight));
                }
                runStart = col;
            }

            // Puis le texte, par plages de même style, pour limiter le nombre
            // d'objets FormattedText créés.
            int textStart = 0;
            while (textStart < line.Length)
            {
                int end = textStart;
                while (end < line.Length && SameForeground(line[end], line[textStart])) end++;

                var text = new string(line[textStart..end].Select(c => c.Char).ToArray());
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var cell = line[textStart];
                    var typeface = cell.Bold
                        ? new Typeface(FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal)
                        : _typeface;

                    var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, FontSize,
                        ResolveForeground(cell), dpi);
                    if (cell.Underline)
                        formatted.SetTextDecorations(TextDecorations.Underline);

                    dc.DrawText(formatted, new Point(textStart * _charWidth, y));
                }
                textStart = end;
            }
        }

        // Curseur : bloc semi-transparent, visible sans masquer le caractère.
        if (_emulator.CursorVisible && IsFocused)
        {
            var cursor = new SolidColorBrush(Color.FromArgb(0x99, 0xD4, 0xD4, 0xD4));
            dc.DrawRectangle(cursor, null,
                new Rect(_emulator.CursorCol * _charWidth,
                         _emulator.CursorRow * _charHeight,
                         _charWidth, _charHeight));
        }
    }

    private static bool SameBackground(TerminalCell a, TerminalCell b)
        => a.Background == b.Background && a.Inverse == b.Inverse
           && a.Foreground == b.Foreground;

    private static bool SameForeground(TerminalCell a, TerminalCell b)
        => a.Foreground == b.Foreground && a.Bold == b.Bold
           && a.Underline == b.Underline && a.Inverse == b.Inverse
           && a.Background == b.Background;

    private static Brush? ResolveBackground(TerminalCell cell)
    {
        if (cell.Inverse) return Brushes[Math.Min(cell.Foreground, (byte)15)];
        return cell.Background == 0 ? null : Brushes[Math.Min(cell.Background - 1, 15)];
    }

    private static Brush ResolveForeground(TerminalCell cell)
    {
        if (cell.Inverse)
            return cell.Background == 0
                ? DefaultBackground
                : Brushes[Math.Min(cell.Background - 1, 15)];

        var index = cell.Foreground;
        // Le gras éclaircit les 8 premières couleurs, convention des terminaux.
        if (cell.Bold && index < 8) index += 8;
        return Brushes[Math.Min(index, (byte)15)];
    }

    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }
}
