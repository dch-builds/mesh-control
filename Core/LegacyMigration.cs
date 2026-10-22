using System.IO;

namespace MeshControl.Core;

/// <summary>
/// Reprise des données laissées par « Mesh Control », nom porté par l'application
/// jusqu'à la version 1.1.14.
///
/// Les réglages vivent dans un dossier qui porte le nom de l'application. Après
/// le renommage, l'application regarderait au mauvais endroit : l'utilisateur se
/// retrouverait déconnecté, avec un second facteur à ressaisir et un jeton de
/// connexion à recréer, alors que tout est intact à l'ancien emplacement. On
/// déplace donc les dossiers au premier démarrage.
///
/// Le chiffrement DPAPI du jeton est lié à la session Windows et non au chemin
/// du fichier : le jeton reste déchiffrable après déplacement.
///
/// IMPORTANT : <see cref="Run"/> doit s'exécuter avant tout accès à
/// <see cref="Log"/> ou <see cref="AppSettings"/>, qui créent leur dossier dès
/// le premier appel. Un dossier déjà présent, même vide, ferait échouer le
/// déplacement.
/// </summary>
public static class LegacyMigration
{
    private const string LegacyName  = "Mesh Control";
    private const string CurrentName = "Mesh Control";

    /// <summary>
    /// Ce qui a été repris, en attente d'être journalisé. La migration ne peut
    /// pas écrire dans le journal elle-même : elle déplace le dossier qui
    /// l'accueille.
    /// </summary>
    private static readonly List<string> Report = new();

    public static void Run()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        MoveFolder(Path.Combine(roaming, LegacyName),
                   Path.Combine(roaming, CurrentName), "réglages");

        MoveFolder(Path.Combine(local, LegacyName),
                   Path.Combine(local, CurrentName), "journaux");

        RenameLogFiles(Path.Combine(local, CurrentName, "logs"));

        // À partir d'ici seulement, journaliser est sans danger : le dossier des
        // journaux est à son emplacement définitif.
        if (StartupManager.MigrateFromLegacy())
            Report.Add("Démarrage automatique repris sous le nouveau nom.");
    }

    /// <summary>
    /// Verse le compte rendu dans le journal. À appeler après
    /// <c>Log.StartSession</c> : plus tôt, le dossier des journaux serait créé
    /// avant d'avoir été déplacé.
    /// </summary>
    public static void WriteReport()
    {
        foreach (var line in Report) Log.Info(line, "Migration");
        Report.Clear();
    }

    private static void MoveFolder(string from, string to, string label)
    {
        try
        {
            if (!Directory.Exists(from)) return;   // installation neuve : rien à reprendre

            if (Directory.Exists(to))
            {
                // Soit la reprise a déjà eu lieu, soit un démarrage interrompu a
                // laissé un dossier vide. Un dossier vide ne vaut pas d'être
                // gardé ; un dossier peuplé ne doit surtout pas être écrasé.
                if (Directory.EnumerateFileSystemEntries(to).Any())
                {
                    Report.Add($"Reprise des {label} abandonnée : « {to} » existe déjà et n'est pas vide.");
                    return;
                }

                Directory.Delete(to);
            }

            Directory.Move(from, to);
            Report.Add($"{label} repris depuis « {from} ».");
        }
        catch (Exception ex)
        {
            // Un échec ne doit pas empêcher le démarrage : l'application
            // repartira sur des réglages vierges — désagréable, mais pas
            // bloquant — et les données restent lisibles à l'ancien emplacement.
            Report.Add($"Reprise des {label} impossible : {ex.Message}");
        }
    }

    /// <summary>
    /// Aligne le nom des journaux repris sur le nouveau. Sans cela, l'ancienne
    /// série « mesh-control.log » cohabiterait indéfiniment avec la nouvelle sans
    /// jamais être recyclée par la rotation.
    /// </summary>
    private static void RenameLogFiles(string logFolder)
    {
        try
        {
            if (!Directory.Exists(logFolder)) return;

            foreach (var file in Directory.GetFiles(logFolder, "mesh-control*.log"))
            {
                var target = Path.Combine(logFolder,
                    Path.GetFileName(file).Replace("mesh-control", "mesh-control"));

                if (!File.Exists(target)) File.Move(file, target);
            }
        }
        catch { /* le nom d'un journal n'a aucune conséquence fonctionnelle */ }
    }
}
