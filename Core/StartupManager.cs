using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using Velopack.Locators;

namespace MeshControl.Core;

/// <summary>
/// Démarrage automatique avec Windows, via une TÂCHE PLANIFIÉE différée.
///
/// Pourquoi pas la clé de registre Run : elle lance l'application au moment
/// exact où Windows ouvre la session, donc en pleine concurrence d'accès disque
/// avec les services et les autres applications de démarrage. Or l'application
/// lit une centaine de mégaoctets à son lancement (runtime .NET et WPF) : dans
/// cette bousculade, elle met plusieurs fois plus de temps à apparaître, et
/// ralentit l'ouverture de session au passage.
///
/// Une tâche planifiée avec un court délai laisse le système finir son
/// démarrage. L'icône apparaît quelques secondes plus tard, mais l'ouverture de
/// session est plus rapide et l'application se charge sans concurrence.
///
/// La tâche est créée dans le contexte de l'utilisateur courant : aucun droit
/// administrateur n'est requis.
/// </summary>
public static class StartupManager
{
    private const string TaskName = "Mesh Control - Demarrage";

    // Ancienne méthode, conservée pour pouvoir la nettoyer lors de la migration.
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Mesh Control";

    /// <summary>
    /// Délai après l'ouverture de session, en secondes. Assez court pour que
    /// l'icône ne se fasse pas attendre, assez long pour laisser passer le gros
    /// des accès disque du démarrage.
    /// </summary>
    private const int DelaySeconds = 20;

    /// <summary>
    /// Chemin de l'exécutable, tel qu'inscrit au démarrage.
    ///
    /// Sous Velopack, le processus en cours tourne depuis le dossier des fichiers
    /// versionnés, que chaque mise à jour remplace en bloc. On inscrit donc le
    /// stub posé à la racine de l'installation : il ne bouge jamais, et c'est
    /// précisément le chemin que Velopack destine aux raccourcis et aux
    /// lanceurs. Hors installation — exécution depuis le dossier de compilation,
    /// ou version portable, qui n'a pas de stub — on retombe sur le processus
    /// courant.
    /// </summary>
    private static string? ExecutablePath
    {
        get
        {
            // Environment.ProcessPath vise le vrai .exe (et non la dll).
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return null;

            try
            {
                if (VelopackLocator.IsCurrentSet && !VelopackLocator.Current.IsPortable)
                {
                    var root = VelopackLocator.Current.RootAppDir;
                    if (!string.IsNullOrEmpty(root))
                    {
                        var stub = Path.Combine(root, Path.GetFileName(path));
                        if (File.Exists(stub)) return stub;
                    }
                }
            }
            catch (Exception ex)
            {
                // Sans conséquence : le chemin du processus reste valable tant
                // que l'application n'est pas mise à jour.
                Log.Debug($"Stub Velopack introuvable : {ex.Message}", "Démarrage");
            }

            return path;
        }
    }

    /// <summary>Vrai si l'application est inscrite au démarrage de Windows.</summary>
    public static bool IsEnabled()
    {
        // La tâche planifiée fait foi ; la clé de registre n'est consultée que
        // pour les installations antérieures pas encore migrées.
        return TaskExists() || RunKeyExists();
    }

    /// <summary>
    /// Active ou désactive le démarrage automatique. Renvoie false si l'opération
    /// a échoué (politique de sécurité, service de planification désactivé…).
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            // Retirer les deux formes : la tâche et l'éventuel reliquat de
            // registre d'une version précédente.
            bool taskRemoved = DeleteTask();
            RemoveRunKey();
            return taskRemoved || !TaskExists();
        }

        var exe = ExecutablePath;
        if (exe is null) return false;

        if (!CreateTask(exe)) return false;

        // La tâche a pris le relais : la clé de registre ferait un double
        // lancement.
        RemoveRunKey();
        return true;
    }

    // MARK: - Tâche planifiée

    private static bool TaskExists() => RunSchTasks($"/query /tn \"{TaskName}\"") == 0;

    private static bool DeleteTask()
    {
        if (!TaskExists()) return true;
        return RunSchTasks($"/delete /tn \"{TaskName}\" /f") == 0;
    }

    /// <summary>
    /// Crée (ou remplace) la tâche à partir d'une définition XML. Passer par un
    /// fichier XML plutôt que par les options en ligne de commande est le seul
    /// moyen de fixer le délai après ouverture de session.
    /// </summary>
    private static bool CreateTask(string exePath)
    {
        string? xmlPath = null;
        try
        {
            xmlPath = Path.Combine(Path.GetTempPath(),
                                   $"mesh-control-startup-{Guid.NewGuid():N}.xml");

            // schtasks exige de l'UTF-16 avec indicateur d'ordre des octets :
            // en UTF-8, il rejette le fichier avec une erreur peu explicite.
            File.WriteAllText(xmlPath, BuildTaskXml(exePath), new UnicodeEncoding(false, true));

            return RunSchTasks($"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f") == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (xmlPath is not null)
                try { File.Delete(xmlPath); } catch { /* fichier temporaire */ }
        }
    }

    private static string BuildTaskXml(string exePath)
    {
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var delay = $"PT{DelaySeconds}S";

        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Lance Mesh Control dans la zone de notification, peu apres l'ouverture de session.</Description>
    <URI>\{TaskName}</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{Escape(user)}</UserId>
      <Delay>{delay}</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{Escape(user)}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <!-- Sur portable, l'application doit demarrer aussi sur batterie. -->
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <!-- Duree illimitee : l'application reste ouverte toute la session. -->
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{Escape(exePath)}</Command>
      <Arguments>--minimized</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Exécute schtasks sans afficher de fenêtre. Renvoie le code de sortie.</summary>
    private static int RunSchTasks(string arguments)
    {
        try
        {
            var info = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(info);
            if (process is null) return -1;

            // Lire les flux avant d'attendre : un tampon plein bloquerait le
            // processus fils.
            process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(15000)) return -1;

            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
                Log.Debug($"schtasks : {error.Trim()}", "Démarrage");

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Warning($"Planification indisponible : {ex.Message}", "Démarrage");
            return -1;
        }
    }

    // MARK: - Ancienne méthode (registre), pour la migration

    private static bool RunKeyExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) is string value && !string.IsNullOrEmpty(value);
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { /* clé absente ou non modifiable : sans conséquence */ }
    }
}
