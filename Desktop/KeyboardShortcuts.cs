namespace MeshControl.Desktop;

/// <summary>Codes de touches virtuelles Windows utilisés par les raccourcis.</summary>
internal static class Vk
{
    public const byte Backspace = 0x08;
    public const byte Tab = 0x09;
    public const byte Enter = 0x0D;
    public const byte Shift = 0x10;
    public const byte Ctrl = 0x11;
    public const byte Alt = 0x12;
    public const byte Escape = 0x1B;
    public const byte Space = 0x20;
    public const byte End = 0x23;
    public const byte Home = 0x24;
    public const byte Left = 0x25;
    public const byte Up = 0x26;
    public const byte Right = 0x27;
    public const byte Down = 0x28;
    public const byte PrintScreen = 0x2C;
    public const byte Delete = 0x2E;
    public const byte LeftWin = 0x5B;
    public const byte F1 = 0x70;
    public const byte F2 = 0x71;
    public const byte F3 = 0x72;
    public const byte F4 = 0x73;
    public const byte F5 = 0x74;
    public const byte F11 = 0x7A;

    // Lettres et chiffres : le code VK correspond au caractère ASCII majuscule.
    public static byte Letter(char c) => (byte)char.ToUpperInvariant(c);
}

/// <summary>
/// Un raccourci envoyable à la machine distante : des modificateurs maintenus
/// pendant l'appui d'une touche.
/// </summary>
/// <param name="Label">Intitulé affiché dans le menu.</param>
/// <param name="Description">Ce que fait le raccourci sur la machine distante.</param>
/// <param name="Modifiers">Touches maintenues (Ctrl, Alt, Maj, Win).</param>
/// <param name="Key">Touche principale.</param>
/// <param name="Extended">Touche « étendue » (flèches, Suppr, pavé numérique…).</param>
public sealed record RemoteShortcut(
    string Label,
    string Description,
    byte[] Modifiers,
    byte Key,
    bool Extended = false);

/// <summary>
/// Catalogue des raccourcis proposés dans le menu du bureau à distance.
///
/// Note : le protocole KVM transporte des codes VK Windows. L'agent MeshCentral
/// sous Linux les retraduit vers X11/uinput, si bien que les combinaisons
/// courantes fonctionnent des deux côtés — mais la sémantique diffère (la touche
/// Win devient Super, etc.), d'où deux listes distinctes.
/// </summary>
public static class KeyboardShortcuts
{
    /// <summary>Raccourcis pertinents sur une machine Windows.</summary>
    public static readonly RemoteShortcut[] Windows =
    [
        new("Ctrl + Alt + Suppr", "Écran de sécurité Windows",
            [Vk.Ctrl, Vk.Alt], Vk.Delete, Extended: true),
        new("Alt + Tab", "Basculer entre les fenêtres",
            [Vk.Alt], Vk.Tab),
        new("Win", "Ouvrir le menu Démarrer",
            [], Vk.LeftWin),
        new("Win + D", "Afficher le Bureau",
            [Vk.LeftWin], Vk.Letter('D')),
        new("Win + E", "Ouvrir l'Explorateur de fichiers",
            [Vk.LeftWin], Vk.Letter('E')),
        new("Win + R", "Ouvrir Exécuter",
            [Vk.LeftWin], Vk.Letter('R')),
        new("Win + L", "Verrouiller la session",
            [Vk.LeftWin], Vk.Letter('L')),
        new("Win + Maj + S", "Capture d'écran (outil Capture)",
            [Vk.LeftWin, Vk.Shift], Vk.Letter('S')),
        new("Alt + F4", "Fermer la fenêtre active",
            [Vk.Alt], Vk.F4),
        new("Ctrl + Maj + Échap", "Ouvrir le Gestionnaire des tâches",
            [Vk.Ctrl, Vk.Shift], Vk.Escape),
        new("Ctrl + Maj + N", "Nouveau dossier (Explorateur)",
            [Vk.Ctrl, Vk.Shift], Vk.Letter('N')),
        new("F5", "Actualiser",
            [], Vk.F5),
        new("F11", "Plein écran",
            [], Vk.F11),
    ];

    /// <summary>Raccourcis pertinents sur une machine Linux.</summary>
    public static readonly RemoteShortcut[] Linux =
    [
        new("Ctrl + Alt + T", "Ouvrir un terminal",
            [Vk.Ctrl, Vk.Alt], Vk.Letter('T')),
        new("Ctrl + Alt + F1", "Basculer vers la console TTY1",
            [Vk.Ctrl, Vk.Alt], Vk.F1),
        new("Ctrl + Alt + F2", "Basculer vers la console TTY2",
            [Vk.Ctrl, Vk.Alt], Vk.F2),
        new("Ctrl + Alt + F7", "Revenir à l'environnement graphique",
            [Vk.Ctrl, Vk.Alt], 0x76 /* F7 */),
        new("Alt + Tab", "Basculer entre les fenêtres",
            [Vk.Alt], Vk.Tab),
        new("Super", "Vue d'ensemble des activités",
            [], Vk.LeftWin),
        new("Ctrl + Alt + L", "Verrouiller la session",
            [Vk.Ctrl, Vk.Alt], Vk.Letter('L')),
        new("Alt + F2", "Lancer une commande",
            [Vk.Alt], Vk.F2),
        new("Alt + F4", "Fermer la fenêtre active",
            [Vk.Alt], Vk.F4),
        new("Ctrl + Alt + Suppr", "Boîte de dialogue de déconnexion",
            [Vk.Ctrl, Vk.Alt], Vk.Delete, Extended: true),
        new("Ctrl + C", "Interrompre (terminal)",
            [Vk.Ctrl], Vk.Letter('C')),
        new("Ctrl + D", "Fin de saisie / fermer le shell",
            [Vk.Ctrl], Vk.Letter('D')),
    ];

    /// <summary>Raccourcis d'édition, valables sur les deux systèmes.</summary>
    public static readonly RemoteShortcut[] Editing =
    [
        new("Ctrl + C", "Copier", [Vk.Ctrl], Vk.Letter('C')),
        new("Ctrl + V", "Coller", [Vk.Ctrl], Vk.Letter('V')),
        new("Ctrl + X", "Couper", [Vk.Ctrl], Vk.Letter('X')),
        new("Ctrl + A", "Tout sélectionner", [Vk.Ctrl], Vk.Letter('A')),
        new("Ctrl + Z", "Annuler", [Vk.Ctrl], Vk.Letter('Z')),
        new("Ctrl + S", "Enregistrer", [Vk.Ctrl], Vk.Letter('S')),
        new("Échap", "Échap", [], Vk.Escape),
    ];
}
