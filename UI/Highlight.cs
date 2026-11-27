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
///
/// Avec <c>Highlight.SplitLabel</c>, un nom de la forme « code - libellé »
/// s'affiche en deux temps : le code dans la graisse du TextBlock, le libellé
/// en texte secondaire. L'œil trouve plus vite le poste qu'il cherche.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Highlight),
            new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.RegisterAttached("Query", typeof(string), typeof(Highlight),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.Inherits, OnChanged));

    public static readonly DependencyProperty SplitLabelProperty =
        DependencyProperty.RegisterAttached("SplitLabel", typeof(bool), typeof(Highlight),
            new PropertyMetadata(false, OnChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    public static string GetQuery(DependencyObject d) => (string)d.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject d, string value) => d.SetValue(QueryProperty, value);

    public static bool GetSplitLabel(DependencyObject d) => (bool)d.GetValue(SplitLabelProperty);
    public static void SetSplitLabel(DependencyObject d, bool value) => d.SetValue(SplitLabelProperty, value);

    /// <summary>Séparateur entre le code d'une machine et son libellé.</summary>
    private const string LabelSeparator = " - ";

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Query étant héritée, ce rappel touche tous les éléments de la liste :
        // seuls les TextBlock qui portent un Highlight.Text nous concernent.
        if (d is not TextBlock block || GetText(block) is not { } text) return;

        block.Inlines.Clear();
        var query = GetQuery(block);

        var split = GetSplitLabel(block) ? text.IndexOf(LabelSeparator, StringComparison.Ordinal) : -1;
        if (split > 0 && split + LabelSeparator.Length < text.Length)
        {
            AddSegment(block, text[..split], query, label: false);
            block.Inlines.Add(new Run("  "));
            AddSegment(block, text[(split + LabelSeparator.Length)..], query, label: true);
        }
        else
        {
            AddSegment(block, text, query, label: false);
        }
    }

    /// <summary>Ajoute un segment de texte, la recherche surlignée.</summary>
    private static void AddSegment(TextBlock block, string text, string query, bool label)
    {
        Run Make(string part)
        {
            var run = new Run(part);
            if (label)
            {
                run.FontWeight = FontWeights.Normal;
                if (block.TryFindResource("TextSecondaryBrush") is Brush secondary) run.Foreground = secondary;
            }
            return run;
        }

        if (string.IsNullOrEmpty(query))
        {
            block.Inlines.Add(Make(text));
            return;
        }

        var brush = block.TryFindResource("SearchHighlightBrush") as Brush ?? Brushes.Khaki;
        var start = 0;
        while (start < text.Length)
        {
            // Même règle que le filtrage : sans accents ni casse (TextMatch).
            var index = TextMatch.IndexOf(text, query, start, out var length);
            if (index < 0 || length == 0) break;
            if (index > start) block.Inlines.Add(Make(text[start..index]));
            var hit = Make(text.Substring(index, length));
            hit.Background = brush;
            block.Inlines.Add(hit);
            start = index + length;
        }
        if (start < text.Length) block.Inlines.Add(Make(text[start..]));
    }
}
