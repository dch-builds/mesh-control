using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MeshControl.UI;

/// <summary>
/// Surlignage du texte recherché dans un TextBlock.
///
/// Le TextBlock reçoit son texte par <c>Highlight.Text</c> au lieu de
/// <c>Text</c>, et la recherche par <c>Highlight.Query</c>. Query est héritée :
/// posée une fois sur la liste, elle descend jusqu'à chaque ligne, y compris
/// celles que la virtualisation crée ensuite.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Highlight),
            new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.RegisterAttached("Query", typeof(string), typeof(Highlight),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.Inherits, OnChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    public static string GetQuery(DependencyObject d) => (string)d.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject d, string value) => d.SetValue(QueryProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Query étant héritée, ce rappel touche tous les éléments de la liste :
        // seuls les TextBlock qui portent un Highlight.Text nous concernent.
        if (d is not TextBlock block || GetText(block) is not { } text) return;

        var query = GetQuery(block);
        block.Inlines.Clear();
        if (string.IsNullOrEmpty(query))
        {
            block.Inlines.Add(new Run(text));
            return;
        }

        var brush = block.TryFindResource("SearchHighlightBrush") as Brush ?? Brushes.Khaki;
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) break;
            if (index > start) block.Inlines.Add(new Run(text[start..index]));
            block.Inlines.Add(new Run(text.Substring(index, query.Length)) { Background = brush });
            start = index + query.Length;
        }
        if (start < text.Length) block.Inlines.Add(new Run(text[start..]));
    }
}
