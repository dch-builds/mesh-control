using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace MeshControl.Mac;

/// <summary>
/// Recherche dans l'historique du terminal, en surimpression en haut à droite
/// — portage de SSH/TerminalSearchBar.xaml. Posée par-dessus le texte : une
/// bande insérée changerait la hauteur de la grille, donc la taille annoncée
/// au shell, à chaque ouverture.
/// </summary>
public sealed class TerminalSearchBar : Border
{
    private readonly TextBox _query;
    private readonly TextBlock _count;
    private readonly Button _previous;
    private readonly Button _next;
    private TerminalControl? _terminal;

    public TerminalSearchBar()
    {
        IsVisible = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(0, 12, 16, 0);
        Background = new SolidColorBrush(Color.Parse("#2A2A30"));
        BorderBrush = new SolidColorBrush(Color.Parse("#3C3C44"));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(8, 6);
        BoxShadow = BoxShadows.Parse("0 2 12 0 #50000000");

        _query = new TextBox
        {
            Width = 190,
            FontSize = 12,
            Padding = new Thickness(6, 3),
            Background = new SolidColorBrush(Color.Parse("#1B1B1F")),
            Foreground = new SolidColorBrush(Color.Parse("#E4E6EA")),
            CaretBrush = new SolidColorBrush(Color.Parse("#E4E6EA")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3C3C44")),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _query.Classes.Add("terminalSearch");
        _query.TextChanged += (_, _) => Run();
        _query.KeyDown += Query_KeyDown;

        _count = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#9A9AA2")),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 76,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
        };

        _previous = SearchButton("▲", "Occurrence précédente (Maj+Entrée)", () => { _terminal?.FindPrevious(); UpdateCount(); });
        _next = SearchButton("▼", "Occurrence suivante (Entrée)", () => { _terminal?.FindNext(); UpdateCount(); });
        var close = SearchButton("✕", "Fermer (Échap)", Close);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_query);
        row.Children.Add(_count);
        row.Children.Add(_previous);
        row.Children.Add(_next);
        row.Children.Add(close);
        Child = row;
    }

    private static Button SearchButton(string glyph, string tip, Action action)
    {
        var button = new Button { Content = glyph };
        button.Classes.Add("terminalSearch");
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    public bool IsOpen => IsVisible;

    public void Attach(TerminalControl terminal) => _terminal = terminal;

    public void Open()
    {
        IsVisible = true;
        Run();
        Dispatcher.UIThread.Post(() =>
        {
            _query.Focus();
            _query.SelectAll();
        }, DispatcherPriority.Input);
    }

    public void Close()
    {
        IsVisible = false;
        _terminal?.Search(null);
        _terminal?.Focus();
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    private void Run()
    {
        if (_terminal is null) return;
        _terminal.Search(_query.Text);
        UpdateCount();
    }

    private void UpdateCount()
    {
        if (_terminal is null) return;
        var total = _terminal.MatchCount;
        _count.Text = string.IsNullOrEmpty(_query.Text)
            ? ""
            : total == 0 ? "Aucun résultat" : $"{_terminal.CurrentMatchNumber} / {total}";
        _previous.IsEnabled = _next.IsEnabled = total > 0;
    }

    private void Query_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _terminal?.FindPrevious();
                else _terminal?.FindNext();
                UpdateCount();
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
