using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using MeshControl.UI;

namespace MeshControl.Mac;

/// <summary>
/// Surligne le texte recherché dans les lignes de la liste (portage de
/// UI/Highlight.cs). <c>Query</c> est héritée : posée sur la liste, elle
/// atteint chaque ligne, et tout TextBlock qui porte un <c>Highlight.Text</c>
/// se recompose quand elle change.
/// </summary>
public sealed class Highlight : AvaloniaObject
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<Highlight, Control, string?>("Text");

    public static readonly AttachedProperty<string> QueryProperty =
        AvaloniaProperty.RegisterAttached<Highlight, Control, string>("Query", "", inherits: true);

    public static string? GetText(Control c) => c.GetValue(TextProperty);
    public static void SetText(Control c, string? value) => c.SetValue(TextProperty, value);
    public static string GetQuery(Control c) => c.GetValue(QueryProperty);
    public static void SetQuery(Control c, string value) => c.SetValue(QueryProperty, value);

    static Highlight()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, _) => Update(block));
        QueryProperty.Changed.AddClassHandler<TextBlock>((block, _) => Update(block));
    }

    private static void Update(TextBlock block)
    {
        if (GetText(block) is not { } text) return;
        var query = GetQuery(block);
        var inlines = new InlineCollection();

        // Les fragments reprennent explicitement la graisse du texte : sans
        // cela, le nom des machines (semi-gras) s'affichait en graisse normale.
        Run Make(string part) => new(part) { FontWeight = block.FontWeight };

        if (string.IsNullOrEmpty(query))
        {
            inlines.Add(Make(text));
        }
        else
        {
            var brush = Palette.Resource("SearchHighlightBrush");
            var start = 0;
            while (start < text.Length)
            {
                var index = TextMatch.IndexOf(text, query, start, out var length);
                if (index < 0 || length == 0)
                {
                    inlines.Add(Make(text[start..]));
                    break;
                }
                if (index > start) inlines.Add(Make(text[start..index]));
                var match = Make(text.Substring(index, length));
                match.Background = brush;
                inlines.Add(match);
                start = index + length;
            }
        }
        block.Inlines = inlines;
    }
}
