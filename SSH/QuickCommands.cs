using MeshControl.Core;

namespace MeshControl.Ssh;

/// <summary>
/// Commande proposée par le menu « Commandes » du terminal.
/// </summary>
/// <param name="Label">Libellé du menu.</param>
/// <param name="Command">Texte envoyé au shell.</param>
/// <param name="Run">
/// Vrai : la commande part avec Entrée (lecture, navigation). Faux : elle est
/// seulement tapée, à vérifier puis valider — tout ce qui modifie la machine.
/// </param>
/// <param name="Note">Précision affichée dans l'info-bulle.</param>
internal sealed record QuickCommand(string Label, string Command, bool Run = true, string? Note = null);

/// <summary>Sous-menu du menu « Commandes ». Un élément null est un séparateur.</summary>
internal sealed record QuickCommandGroup(string Title, IReadOnlyList<QuickCommand?> Items);

/// <summary>
/// Commandes courantes de maintenance et de dépannage, écrites pour le shell
/// effectivement ouvert : cmd et PowerShell ne parlent pas la même langue, et
/// un shell administrateur tourne sous SYSTEM, dont le profil n'est pas celui
/// de l'utilisateur qu'on dépanne.
/// </summary>
internal static class QuickCommands
{
    public static IReadOnlyList<QuickCommandGroup> For(OSFamily os, ShellKind shell) =>
        os == OSFamily.Windows ? Windows(shell) : Unix();

    // MARK: - Windows

    private static IReadOnlyList<QuickCommandGroup> Windows(ShellKind shell)
    {
        var ps = shell is ShellKind.PowerShell or ShellKind.UserPowerShell;
        var asSystem = shell is ShellKind.Default or ShellKind.PowerShell;

        // Variante cmd ou PowerShell de la même action.
        string W(string cmd, string powershell) => ps ? powershell : cmd;

        // Script PowerShell, lancé tel quel ou depuis cmd. Il ne doit contenir
        // aucun guillemet double, qui fermerait l'argument côté cmd.
        string P(string script) => ps ? script : $"powershell -NoProfile -Command \"{script}\"";

        string Cd(string path) => ps ? $"cd '{path}'" : $"cd /d \"{path}\"";

        // Dossier calculé sur la machine par une expression PowerShell. Depuis
        // cmd, son résultat est récupéré par for /f (usebackq : l'expression
        // peut alors contenir des apostrophes).
        string CdTo(string expression) => ps
            ? $"Set-Location $({expression})"
            : $"for /f \"usebackq delims=\" %p in (`powershell -NoProfile -Command \"{expression}\"`) do @cd /d \"%p\"";

        // Dossiers de l'utilisateur, lus dans « Shell Folders » : les chemins y
        // sont absolus et suivent les redirections (OneDrive, partages). Sous
        // SYSTEM, c'est la ruche de l'utilisateur connecté à la console qu'on
        // consulte, retrouvée par son SID.
        var shellFolders = asSystem
            ? "$k='Registry::HKEY_USERS\\'+([Security.Principal.NTAccount](Get-CimInstance Win32_ComputerSystem).UserName).Translate([Security.Principal.SecurityIdentifier]).Value+'\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Shell Folders'"
            : "$k='HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Shell Folders'";
        string UserFolder(string value) => CdTo($"{shellFolders}; {value}");
        string Folder(string name) => $"(Get-ItemProperty $k).'{name}'";

        var userNote = asSystem
            ? "Dossier de l'utilisateur connecté à la console de la machine (pas d'une session Bureau à distance)."
            : null;

        var folders = new QuickCommandGroup("Dossiers",
        [
            new("Dossier de l'utilisateur",
                UserFolder($"Split-Path (Split-Path {Folder("Local AppData")})"), Note: userNote),
            new("Bureau", UserFolder(Folder("Desktop")), Note: userNote),
            new("Téléchargements", UserFolder(Folder("{374DE290-123F-4565-9164-39C4925E467B}")), Note: userNote),
            new("Documents", UserFolder(Folder("Personal")), Note: userNote),
            new("AppData (Roaming)", UserFolder(Folder("AppData")), Note: userNote),
            new("AppData (Local)", UserFolder(Folder("Local AppData")), Note: userNote),
            new("Temp de l'utilisateur", UserFolder($"Join-Path {Folder("Local AppData")} 'Temp'"), Note: userNote),
            null,
            new("Utilisateurs (C:\\Users)", Cd("C:\\Users")),
            new("Program Files", Cd("C:\\Program Files")),
            new("Program Files (x86)", Cd("C:\\Program Files (x86)")),
            new("ProgramData", Cd("C:\\ProgramData")),
            new("Temp de Windows", Cd("C:\\Windows\\Temp")),
            new("Dossier du fichier hosts", Cd("C:\\Windows\\System32\\drivers\\etc")),
            new("Racine du disque (C:\\)", Cd("C:\\")),
        ]);

        var info = new QuickCommandGroup("Informations",
        [
            new("Utilisateurs connectés", "query user"),
            new("Informations système", "systeminfo"),
            new("Dernier démarrage", P("(Get-CimInstance Win32_OperatingSystem).LastBootUpTime")),
            new("Espace disque", P("Get-PSDrive -PSProvider FileSystem")),
            null,
            new("Processus les plus gourmands (mémoire)",
                P("Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 15 Name,Id,@{n='Mo';e={[int]($_.WorkingSet64/1MB)}},CPU | Format-Table -AutoSize")),
            new("Services automatiques arrêtés",
                P("Get-Service | Where-Object {$_.StartType -eq 'Automatic' -and $_.Status -ne 'Running'} | Format-Table -AutoSize Name,DisplayName,Status")),
            new("Logiciels installés",
                P("Get-ItemProperty HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*,HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\* | Where-Object DisplayName | Sort-Object DisplayName | Format-Table -AutoSize DisplayName,DisplayVersion,Publisher")),
            new("Dernières mises à jour installées",
                P("Get-HotFix | Sort-Object InstalledOn -Descending | Select-Object -First 10 | Format-Table -AutoSize HotFixID,Description,InstalledOn")),
            new("Erreurs système récentes",
                P("Get-WinEvent -FilterHashtable @{LogName='System';Level=1,2} -MaxEvents 20 | Format-Table -AutoSize -Wrap TimeCreated,Id,ProviderName,Message")),
            new("Imprimantes",
                P("Get-Printer | Format-Table -AutoSize Name,DriverName,PortName,PrinterStatus")),
        ]);

        var network = new QuickCommandGroup("Réseau",
        [
            new("Configuration réseau", "ipconfig /all"),
            new("Tester Internet (ping)", "ping -n 4 8.8.8.8"),
            new("Tester la résolution DNS", "nslookup www.google.com"),
            new("Tester un port", P("Test-NetConnection serveur -Port 443"), Run: false,
                Note: "Remplacez « serveur » et le port avant de valider."),
            null,
            new("Ports en écoute", "netstat -ano | findstr LISTENING"),
            new("Table de routage", "route print"),
            new("Lecteurs réseau connectés", "net use"),
            new("Vider le cache DNS", "ipconfig /flushdns"),
        ]);

        var maintenance = new QuickCommandGroup("Maintenance",
        [
            new("Vérifier les fichiers système (SFC)", "sfc /scannow", Run: false,
                Note: "Plusieurs minutes. Droits administrateur requis."),
            new("Réparer l'image Windows (DISM)", "DISM /Online /Cleanup-Image /RestoreHealth", Run: false,
                Note: "Plusieurs minutes, accès à Windows Update. Droits administrateur requis."),
            new("Forcer les stratégies de groupe", "gpupdate /force", Run: false),
            null,
            new("Redémarrer le spouleur d'impression",
                W("net stop spooler && net start spooler", "Restart-Service Spooler -Force"), Run: false),
            new("Vider la file d'impression",
                W("net stop spooler && del /q /f \"%windir%\\System32\\spool\\PRINTERS\\*\" && net start spooler",
                  "Stop-Service Spooler -Force; Remove-Item \"$env:windir\\System32\\spool\\PRINTERS\\*\" -Force; Start-Service Spooler"),
                Run: false, Note: "Supprime tous les documents en attente d'impression."),
            new("Nettoyer le Temp de Windows",
                W("del /q /f /s \"%windir%\\Temp\\*\"",
                  "Remove-Item \"$env:windir\\Temp\\*\" -Recurse -Force -ErrorAction SilentlyContinue"),
                Run: false, Note: "Les fichiers en cours d'utilisation sont ignorés."),
            new("Relancer l'Explorateur Windows", "taskkill /f /im explorer.exe", Run: false,
                Note: "Windows relance l'Explorateur de lui-même. Ferme les fenêtres de dossiers ouvertes."),
            null,
            new("Redémarrer dans 5 minutes",
                "shutdown /r /t 300 /c \"Maintenance : la machine redemarre dans 5 minutes.\"", Run: false,
                Note: "L'utilisateur est prévenu. « Annuler un arrêt programmé » l'interrompt."),
            new("Annuler un arrêt programmé", "shutdown /a"),
        ]);

        return [folders, info, network, maintenance];
    }

