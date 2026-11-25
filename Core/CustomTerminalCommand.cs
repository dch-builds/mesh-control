namespace MeshControl.Core;

/// <summary>Shell auquel une commande personnalisée est destinée.</summary>
public enum CustomCommandShell
{
    /// <summary>Proposée quel que soit le shell ouvert.</summary>
    Any,
    /// <summary>Invite de commandes Windows (Admin Shell, User Shell).</summary>
    Cmd,
    /// <summary>PowerShell (Admin PowerShell, User PowerShell).</summary>
    PowerShell,
    /// <summary>Shells Linux et macOS.</summary>
    Unix,
}

/// <summary>
/// Commande ajoutée par l'utilisateur au menu « Commandes » du terminal.
/// Enregistrée dans ses réglages (%APPDATA%), donc propre à sa session Windows.
/// </summary>
public sealed class CustomTerminalCommand
{
    /// <summary>Libellé du menu.</summary>
    public string Label { get; set; } = "";

    /// <summary>Texte envoyé au shell ; plusieurs lignes possibles.</summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// Vrai : la commande part avec Entrée. Faux : elle est seulement tapée,
    /// à vérifier puis valider — comme les commandes intégrées qui modifient
    /// la machine.
    /// </summary>
    public bool Run { get; set; }

    /// <summary>Shell pour lequel la commande est proposée.</summary>
    public CustomCommandShell Shell { get; set; }

    public CustomTerminalCommand Clone() => (CustomTerminalCommand)MemberwiseClone();
}
