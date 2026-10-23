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

    /// <summary>
    /// Clic du milieu : la fenêtre décide quoi coller, elle seule connaît la
    /// session à qui envoyer le texte.
    /// </summary>
    public event Action? PasteRequested;

    public TerminalEmulator Emulator => _emulator;

    /// <summary>
    /// Nombre de lignes dont la vue est remontée dans l'historique. Zéro quand
    /// on regarde le bas, c'est-à-dire l'écran courant.
    /// </summary>
    private int _viewOffset;

    /// <summary>Largeur de la glissière d'historique, dessinée par-dessus le texte.</summary>
    private const double ScrollbarWidth = 9;

    /// <summary>
    /// Prolonge le défilement tant qu'on étire une sélection hors du cadre.
    /// Sans lui, plus aucun événement de souris n'arriverait une fois le curseur
    /// immobile au-delà du bord, et la sélection s'arrêterait là.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _autoScrollTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(60),
    };

    /// <summary>+1 remonte dans l'historique, -1 redescend, 0 à l'arrêt.</summary>
    private int _autoScrollDirection;

    // MARK: - Zoom

    public const double MinFontSize = 8;
    public const double MaxFontSize = 28;
    public const double DefaultFontSize = 13;

    /// <summary>
    /// La taille de police a changé : la fenêtre l'enregistre dans les
    /// préférences, pour que le terminal suivant s'ouvre à la même échelle.
    /// </summary>
    public event Action<double>? FontSizeChanged;

    /// <summary>
    /// Change la taille de police. La grille est recalculée dans la foulée :
    /// zoomer change le nombre de colonnes et de lignes, et le shell distant
    /// doit en être informé comme pour un redimensionnement de fenêtre.
    /// </summary>
    public void SetFontSize(double size)
    {
        var clamped = Math.Clamp(Math.Round(size), MinFontSize, MaxFontSize);
        if (Math.Abs(clamped - FontSize) < 0.01) return;

        FontSize = clamped;
        MeasureCharacter();
        RecalculateGrid();
        InvalidateVisual();
        FontSizeChanged?.Invoke(clamped);
    }

    public void ZoomBy(double points) => SetFontSize(FontSize + points);

    public void ResetZoom() => SetFontSize(DefaultFontSize);

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

    private static readonly Brush CursorBrush =
        new SolidColorBrush(Color.FromArgb(0x99, 0xD4, 0xD4, 0xD4));

    private static readonly Brush ScrollbarTrack =
        new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

    private static readonly Brush ScrollbarThumb =
        new SolidColorBrush(Color.FromArgb(0x96, 0xD4, 0xD4, 0xD4));

    // Surbrillance de recherche : ambre sombre et opaque, pour que le texte
    // clair du terminal reste lisible par-dessus.
    private static readonly Brush MatchBrush =
        new SolidColorBrush(Color.FromRgb(0x62, 0x33, 0x15));

    private static readonly Brush CurrentMatchBrush =
        new SolidColorBrush(Color.FromRgb(0x9E, 0x6A, 0x03));

    static TerminalControl()
    {
        // Les brosses statiques sont figées : elles peuvent alors être partagées
        // entre threads et WPF les rend plus efficacement.
        foreach (var b in Brushes) b.Freeze();
        DefaultBackground.Freeze();
        CursorBrush.Freeze();
        ScrollbarTrack.Freeze();
        ScrollbarThumb.Freeze();
        MatchBrush.Freeze();
        CurrentMatchBrush.Freeze();
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

        _autoScrollTimer.Tick += (_, _) => AutoScrollStep();

        // Le contrôle est créé avant d'être dans l'arbre visuel : on prend le
        // focus dès qu'il y est réellement attaché.
        Loaded += (_, _) => Focus();
        Unloaded += (_, _) => _autoScrollTimer.Stop();
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
        // La sortie ramène en bas, comme dans tous les terminaux : on remonte
        // pour relire, on reprend la main dès qu'il se passe quelque chose.
        _viewOffset = 0;
        // Les occurrences désignent des lignes qui viennent de glisser dans
        // l'historique : elles ne valent plus rien. La requête, elle, reste dans
        // la barre de recherche, prête à être relancée.
        ForgetMatches();
        InvalidateVisual();
    }

    public void Clear()
    {
        // L'historique part aussi : « Effacer » laisserait sinon une fenêtre
        // vide qu'un coup de molette suffirait à remplir de nouveau.
        _emulator.Write("\u001B[2J\u001B[H");
        _emulator.ClearScrollback();
        ClearSelection();
        _viewOffset = 0;
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
        // Contrôle pas encore disposé : on annoncerait une grille de 2 × 2 au
        // shell distant, qui reformaterait sa sortie pour rien.
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        int cols = Math.Max(2, (int)(ActualWidth / _charWidth));
        int rows = Math.Max(2, (int)(ActualHeight / _charHeight));
        if (cols == _emulator.Cols && rows == _emulator.Rows) return;

        _emulator.Resize(cols, rows);
        ClampViewOffset();
        GridSizeChanged?.Invoke(cols, rows);
        InvalidateVisual();
    }

    // MARK: - Défilement de l'historique

    /// <summary>
    /// Remonte (delta positif) ou redescend dans l'historique. La vue s'arrête
    /// d'elle-même aux deux bouts.
    /// </summary>
    private void ScrollBy(int lines) => ScrollTo(_viewOffset + lines);

    private void ScrollTo(int offset)
    {
        var clamped = Math.Clamp(offset, 0, _emulator.ScrollbackCount);
        if (clamped == _viewOffset) return;

        _viewOffset = clamped;
        InvalidateVisual();
    }

    /// <summary>
    /// Ramène la vue dans les bornes après un changement de taille : l'écran
    /// venant de grandir peut déjà montrer une partie de l'historique.
    /// </summary>
    private void ClampViewOffset() =>
        _viewOffset = Math.Clamp(_viewOffset, 0, _emulator.ScrollbackCount);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomBy(e.Delta / 120);
            e.Handled = true;
            return;
        }

        // Trois lignes par cran, comme la plupart des terminaux : assez pour
        // avancer, assez peu pour ne pas perdre le fil.
        ScrollBy(e.Delta / 120 * 3);
        e.Handled = true;
    }

    /// <summary>
    /// Touches qui pilotent la vue plutôt que le shell : Maj + Page précédente
    /// et Page suivante pour parcourir l'historique, Ctrl+Maj + Début et Fin
    /// pour aller d'un bout à l'autre. Renvoie vrai si la touche a été consommée.
    ///
    /// Maj est exigé parce que Page précédente nue appartient au programme
    /// distant — un pager ou un éditeur en a besoin.
    ///
    /// Toute autre frappe ramène la vue en bas : on remonte pour lire, on tape
    /// pour reprendre la main. Les modificateurs seuls sont ignorés, sans quoi
    /// le simple fait d'enfoncer Maj annulerait le défilement qu'il commande.
    /// </summary>
    public bool HandleViewKey(Key key, ModifierKeys modifiers)
    {
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);

        if (shift && !ctrl)
        {
            switch (key)
            {
                case Key.PageUp: ScrollBy(_emulator.Rows - 1); return true;
                case Key.PageDown: ScrollBy(-(_emulator.Rows - 1)); return true;
            }
        }

        if (shift && ctrl)
        {
            switch (key)
            {
                case Key.Home: ScrollTo(_emulator.ScrollbackCount); return true;
                case Key.End: ScrollTo(0); return true;

                // Zoom sous Ctrl+Maj et non sous Ctrl seul : Ctrl+Moins est le
                // « défaire » de readline, et Ctrl+Maj est de toute façon le
                // préfixe local de l'application.
                case Key.OemPlus or Key.Add: ZoomBy(+1); return true;
                case Key.OemMinus or Key.Subtract: ZoomBy(-1); return true;
                case Key.D0 or Key.NumPad0: ResetZoom(); return true;
            }
        }

        if (key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
                     or Key.System or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
        {
            ScrollTo(0);
        }

        return false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_charWidth <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int viewRow = 0; viewRow < _emulator.Rows; viewRow++)
        {
            double y = viewRow * _charHeight;
            var line = LineForView(viewRow);

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

            // La surbrillance de recherche se pose entre le fond et le texte :
            // elle doit couvrir le premier sans masquer le second.
            DrawMatches(dc, viewRow, y);

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
        // Sans objet quand on regarde l'historique — le curseur n'y est pas.
        if (_emulator.CursorVisible && IsFocused && _viewOffset == 0)
        {
            dc.DrawRectangle(CursorBrush, null,
                new Rect(_emulator.CursorCol * _charWidth,
                         _emulator.CursorRow * _charHeight,
                         _charWidth, _charHeight));
        }

        DrawScrollbar(dc);
    }

    /// <summary>
    /// La ligne à dessiner à cette position de la vue, sélection comprise : les
    /// cellules retenues passent en vidéo inverse, ce que le tracé sait déjà
    /// rendre — pas besoin d'une passe supplémentaire.
    /// </summary>
    private TerminalCell[] LineForView(int viewRow)
    {
        var row = viewRow - _viewOffset;
        var line = _emulator.LineAt(row);

        if (line.Length == 0 || !TryGetSelectionRange(out var start, out var end)) return line;
        if (row < start.Row || row > end.Row) return line;

        var from = Math.Clamp(row == start.Row ? start.Col : 0, 0, line.Length);
        var to = Math.Clamp(row == end.Row ? end.Col : line.Length, 0, line.Length);
        if (from >= to) return line;

        // Sur une copie : la grille appartient à l'émulateur, et la sélection
        // n'est qu'un habillage de la vue.
        var highlighted = (TerminalCell[])line.Clone();
        for (int c = from; c < to; c++) highlighted[c].Inverse = !highlighted[c].Inverse;
        return highlighted;
    }

    private void DrawScrollbar(DrawingContext dc)
    {
        if (!ScrollbarVisible || ActualHeight <= 0) return;

        double total = _emulator.ScrollbackCount + _emulator.Rows;
        double x = ActualWidth - ScrollbarWidth;
        dc.DrawRectangle(ScrollbarTrack, null, new Rect(x, 0, ScrollbarWidth, ActualHeight));

        // Taille minimale : strictement proportionnel à la part visible, le
        // curseur disparaîtrait sur un historique de plusieurs milliers de lignes.
        var thumbHeight = Math.Max(MinThumbHeight, ActualHeight * _emulator.Rows / total);
        var top = Math.Clamp((_emulator.ScrollbackCount - _viewOffset) / total * ActualHeight,
                             0, ActualHeight - thumbHeight);

        dc.DrawRoundedRectangle(ScrollbarThumb, null,
            new Rect(x + 2, top, ScrollbarWidth - 4, thumbHeight), 2, 2);
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

    // MARK: - Sélection à la souris

    /// <summary>
    /// Une position dans le flux. La ligne est absolue au sens de
    /// <see cref="TerminalEmulator.LineAt"/> — négative dans l'historique — si
    /// bien que la sélection reste accrochée à son texte quand la vue défile.
    /// </summary>
    private readonly record struct CellPosition(int Row, int Col) : IComparable<CellPosition>
    {
        public int CompareTo(CellPosition other) =>
            Row != other.Row ? Row.CompareTo(other.Row) : Col.CompareTo(other.Col);
    }

    private CellPosition? _selectionAnchor;
    private CellPosition? _selectionFocus;
    private bool _selecting;

    /// <summary>Vrai lorsqu'une sélection non vide est en place.</summary>
    public bool HasSelection => TryGetSelectionRange(out _, out _);

    public void ClearSelection()
    {
        if (_selectionAnchor is null && _selectionFocus is null) return;
        _selectionAnchor = _selectionFocus = null;
        InvalidateVisual();
    }

    /// <summary>
    /// Texte à copier : la sélection si elle existe, sinon tout le terminal,
    /// historique compris. « Copier » sans rien avoir sélectionné garde ainsi
    /// son sens d'avant la sélection.
    /// </summary>
    public string TextForCopy() => GetSelectedText() ?? _emulator.GetText();

    /// <summary>Texte sélectionné, ou null s'il n'y a pas de sélection.</summary>
    public string? GetSelectedText()
    {
        if (!TryGetSelectionRange(out var start, out var end)) return null;

        var sb = new System.Text.StringBuilder();
        for (int row = start.Row; row <= end.Row; row++)
        {
            // Sélection s'arrêtant au début d'une ligne : cette ligne-là n'en
            // fait pas partie, elle n'en marque que la fin.
            if (row == end.Row && end.Col == 0 && row != start.Row) break;

            var line = _emulator.LineAt(row);
            var from = Math.Clamp(row == start.Row ? start.Col : 0, 0, line.Length);
            var to = Math.Clamp(row == end.Row ? end.Col : line.Length, 0, line.Length);

            if (row > start.Row) sb.Append('\n');
            // Les blancs de remplissage jusqu'au bord droit n'ont rien à faire
            // dans le presse-papiers.
            if (to > from) sb.Append(TerminalEmulator.LineText(line[from..to]));
        }

        return sb.ToString();
    }

    private bool TryGetSelectionRange(out CellPosition start, out CellPosition end)
    {
        start = end = default;
        if (_selectionAnchor is not { } anchor || _selectionFocus is not { } focus) return false;
        if (anchor.CompareTo(focus) == 0) return false;

        (start, end) = anchor.CompareTo(focus) < 0 ? (anchor, focus) : (focus, anchor);
        return true;
    }

    /// <summary>Ligne absolue visée par un point de la surface.</summary>
    private int RowAt(Point p) =>
        Math.Clamp((int)Math.Floor(p.Y / _charHeight), 0, _emulator.Rows - 1) - _viewOffset;

    /// <summary>Cellule sous le point, pour désigner un mot ou une ligne.</summary>
    private CellPosition PointToCell(Point p) =>
        new(RowAt(p), Math.Max(0, (int)Math.Floor(p.X / _charWidth)));

    /// <summary>
    /// Frontière de cellule la plus proche du point. C'est elle qu'il faut pour
    /// étirer une sélection : on sélectionne entre deux caractères et non sur un
    /// caractère, sinon le dernier d'un tracé vers la droite échapperait
    /// toujours à la sélection.
    /// </summary>
    private CellPosition PointToBoundary(Point p) =>
        new(RowAt(p), Math.Max(0, (int)Math.Round(p.X / _charWidth)));

    /// <summary>
    /// Les séparateurs de chemin et d'URL comptent comme des lettres : on
    /// double-clique sur un chemin pour le copier entier, pas pour en détacher
    /// un fragment.
    /// </summary>
    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or '\\' or ':' or '~';

    private void SelectWordAt(CellPosition cell)
    {
        var line = _emulator.LineAt(cell.Row);
        if (cell.Col >= line.Length || !IsWordChar(line[cell.Col].Char))
        {
            ClearSelection();
            return;
        }

        var start = cell.Col;
        while (start > 0 && IsWordChar(line[start - 1].Char)) start--;

        var end = cell.Col;
        while (end + 1 < line.Length && IsWordChar(line[end + 1].Char)) end++;

        _selectionAnchor = new CellPosition(cell.Row, start);
        _selectionFocus = new CellPosition(cell.Row, end + 1);
    }

    /// <summary>Triple-clic : la ligne entière, fin comprise.</summary>
    private void SelectLineAt(CellPosition cell)
    {
        _selectionAnchor = new CellPosition(cell.Row, 0);
        _selectionFocus = new CellPosition(cell.Row + 1, 0);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        // Clic du milieu : coller, convention des terminaux Unix.
        if (e.ChangedButton == MouseButton.Middle)
        {
            PasteRequested?.Invoke();
            e.Handled = true;
            return;
        }

        // La cellule n'a pas encore été mesurée : toute conversion en
        // coordonnées de grille diviserait par zéro.
        if (e.ChangedButton != MouseButton.Left || _charWidth <= 0 || _charHeight <= 0) return;
        var point = e.GetPosition(this);

        if (ScrollbarVisible && point.X >= ActualWidth - ScrollbarWidth)
        {
            _draggingScrollbar = true;
            ScrollToBarPosition(point.Y);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        switch (e.ClickCount)
        {
            case 2: SelectWordAt(PointToCell(point)); break;
            case 3: SelectLineAt(PointToCell(point)); break;
            default:
                // Un simple clic efface la sélection précédente : ancre et point
                // courant confondus, donc sélection vide.
                _selectionAnchor = _selectionFocus = PointToBoundary(point);
                _selecting = true;
                CaptureMouse();
                break;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_charWidth <= 0 || _charHeight <= 0) return;
        var point = e.GetPosition(this);

        // Approcher du bord droit fait apparaître la glissière.
        var near = _emulator.ScrollbackCount > 0
                   && point.X >= ActualWidth - ScrollbarWidth * 2;
        if (near != _pointerNearScrollbar)
        {
            _pointerNearScrollbar = near;
            InvalidateVisual();
        }

        if (_draggingScrollbar)
        {
            ScrollToBarPosition(point.Y);
            return;
        }

        if (!_selecting) return;

        // Tracé qui sort par le haut ou par le bas : le défilement se poursuit
        // tout seul, sinon la sélection buterait sur le bord dès que la souris
        // ne bouge plus.
        _autoScrollDirection = point.Y < 0 ? 1 : point.Y > ActualHeight ? -1 : 0;
        if (_autoScrollDirection != 0) _autoScrollTimer.Start();
        else _autoScrollTimer.Stop();

        _selectionFocus = PointToBoundary(point);
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left) return;

        _selecting = false;
        _draggingScrollbar = false;
        _autoScrollDirection = 0;
        _autoScrollTimer.Stop();
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_pointerNearScrollbar && !_draggingScrollbar)
        {
            _pointerNearScrollbar = false;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Capture perdue sans relâchement — une boîte de dialogue s'ouvre, le
    /// contenu change d'onglet. Sans ce nettoyage, le tracé reprendrait au
    /// prochain passage de la souris, bouton relâché.
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _selecting = false;
        _draggingScrollbar = false;
        _autoScrollDirection = 0;
        _autoScrollTimer.Stop();
    }

    private void AutoScrollStep()
    {
        if (!_selecting || _autoScrollDirection == 0)
        {
            _autoScrollTimer.Stop();
            return;
        }

        ScrollBy(_autoScrollDirection);
        // Le point suivi reste sous la souris : la sélection s'étend au fil du
        // défilement, sans qu'il faille bouger.
        _selectionFocus = PointToBoundary(Mouse.GetPosition(this));
        InvalidateVisual();
    }

    // MARK: - Recherche dans l'historique

    /// <summary>Une occurrence : ligne absolue, colonne de départ, longueur.</summary>
    private readonly record struct SearchMatch(int Row, int Col, int Length);

    private readonly List<SearchMatch> _matches = new();
    private readonly Dictionary<int, List<SearchMatch>> _matchesByRow = new();
    private int _currentMatch = -1;

    public int MatchCount => _matches.Count;

    /// <summary>Rang de l'occurrence mise en avant, à partir de 1 ; zéro s'il n'y en a aucune.</summary>
    public int CurrentMatchNumber => _currentMatch < 0 ? 0 : _currentMatch + 1;

    /// <summary>
    /// Cherche le texte dans tout le flux, historique compris, sans tenir compte
    /// de la casse : on cherche un nom de fichier ou un message d'erreur, pas un
    /// motif exact. La dernière occurrence est retenue d'emblée — c'est la
    /// sortie récente que l'on veut retrouver dans la quasi-totalité des cas.
    /// </summary>
    public void Search(string? query)
    {
        ForgetMatches();

        if (!string.IsNullOrEmpty(query))
        {
            for (int row = -_emulator.ScrollbackCount; row < _emulator.Rows; row++)
            {
                var line = _emulator.LineAt(row);
                if (line.Length < query.Length) continue;

                var text = new string(line.Select(c => c.Char).ToArray());
                var from = 0;
                while (from <= text.Length - query.Length)
                {
                    var at = text.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) break;

                    var match = new SearchMatch(row, at, query.Length);
                    _matches.Add(match);
                    if (!_matchesByRow.TryGetValue(row, out var list))
                        _matchesByRow[row] = list = new List<SearchMatch>();
                    list.Add(match);

                    from = at + 1;   // les occurrences qui se chevauchent comptent
                }
            }
        }

        if (_matches.Count > 0) GoToMatch(_matches.Count - 1);
        else InvalidateVisual();
    }

    /// <summary>Oublie les occurrences sans toucher à la vue.</summary>
    private void ForgetMatches()
    {
        if (_matches.Count == 0) return;
        _matches.Clear();
        _matchesByRow.Clear();
        _currentMatch = -1;
    }

    public void FindNext() => StepMatch(+1);

    public void FindPrevious() => StepMatch(-1);

    /// <summary>Passe à l'occurrence suivante ou précédente, en bouclant.</summary>
    private void StepMatch(int delta)
    {
        if (_matches.Count == 0) return;

        var index = _currentMatch < 0
            ? (delta > 0 ? 0 : _matches.Count - 1)
            : (_currentMatch + delta + _matches.Count) % _matches.Count;
        GoToMatch(index);
    }

    private void GoToMatch(int index)
    {
        _currentMatch = index;
        // Centrée plutôt que collée au bord : ce qui entoure une occurrence est
        // souvent ce qui permet de la reconnaître.
        ScrollTo(_emulator.Rows / 2 - _matches[index].Row);
        InvalidateVisual();
    }

    private void DrawMatches(DrawingContext dc, int viewRow, double y)
    {
        if (_matches.Count == 0) return;
        if (!_matchesByRow.TryGetValue(viewRow - _viewOffset, out var matches)) return;

        var current = _currentMatch >= 0 ? _matches[_currentMatch] : default;
        foreach (var match in matches)
        {
            dc.DrawRectangle(match == current ? CurrentMatchBrush : MatchBrush, null,
                new Rect(match.Col * _charWidth, y, match.Length * _charWidth, _charHeight));
        }
    }

    // MARK: - Glissière d'historique

    private bool _draggingScrollbar;
    private bool _pointerNearScrollbar;

    private const double MinThumbHeight = 24;

    /// <summary>
    /// La glissière ne paraît que lorsqu'elle sert : on a remonté, on la
    /// manipule, ou la souris s'en approche. Posée par-dessus le texte, elle
    /// masquerait sinon la dernière colonne en permanence — et lui réserver une
    /// colonne ferait changer la taille du terminal distant au premier
    /// défilement.
    /// </summary>
    private bool ScrollbarVisible =>
        _emulator.ScrollbackCount > 0
        && (_viewOffset > 0 || _draggingScrollbar || _pointerNearScrollbar);

    private void ScrollToBarPosition(double y)
    {
        if (ActualHeight <= 0) return;

        double total = _emulator.ScrollbackCount + _emulator.Rows;
        var thumbHeight = Math.Max(MinThumbHeight, ActualHeight * _emulator.Rows / total);

        // Le curseur se centre sous la souris plutôt que d'être saisi par son
        // bord supérieur : le saut au premier clic est alors sans surprise.
        var ratio = (y - thumbHeight / 2) / Math.Max(1, ActualHeight - thumbHeight);
        var topLine = (int)Math.Round(Math.Clamp(ratio, 0, 1) * _emulator.ScrollbackCount);
        ScrollTo(_emulator.ScrollbackCount - topLine);
    }
}
