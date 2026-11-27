using System.Globalization;

namespace MeshControl.UI;

/// <summary>
/// Comparaison de recherche, insensible à la casse et aux accents : « neg »
/// trouve « Négoce », « regie » trouve « Régie ». Une comparaison ordinale
/// exigeait de taper l'accent exact, ce que personne ne fait dans un champ de
/// recherche.
/// </summary>
public static class TextMatch
{
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    /// <summary>Le texte contient-il la recherche ? Une recherche vide correspond à tout.</summary>
    public static bool Contains(string? text, string query)
        => query.Length == 0 || (text is { Length: > 0 } && Compare.IndexOf(text, query, Options) >= 0);

    /// <summary>
    /// Position de la recherche dans le texte à partir de <paramref name="start"/>,
    /// et longueur réellement couverte (elle peut différer de celle de la
    /// recherche quand des caractères se décomposent). -1 si absente.
    /// </summary>
    public static int IndexOf(string text, string query, int start, out int length)
    {
        length = 0;
        if (start >= text.Length || query.Length == 0) return -1;
        var index = Compare.IndexOf(text.AsSpan(start), query.AsSpan(), Options, out length);
        return index < 0 ? -1 : start + index;
    }
}
