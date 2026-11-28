namespace MeshControl.Mac;

// Portage de UI/DeviceAction.cs (version Windows), à l'identique.

/// <summary>
/// Les actions applicables à une machine. Partagées entre le menu contextuel de
/// la liste et la palette de recherche rapide, pour que les deux proposent
/// exactement les mêmes possibilités.
/// </summary>
public enum DeviceAction
{
    Desktop,
    Terminal,
    Files,
    SendFiles,
    Ssh,
    Wake,
    Reboot,
    Sleep,
    PowerOff,
    Processes,
    OpenUrl,
    ToggleFavorite,
    Share,
    Rename,
    CopyName,
    SystemInfo,
    WebPage,
}

/// <summary>
/// Description d'une action pour l'affichage : libellé, symbole et conditions de
/// disponibilité. Une seule table décrit le menu et la palette.
/// </summary>
public sealed record DeviceActionInfo(
    DeviceAction Action,
    string Glyph,
    string Label,
    string Detail)
{
    /// <summary>
    /// Ordre d'apparition dans la palette. Le bureau à distance d'abord : c'est
    /// l'action attendue dans la grande majorité des cas.
    /// </summary>
    public static readonly IReadOnlyList<DeviceActionInfo> All = new[]
    {
        new DeviceActionInfo(DeviceAction.Desktop, "🖥", "Bureau à distance",
            "Prendre la main sur l'écran"),
        new DeviceActionInfo(DeviceAction.Terminal, "▶", "Terminal",
            "Invite de commandes / PowerShell via l'agent"),
        new DeviceActionInfo(DeviceAction.Files, "📁", "Fichiers",
            "Parcourir et transférer des fichiers"),
        new DeviceActionInfo(DeviceAction.SendFiles, "📤", "Envoyer des fichiers",
            "Vers cette machine et d'autres, en parallèle"),
        new DeviceActionInfo(DeviceAction.Ssh, "▶", "Terminal SSH",
            "Nécessite SSH activé sur le serveur"),
        new DeviceActionInfo(DeviceAction.WebPage, "🌐", "Ouvrir la page web",
            "Voir la machine dans MeshCentral"),
        new DeviceActionInfo(DeviceAction.SystemInfo, "ℹ", "Informations système",
            "Processeur, mémoire, disques, BIOS"),
        new DeviceActionInfo(DeviceAction.Processes, "⚙", "Processus",
            "Voir et arrêter les processus"),
        new DeviceActionInfo(DeviceAction.OpenUrl, "🔗", "Ouvrir une URL",
            "Lancer le navigateur de la machine"),
        new DeviceActionInfo(DeviceAction.Wake, "⏻", "Réveiller",
            "Signal Wake-on-LAN"),
        new DeviceActionInfo(DeviceAction.Reboot, "↻", "Redémarrer",
            "La machine va redémarrer"),
        new DeviceActionInfo(DeviceAction.Sleep, "☾", "Mettre en veille",
            "Suspendre la machine"),
        new DeviceActionInfo(DeviceAction.PowerOff, "⏼", "Éteindre",
            "Arrêter la machine"),
        new DeviceActionInfo(DeviceAction.ToggleFavorite, "★", "Favoris",
            "Épingler ou retirer des favoris"),
        new DeviceActionInfo(DeviceAction.Share, "🔑", "Partager un lien",
            "Créer un lien public temporaire"),
        new DeviceActionInfo(DeviceAction.Rename, "✏", "Renommer",
            "Changer le nom affiché dans MeshCentral"),
        new DeviceActionInfo(DeviceAction.CopyName, "📋", "Copier le nom",
            "Copier le nom de la machine"),
    };

    /// <summary>
    /// Mots-clés supplémentaires pour la recherche : on veut retrouver « Bureau
    /// à distance » en tapant « rdp » ou « écran ».
    /// </summary>
    public string Keywords => Action switch
    {
        DeviceAction.Desktop => "bureau distance ecran écran rdp kvm vnc",
        DeviceAction.Terminal => "terminal shell cmd powershell invite commande console",
        DeviceAction.Files => "fichiers dossier explorateur transfert upload download",
        DeviceAction.SendFiles => "envoyer fichiers deposer déposer plusieurs machines appareils upload copier",
        DeviceAction.Ssh => "ssh terminal secure shell",
        DeviceAction.WebPage => "web navigateur meshcentral page site url console",
        DeviceAction.SystemInfo => "informations systeme système sysinfo materiel matériel bios",
        DeviceAction.Processes => "processus taches tâches gestionnaire process kill",
        DeviceAction.OpenUrl => "url lien adresse navigateur ouvrir",
        DeviceAction.Wake => "reveiller réveiller wake wol allumer demarrer démarrer",
        DeviceAction.Reboot => "redemarrer redémarrer reboot restart",
        DeviceAction.Sleep => "veille sleep suspendre",
        DeviceAction.PowerOff => "eteindre éteindre arreter arrêter shutdown poweroff",
        DeviceAction.ToggleFavorite => "favori favoris epingler épingler etoile étoile",
        DeviceAction.Share => "partager partage lien public iframe",
        DeviceAction.Rename => "renommer nom changer modifier rename libelle libellé",
        DeviceAction.CopyName => "copier nom presse-papiers",
        _ => "",
    };
}
