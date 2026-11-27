using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;
using Avalonia.Threading;
using MeshControl.Ssh;

namespace MeshControl.Mac;

/// <summary>
/// Rendu d'un <see cref="TerminalEmulator"/> — portage de SSH/TerminalControl.cs
/// (version Windows). La grille est dessinée directement dans Render : un
/// élément visuel par caractère serait bien trop coûteux.
///
/// Mêmes comportements : sélection (glisser, double-clic mot, triple-clic
/// ligne), historique à la molette avec glissière discrète, recherche,
/// zoom, taille affichée au redimensionnement, menu contextuel.
/// </summary>
public sealed class TerminalControl : Control, ICustomHitTest
{
    private readonly TerminalEmulator _emulator = new();
    private Typeface _typeface;
    private Typeface _boldTypeface;
    private double _charWidth;
    private double _charHeight;

    /// <summary>Police à chasse fixe : celle de Windows, sinon celle du Mac.</summary>
    public static readonly FontFamily TerminalFont = new("Cascadia Mono, Consolas, Menlo, SF Mono, monospace");

    public event Action<int, int>? GridSizeChanged;
    public event Action? PasteRequested;
    public event Action? SearchRequested;
    public event Action? CommandsRequested;
    public event Action? ReconnectRequested;
    public event Action<double>? FontSizeChanged;

    public TerminalEmulator Emulator => _emulator;

    /// <summary>Lignes dont la vue est remontée dans l'historique (0 = écran courant).</summary>
    private int _viewOffset;

    private const double ScrollbarWidth = 9;

    // MARK: - Zoom

    public const double MinFontSize = 8;
    public const double MaxFontSize = 28;
    public const double DefaultFontSize = 13;

    private double _fontSize = DefaultFontSize;
    public double TerminalFontSize => _fontSize;

    public void SetFontSize(double size)
    {
        var clamped = Math.Clamp(Math.Round(size), MinFontSize, MaxFontSize);
        if (Math.Abs(clamped - _fontSize) < 0.01 && _charWidth > 0) return;
        _fontSize = clamped;
        MeasureCharacter();
        RecalculateGrid();
        InvalidateVisual();
        FontSizeChanged?.Invoke(clamped);
    }

    public void ZoomBy(double points) => SetFontSize(_fontSize + points);
    public void ResetZoom() => SetFontSize(DefaultFontSize);

    // MARK: - Couleurs (identiques à la version Windows)

    private static readonly Color[] AnsiColors =
    [
        Color.FromRgb(0x1E, 0x1E, 0x1E), Color.FromRgb(0xCD, 0x31, 0x31),
        Color.FromRgb(0x0D, 0xBC, 0x79), Color.FromRgb(0xE5, 0xE5, 0x10),
        Color.FromRgb(0x24, 0x72, 0xC8), Color.FromRgb(0xBC, 0x3F, 0xBC),
        Color.FromRgb(0x11, 0xA8, 0xCD), Color.FromRgb(0xD4, 0xD4, 0xD4),
        Color.FromRgb(0x66, 0x66, 0x66), Color.FromRgb(0xF1, 0x4C, 0x4C),
        Color.FromRgb(0x23, 0xD1, 0x8B), Color.FromRgb(0xF5, 0xF5, 0x43),
        Color.FromRgb(0x3B, 0x8E, 0xEA), Color.FromRgb(0xD6, 0x70, 0xD6),
        Color.FromRgb(0x29, 0xB8, 0xDB), Color.FromRgb(0xFF, 0xFF, 0xFF),
    ];

    private static readonly IBrush[] AnsiBrushes =
        AnsiColors.Select(c => (IBrush)new ImmutableSolidColorBrush(c)).ToArray();