    // MARK: - Linux et macOS

    private static IReadOnlyList<QuickCommandGroup> Unix() =>
    [
        new("Dossiers",
        [
            new("Dossier personnel", "cd ~"),
            new("Utilisateurs (/home)", "cd /home"),
            new("Journaux (/var/log)", "cd /var/log"),
            new("Configuration (/etc)", "cd /etc"),
            new("Temporaires (/tmp)", "cd /tmp"),
            new("Applications (/opt)", "cd /opt"),
        ]),
        new("Informations",
        [
            new("Utilisateurs connectés", "who"),
            new("Système", "uname -a; cat /etc/os-release 2>/dev/null"),
            new("Durée de fonctionnement", "uptime"),
            new("Espace disque", "df -h"),
            new("Mémoire", "free -h"),
            null,
            new("Processus les plus gourmands (mémoire)", "ps aux --sort=-%mem | head -n 15"),
            new("Services en échec", "systemctl --failed"),
            new("Erreurs depuis le démarrage", "journalctl -p err -b --no-pager | tail -n 40"),
        ]),
        new("Réseau",
        [
            new("Adresses réseau", "ip -br address"),
            new("Tester Internet (ping)", "ping -c 4 8.8.8.8"),
            new("Tester la résolution DNS", "getent hosts www.google.com"),
            new("Ports en écoute", "ss -tulpn"),
            new("Table de routage", "ip route"),
        ]),
        new("Maintenance",
        [
            new("Mises à jour disponibles (Debian, Ubuntu)", "apt update && apt list --upgradable", Run: false),
            new("Redémarrer un service", "systemctl restart nom-du-service", Run: false,
                Note: "Remplacez « nom-du-service » avant de valider."),
            new("Purger les journaux de plus de 7 jours", "journalctl --vacuum-time=7d", Run: false),
            null,
            new("Redémarrer dans 5 minutes",
                "shutdown -r +5 \"Maintenance : la machine redemarre dans 5 minutes.\"", Run: false,
                Note: "« Annuler un arrêt programmé » l'interrompt."),
            new("Annuler un arrêt programmé", "shutdown -c"),
        ]),
    ];
}
