using System.Windows;

namespace MeshControl.Ssh;

/// <summary>
/// Prépare un texte collé avant son envoi au shell.
///
/// Coller plusieurs lignes dans un shell revient à les exécuter l'une après
/// l'autre, dès réception — avec les droits SYSTEM ou root sur un « Admin
/// Shell ». D'où deux protections :
///  - si le shell distant a activé le collage protégé (bracketed paste, que
///    bash, zsh et PSReadLine savent demander), le texte lui est remis d'un
///    seul bloc et rien ne s'exécute avant Entrée ;
///  - sinon, un collage de plusieurs lignes est confirmé d'abord.
/// Une seule ligne perd son retour à la ligne final : une ligne copiée par
/// triple-clic ne doit pas partir d'elle-même.
/// </summary>
internal static class TerminalPaste
{
    private const string BracketStart = "\u001B[200~";
    private const string BracketEnd = "\u001B[201~";

    /// <summary>
    /// Texte à envoyer, ou null si rien ne doit partir (presse-papiers vide,
    /// collage refusé).
    /// </summary>
    /// <param name="owner">Élément de la session, pour situer la confirmation.</param>
    public static string? Prepare(string? text, bool bracketed, DependencyObject owner)
    {
        if (string.IsNullOrEmpty(text)) return null;

        // Fins de ligne unifiées : le shell attend un retour chariot, comme
        // la touche Entrée.
        var normalized = RemoveControlCharacters(text.Replace("\r\n", "\n").Replace('\r', '\n'));

        if (bracketed)
        {
            // Plus aucun ESC dans le texte : un marqueur de fin glissé dedans
            // (même découpé pour se reformer après un premier filtrage) ne
            // peut plus faire sortir du bloc protégé.
            var body = normalized.Replace("\n", "\r");
            return BracketStart + body + BracketEnd;
        }

        var trimmed = normalized.TrimEnd('\n');
        if (trimmed.Length == 0) return null;

        var lineCount = trimmed.Split('\n').Length;
        if (lineCount == 1) return trimmed;

        if (!ConfirmMultiline(lineCount, owner)) return null;
        return normalized.Replace("\n", "\r");
    }

    /// <summary>
    /// Retire les caractères de contrôle, tabulation et saut de ligne mis à
    /// part. Un texte collé n'a aucune raison d'en contenir, et chacun peut
    /// piloter le shell : ESC ouvre une séquence (dont la fin du collage
    /// protégé), Ctrl+O ou Ctrl+J exécutent la ligne sous bash…
    /// </summary>
    public static string RemoveControlCharacters(string text)
    {
        if (!text.Any(IsUnwantedControl)) return text;
        return new string(text.Where(c => !IsUnwantedControl(c)).ToArray());
    }

    private static bool IsUnwantedControl(char c) =>
        (c < 0x20 && c is not '\t' and not '\n') || (c >= 0x7F && c <= 0x9F);

    private static bool ConfirmMultiline(int lineCount, DependencyObject owner)
    {
        const string caption = "Coller dans le terminal";
        var message = $"Coller {lineCount} lignes ?\n\n"
                      + "Le shell exécutera chaque ligne comme une commande, dès sa réception.";

        var window = Window.GetWindow(owner);
        var answer = window is not null
            ? MessageBox.Show(window, message, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            : MessageBox.Show(message, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return answer == MessageBoxResult.OK;
    }
}