    public static readonly IBrush DefaultBackground = new ImmutableSolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
    private static readonly IBrush CursorBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x99, 0xD4, 0xD4, 0xD4));
    private static readonly IPen CursorOutline = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0xD4, 0xD4, 0xD4)), 1);
    private static readonly IBrush SizeBadgeBackground = new ImmutableSolidColorBrush(Color.FromArgb(0xD9, 0, 0, 0));
    private static readonly IBrush ScrollbarTrack = new ImmutableSolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush ScrollbarThumb = new ImmutableSolidColorBrush(Color.FromArgb(0x96, 0xD4, 0xD4, 0xD4));
    private static readonly IBrush MatchBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x62, 0x33, 0x15));
    private static readonly IBrush CurrentMatchBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x9E, 0x6A, 0x03));

    private readonly DispatcherTimer _autoScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private int _autoScrollDirection;
    private Point _lastPointer;

    private readonly DispatcherTimer _sizeBadgeTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private bool _gridInitialized;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        _typeface = new Typeface(TerminalFont);
        _boldTypeface = new Typeface(TerminalFont, FontStyle.Normal, FontWeight.Bold);
        MeasureCharacter();

        _autoScrollTimer.Tick += (_, _) => AutoScrollStep();
        _sizeBadgeTimer.Tick += (_, _) =>
        {
            _sizeBadgeTimer.Stop();
            InvalidateVisual();
        };
        BuildContextMenu();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _autoScrollTimer.Stop();
        _sizeBadgeTimer.Stop();
    }

    /// <summary>Toute la surface est cliquable, zones vides comprises.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    // MARK: - Menu contextuel

    private MenuItem _copyItem = null!;
    private MenuItem _searchItem = null!;
    private MenuItem _commandsItem = null!;
    private MenuItem _reconnectItem = null!;
    private Separator _reconnectSeparator = null!;

    private static string Gesture(string windows, string mac) => OperatingSystem.IsMacOS() ? mac : windows;

    private void BuildContextMenu()
    {
        MenuItem Item(string header, string? gesture, Action action)
        {
            var item = new MenuItem { Header = MenuHeader(header, gesture) };
            item.Click += (_, _) => action();
            return item;
        }

        _copyItem = Item("Copier", Gesture("Ctrl+C", "⌘C"), () => _ = CopyToClipboardAsync(selectionOnly: true));
        _searchItem = Item("Rechercher…", Gesture("Ctrl+Maj+R", "⌘F"), () => SearchRequested?.Invoke());
        _commandsItem = Item("Commandes…", Gesture("Ctrl+Maj+K", "⇧⌘K"), () =>
            Dispatcher.UIThread.Post(() => CommandsRequested?.Invoke(), DispatcherPriority.Input));
        _reconnectItem = Item("Reconnecter", null, () => ReconnectRequested?.Invoke());
        _reconnectSeparator = new Separator();

        var menu = new ContextMenu();
        menu.Items.Add(_copyItem);
        menu.Items.Add(Item("Coller", Gesture("Ctrl+Maj+V", "⌘V"), () => PasteRequested?.Invoke()));
        menu.Items.Add(Item("Tout copier", null, () => _ = CopyToClipboardAsync(selectionOnly: false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(_commandsItem);
        menu.Items.Add(_searchItem);
        menu.Items.Add(Item("Effacer l'écran", OperatingSystem.IsMacOS() ? "⌘K" : null, Clear));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Texte plus grand", Gesture("Ctrl+Maj++", "⌘+"), () => ZoomBy(+1)));
        menu.Items.Add(Item("Texte plus petit", Gesture("Ctrl+Maj+-", "⌘-"), () => ZoomBy(-1)));
        menu.Items.Add(Item("Taille par défaut", Gesture("Ctrl+Maj+0", "⌘0"), ResetZoom));
        menu.Items.Add(_reconnectSeparator);
        menu.Items.Add(_reconnectItem);

        menu.Closed += (_, _) => Focus();
        menu.Opening += (_, _) =>
        {
            _copyItem.IsEnabled = HasSelection;
            _searchItem.IsVisible = SearchRequested is not null;
            _commandsItem.IsVisible = CommandsRequested is not null;
            _reconnectItem.IsVisible = _reconnectSeparator.IsVisible = ReconnectRequested is not null;
        };
        ContextMenu = menu;
    }

    /// <summary>
    /// Libellé de menu avec son raccourci en retrait à droite, rédigé en
    /// français (« Ctrl+Maj+V »), comme dans la version Windows.
    /// </summary>
    public static Control MenuHeader(string label, string? gesture)
    {
        var panel = new DockPanel();
        if (!string.IsNullOrEmpty(gesture))
        {
            var hint = new TextBlock
            {
                Text = gesture,
                Margin = new Thickness(28, 0, 0, 0),
                Foreground = Palette.Resource("TextSecondaryBrush"),
            };
            DockPanel.SetDock(hint, Dock.Right);
            panel.Children.Add(hint);
        }
        panel.Children.Add(new TextBlock { Text = label });
        return panel;
    }

    /// <summary>Copie la sélection — ou tout le terminal si demandé — puis lève la sélection.</summary>
    public async Task<bool> CopyToClipboardAsync(bool selectionOnly)
    {
        var text = selectionOnly ? GetSelectedText() : TextForCopy();
        if (string.IsNullOrEmpty(text) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
        try { await clipboard.SetTextAsync(text); }
        catch { return false; }
        ClearSelection();
        return true;
    }

    // MARK: - Mesure et grille

    private void MeasureCharacter()
    {
        var sample = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                       _typeface, _fontSize, Brushes.White);
        _charWidth = sample.WidthIncludingTrailingWhitespace;
        _charHeight = Math.Ceiling(sample.Height);
    }

    public void Write(string text)
    {
        _emulator.Write(text);
        _viewOffset = 0;
        ForgetMatches();
        InvalidateVisual();
    }

    public void Clear()
    {
        _emulator.Write("\u001B[2J\u001B[H");
        _emulator.ClearScrollback();
        ClearSelection();
        _viewOffset = 0;
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        RecalculateGrid();
    }

    private void RecalculateGrid()
    {
        if (_charWidth <= 0 || _charHeight <= 0) return;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        int cols = Math.Max(2, (int)(Bounds.Width / _charWidth));
        int rows = Math.Max(2, (int)(Bounds.Height / _charHeight));
        if (cols == _emulator.Cols && rows == _emulator.Rows) return;

        _emulator.Resize(cols, rows);
        ClampViewOffset();
        if (_gridInitialized)
        {
            _sizeBadgeTimer.Stop();
            _sizeBadgeTimer.Start();
        }
        _gridInitialized = true;
        GridSizeChanged?.Invoke(cols, rows);
        InvalidateVisual();
    }

    // MARK: - Historique

    private void ScrollBy(int lines) => ScrollTo(_viewOffset + lines);

    private void ScrollTo(int offset)
    {
        var clamped = Math.Clamp(offset, 0, _emulator.ScrollbackCount);
        if (clamped == _viewOffset) return;
        _viewOffset = clamped;
        InvalidateVisual();
    }

    private void ClampViewOffset() => _viewOffset = Math.Clamp(_viewOffset, 0, _emulator.ScrollbackCount);

    /// <summary>Molette du trackpad : petits déplacements fractionnaires cumulés.</summary>
    private double _wheelAccumulator;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var zoomKey = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers.HasFlag(zoomKey))
        {
            if (Math.Abs(e.Delta.Y) >= 0.5) ZoomBy(Math.Sign(e.Delta.Y));
            e.Handled = true;
            return;
        }

        // Trois lignes par cran, comme la plupart des terminaux.
        _wheelAccumulator += e.Delta.Y * 3;
        var lines = (int)_wheelAccumulator;
        if (lines != 0)
        {
            _wheelAccumulator -= lines;
            ScrollBy(lines);
        }
        e.Handled = true;
    }

    /// <summary>
    /// Touches qui pilotent la vue plutôt que le shell (Maj+Page↑/↓,
    /// Ctrl+Maj+Début/Fin, zoom). Toute autre frappe ramène la vue en bas.
    /// </summary>
    public bool HandleViewKey(Key key, KeyModifiers modifiers)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var cmd = modifiers.HasFlag(KeyModifiers.Meta);

        if (shift && !ctrl)
        {
            switch (key)
            {
                case Key.PageUp: ScrollBy(_emulator.Rows - 1); return true;
                case Key.PageDown: ScrollBy(-(_emulator.Rows - 1)); return true;
            }
        }

        // Zoom : Ctrl+Maj sous Windows (comme la version WPF), Cmd sur Mac.
        if ((shift && ctrl) || (cmd && OperatingSystem.IsMacOS()))
        {
            switch (key)
            {
                case Key.Home when shift && ctrl: ScrollTo(_emulator.ScrollbackCount); return true;
                case Key.End when shift && ctrl: ScrollTo(0); return true;
                case Key.OemPlus or Key.Add: ZoomBy(+1); return true;
                case Key.OemMinus or Key.Subtract: ZoomBy(-1); return true;
                case Key.D0 or Key.NumPad0: ResetZoom(); return true;
            }
        }

        if (!KeyMapping.IsModifier(key)) ScrollTo(0);
        return false;
    }

    // MARK: - Rendu

    public override void Render(DrawingContext dc)
    {
        var size = Bounds.Size;
        dc.DrawRectangle(DefaultBackground, null, new Rect(size));
        if (_charWidth <= 0) return;

        for (int viewRow = 0; viewRow < _emulator.Rows; viewRow++)
        {
            double y = viewRow * _charHeight;
            var line = LineForView(viewRow);

            // Fonds par plages contiguës.
            int runStart = 0;
            for (int col = 1; col <= line.Length; col++)
            {
                bool endOfRun = col == line.Length || !SameBackground(line[col], line[runStart]);
                if (!endOfRun) continue;
                if (ResolveBackground(line[runStart]) is { } background)
                    dc.DrawRectangle(background, null,
                        new Rect(runStart * _charWidth, y, (col - runStart) * _charWidth, _charHeight));
                runStart = col;
            }

            DrawMatches(dc, viewRow, y);

            // Texte par plages de même style.
            int textStart = 0;
            while (textStart < line.Length)
            {
                int end = textStart;
                while (end < line.Length && SameForeground(line[end], line[textStart])) end++;

                var chars = new char[end - textStart];
                for (int i = textStart; i < end; i++) chars[i - textStart] = line[i].Char == '\0' ? ' ' : line[i].Char;
                var text = new string(chars);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var cell = line[textStart];
                    var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        cell.Bold ? _boldTypeface : _typeface, _fontSize, ResolveForeground(cell));
                    if (cell.Underline) formatted.SetTextDecorations(TextDecorations.Underline);
                    dc.DrawText(formatted, new Point(textStart * _charWidth, y));
                }
                textStart = end;
            }
        }

        // Curseur : bloc semi-transparent avec le focus, contour sans.
        if (_emulator.CursorVisible && _viewOffset == 0)
        {
            var cursor = new Rect(_emulator.CursorCol * _charWidth, _emulator.CursorRow * _charHeight,
                                  _charWidth, _charHeight);
            if (IsFocused)
                dc.DrawRectangle(CursorBrush, null, cursor);
            else
                dc.DrawRectangle(null, CursorOutline,
                    new Rect(cursor.X + 0.5, cursor.Y + 0.5,
                             Math.Max(0, cursor.Width - 1), Math.Max(0, cursor.Height - 1)));
        }

        DrawScrollbar(dc, size);
        DrawSizeBadge(dc, size);
    }

    private void DrawSizeBadge(DrawingContext dc, Size size)
    {
        if (!_sizeBadgeTimer.IsEnabled) return;
        var text = new FormattedText($"{_emulator.Cols} × {_emulator.Rows}", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _typeface, Math.Max(12, _fontSize), AnsiBrushes[15]);
        var width = text.Width + 28;
        var height = text.Height + 14;
        var box = new Rect((size.Width - width) / 2, (size.Height - height) / 2, width, height);
        dc.DrawRectangle(SizeBadgeBackground, null, box, 8, 8);
        dc.DrawText(text, new Point(box.X + 14, box.Y + 7));
    }

    /// <summary>La ligne à dessiner, sélection comprise (cellules en vidéo inverse).</summary>
    private TerminalCell[] LineForView(int viewRow)
    {
        var row = viewRow - _viewOffset;
        var line = _emulator.LineAt(row);

        if (line.Length == 0 || !TryGetSelectionRange(out var start, out var end)) return line;
        if (row < start.Row || row > end.Row) return line;

        var from = Math.Clamp(row == start.Row ? start.Col : 0, 0, line.Length);
        var to = Math.Clamp(row == end.Row ? end.Col : line.Length, 0, line.Length);
        if (from >= to) return line;

        var highlighted = (TerminalCell[])line.Clone();
        for (int c = from; c < to; c++) highlighted[c].Inverse = !highlighted[c].Inverse;
        return highlighted;
    }

    private void DrawScrollbar(DrawingContext dc, Size size)
    {
        if (!ScrollbarVisible || size.Height <= 0) return;
        double total = _emulator.ScrollbackCount + _emulator.Rows;
        double x = size.Width - ScrollbarWidth;
        dc.DrawRectangle(ScrollbarTrack, null, new Rect(x, 0, ScrollbarWidth, size.Height));

        var thumbHeight = Math.Max(MinThumbHeight, size.Height * _emulator.Rows / total);
        var top = Math.Clamp((_emulator.ScrollbackCount - _viewOffset) / total * size.Height,
                             0, size.Height - thumbHeight);
        dc.DrawRectangle(ScrollbarThumb, null, new Rect(x + 2, top, ScrollbarWidth - 4, thumbHeight), 2, 2);
    }

    private static bool SameBackground(TerminalCell a, TerminalCell b)
        => a.Background == b.Background && a.Inverse == b.Inverse && a.Foreground == b.Foreground;

    private static bool SameForeground(TerminalCell a, TerminalCell b)
        => a.Foreground == b.Foreground && a.Bold == b.Bold && a.Underline == b.Underline
           && a.Inverse == b.Inverse && a.Background == b.Background;

    private static IBrush? ResolveBackground(TerminalCell cell)
    {
        if (cell.Inverse) return AnsiBrushes[Math.Min(cell.Foreground, (byte)15)];
        return cell.Background == 0 ? null : AnsiBrushes[Math.Min(cell.Background - 1, 15)];
    }

    private static IBrush ResolveForeground(TerminalCell cell)
    {
        if (cell.Inverse)
            return cell.Background == 0 ? DefaultBackground : AnsiBrushes[Math.Min(cell.Background - 1, 15)];
        var index = cell.Foreground;
        if (cell.Bold && index < 8) index += 8;   // le gras éclaircit, convention des terminaux
        return AnsiBrushes[Math.Min(index, (byte)15)];
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    // MARK: - Sélection

    private readonly record struct CellPosition(int Row, int Col) : IComparable<CellPosition>
    {
        public int CompareTo(CellPosition other) =>
            Row != other.Row ? Row.CompareTo(other.Row) : Col.CompareTo(other.Col);
    }

    private CellPosition? _selectionAnchor;
    private CellPosition? _selectionFocus;
    private bool _selecting;

    public bool HasSelection => TryGetSelectionRange(out _, out _);

    public void ClearSelection()
    {
        if (_selectionAnchor is null && _selectionFocus is null) return;
        _selectionAnchor = _selectionFocus = null;
        InvalidateVisual();
    }

    public string TextForCopy() => GetSelectedText() ?? _emulator.GetText();

    public string? GetSelectedText()
    {
        if (!TryGetSelectionRange(out var start, out var end)) return null;
        var sb = new System.Text.StringBuilder();
        for (int row = start.Row; row <= end.Row; row++)
        {
            if (row == end.Row && end.Col == 0 && row != start.Row) break;
            var line = _emulator.LineAt(row);
            var from = Math.Clamp(row == start.Row ? start.Col : 0, 0, line.Length);
            var to = Math.Clamp(row == end.Row ? end.Col : line.Length, 0, line.Length);
            if (row > start.Row) sb.Append('\n');
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

    private int RowAt(Point p) =>
        Math.Clamp((int)Math.Floor(p.Y / _charHeight), 0, _emulator.Rows - 1) - _viewOffset;

    private CellPosition PointToCell(Point p) => new(RowAt(p), Math.Max(0, (int)Math.Floor(p.X / _charWidth)));
    private CellPosition PointToBoundary(Point p) => new(RowAt(p), Math.Max(0, (int)Math.Round(p.X / _charWidth)));

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

    private void SelectLineAt(CellPosition cell)
    {
        _selectionAnchor = new CellPosition(cell.Row, 0);
        _selectionFocus = new CellPosition(cell.Row + 1, 0);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var props = e.GetCurrentPoint(this).Properties;

        // Clic du milieu : coller, convention des terminaux Unix.
        if (props.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
        {
            PasteRequested?.Invoke();
            e.Handled = true;
            return;
        }
        if (props.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || _charWidth <= 0 || _charHeight <= 0)
            return;

        var point = e.GetPosition(this);
        _lastPointer = point;

        if (ScrollbarVisible && point.X >= Bounds.Width - ScrollbarWidth)
        {
            _draggingScrollbar = true;
            ScrollToBarPosition(point.Y);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        switch (e.ClickCount)
        {
            case 2: SelectWordAt(PointToCell(point)); break;
            case 3: SelectLineAt(PointToCell(point)); break;
            default:
                _selectionAnchor = _selectionFocus = PointToBoundary(point);
                _selecting = true;
                e.Pointer.Capture(this);
                break;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_charWidth <= 0 || _charHeight <= 0) return;
        var point = e.GetPosition(this);
        _lastPointer = point;

        var near = _emulator.ScrollbackCount > 0 && point.X >= Bounds.Width - ScrollbarWidth * 2;
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

        _autoScrollDirection = point.Y < 0 ? 1 : point.Y > Bounds.Height ? -1 : 0;
        if (_autoScrollDirection != 0) _autoScrollTimer.Start();
        else _autoScrollTimer.Stop();

        _selectionFocus = PointToBoundary(point);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        _selecting = false;
        _draggingScrollbar = false;
        _autoScrollDirection = 0;
        _autoScrollTimer.Stop();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_pointerNearScrollbar && !_draggingScrollbar)
        {
            _pointerNearScrollbar = false;
            InvalidateVisual();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
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
        _selectionFocus = PointToBoundary(_lastPointer);
        InvalidateVisual();
    }

    // MARK: - Recherche

    private readonly record struct SearchMatch(int Row, int Col, int Length);

    private readonly List<SearchMatch> _matches = new();
    private readonly Dictionary<int, List<SearchMatch>> _matchesByRow = new();
    private int _currentMatch = -1;

    public int MatchCount => _matches.Count;
    public int CurrentMatchNumber => _currentMatch < 0 ? 0 : _currentMatch + 1;

    /// <summary>Cherche dans tout le flux, sans tenir compte de la casse ; la dernière occurrence est retenue.</summary>
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
                    from = at + 1;
                }
            }
        }
        if (_matches.Count > 0) GoToMatch(_matches.Count - 1);
        else InvalidateVisual();
    }

    private void ForgetMatches()
    {
        if (_matches.Count == 0) return;
        _matches.Clear();
        _matchesByRow.Clear();
        _currentMatch = -1;
    }

    public void FindNext() => StepMatch(+1);
    public void FindPrevious() => StepMatch(-1);

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
        ScrollTo(_emulator.Rows / 2 - _matches[index].Row);
        InvalidateVisual();
    }

    private void DrawMatches(DrawingContext dc, int viewRow, double y)
    {
        if (_matches.Count == 0) return;
        if (!_matchesByRow.TryGetValue(viewRow - _viewOffset, out var matches)) return;
        var current = _currentMatch >= 0 ? _matches[_currentMatch] : default;
        foreach (var match in matches)
            dc.DrawRectangle(match == current ? CurrentMatchBrush : MatchBrush, null,
                new Rect(match.Col * _charWidth, y, match.Length * _charWidth, _charHeight));
    }

    // MARK: - Glissière d'historique

    private bool _draggingScrollbar;
    private bool _pointerNearScrollbar;
    private const double MinThumbHeight = 24;

    /// <summary>Visible seulement quand elle sert : on a remonté, on la manipule, ou la souris s'en approche.</summary>
    private bool ScrollbarVisible =>
        _emulator.ScrollbackCount > 0 && (_viewOffset > 0 || _draggingScrollbar || _pointerNearScrollbar);

    private void ScrollToBarPosition(double y)
    {
        var height = Bounds.Height;
        if (height <= 0) return;
        double total = _emulator.ScrollbackCount + _emulator.Rows;
        var thumbHeight = Math.Max(MinThumbHeight, height * _emulator.Rows / total);
        var ratio = (y - thumbHeight / 2) / Math.Max(1, height - thumbHeight);
        var topLine = (int)Math.Round(Math.Clamp(ratio, 0, 1) * _emulator.ScrollbackCount);
        ScrollTo(_emulator.ScrollbackCount - topLine);
    }
}
