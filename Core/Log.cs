using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace MeshControl.Core;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>
/// Journal de l'application, écrit dans un fichier sous %LOCALAPPDATA%.
///
/// Objectif : quand un utilisateur signale un problème, disposer d'une trace
/// plutôt que de deviner. L'écriture est asynchrone (jamais bloquante pour
/// l'interface) et le fichier est renouvelé par taille afin de ne pas grossir
/// indéfiniment.
/// </summary>
public static class Log
{
    private const long MaxFileBytes = 2 * 1024 * 1024;   // 2 Mo par fichier
    private const int KeepFiles = 3;                      // + les 3 précédents

    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());
    private static readonly Lazy<string> DirectoryPath = new(() =>
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mesh Control", "logs");
        Directory.CreateDirectory(path);
        return path;
    });

    /// <summary>Dossier contenant les journaux (exposé dans les réglages).</summary>
    public static string Folder => DirectoryPath.Value;

    private static string CurrentFile => Path.Combine(Folder, "mesh-control.log");

    /// <summary>Niveau minimal enregistré. Debug est bruyant : réservé au diagnostic.</summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    static Log()
    {
        // Un seul thread écrit : pas de contention sur le fichier, et l'appelant
        // n'attend jamais l'entrée/sortie disque.
        var writer = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "Mesh Control log writer",
        };
        writer.Start();
    }

    public static void Debug(string message, string? category = null)
        => Write(LogLevel.Debug, message, category);

    public static void Info(string message, string? category = null)
        => Write(LogLevel.Info, message, category);

    public static void Warning(string message, string? category = null)
        => Write(LogLevel.Warning, message, category);

    public static void Error(string message, string? category = null)
        => Write(LogLevel.Error, message, category);

    /// <summary>Journalise une exception avec son type et sa pile d'appels.</summary>
    public static void Error(string message, Exception exception, string? category = null)
        => Write(LogLevel.Error,
                 $"{message} — {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}",
                 category);

    private static void Write(LogLevel level, string message, string? category)
    {
        if (level < MinimumLevel) return;

        var prefix = level switch
        {
            LogLevel.Debug => "DEBUG",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "INFO ",
        };
        var scope = string.IsNullOrEmpty(category) ? "" : $"[{category}] ";
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {prefix} {scope}{message}";

        // TryAdd : si la file est fermée (arrêt de l'application), on abandonne
        // silencieusement plutôt que de lever une exception depuis un logger.
        try { Queue.TryAdd(line); } catch { }
    }

    private static void ProcessQueue()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Un journal qui échoue ne doit jamais perturber l'application :
                // disque plein, fichier verrouillé, droits insuffisants…
            }
        }
    }

    private static void RotateIfNeeded()
    {
        var file = new FileInfo(CurrentFile);
        if (!file.Exists || file.Length < MaxFileBytes) return;

        // Décale les fichiers : .2 -> .3, .1 -> .2, courant -> .1
        for (int i = KeepFiles; i >= 1; i--)
        {
            var older = Path.Combine(Folder, $"mesh-control.{i}.log");
            var newer = i == 1 ? CurrentFile : Path.Combine(Folder, $"mesh-control.{i - 1}.log");
            try
            {
                if (i == KeepFiles && File.Exists(older)) File.Delete(older);
                if (File.Exists(newer)) File.Move(newer, older, overwrite: true);
            }
            catch { /* rotation impossible : on continue d'écrire dans le fichier courant */ }
        }
    }

    /// <summary>Écrit un en-tête de session : utile pour situer les erreurs.</summary>
    public static void StartSession(string version)
    {
        Info(new string('─', 60));
        Info($"Mesh Control {version} — démarrage");
        Info($"Windows {Environment.OSVersion.Version} · .NET {Environment.Version} · "
           + $"{Environment.UserName}@{Environment.MachineName}");
    }
}
