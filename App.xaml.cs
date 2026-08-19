using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using MeshControl.Core;

namespace MeshControl;

public partial class App : Application
{
    /// <summary>Version affichée et journalisée.</summary>
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// Mutex nomme, conservé pour toute la durée de vie du processus. L'installeur
    /// Inno l'observe (directive AppMutex) pour savoir que l'application tourne et
    /// attendre sa fermeture avant de remplacer les fichiers — indispensable à la
    /// mise à jour silencieuse.
    /// </summary>
    private System.Threading.Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // createdNew inutilisé : on ne bloque pas un second lancement, le mutex
        // ne sert qu'à signaler notre présence à l'installeur.
        try
        {
            _instanceMutex = new System.Threading.Mutex(
                initiallyOwned: false, name: "MeshControl.SingleInstance");
        }
        catch { /* nom déjà pris ou restriction : sans conséquence ici */ }

        // L'application vit dans la zone de notification : elle ne doit pas
        // s'arrêter quand la fenêtre principale est masquée, ni quand on ferme la
        // dernière fenêtre de bureau à distance. Seul Shutdown() explicite quitte.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Log.StartSession(Version);

        // Les erreurs sont journalisées intégralement (type, message, pile) mais
        // présentées simplement : une pile d'appels brute n'aide pas un
        // utilisateur, et l'app doit rester utilisable après l'incident.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Exception non gérée (thread UI)", args.Exception);
            ShowFriendlyError(args.Exception);
            args.Handled = true;   // empêche la fermeture de l'application
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Error("Exception non gérée (thread de fond)", ex);
            else
                Log.Error($"Exception non gérée non typée : {args.ExceptionObject}");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Log.Error("Exception de tâche non observée", args.Exception);
        };

        Exit += (_, _) =>
        {
            Log.Info("Arrêt de l'application");
            // Libérer le mutex : l'installeur attend sa disparition pour agir.
            try { _instanceMutex?.Dispose(); } catch { }
        };
    }

    /// <summary>
    /// Message d'erreur compréhensible, avec renvoi vers le journal pour le
    /// détail technique. Évite d'afficher une pile d'appels à l'utilisateur.
    /// </summary>
    private static void ShowFriendlyError(Exception exception)
    {
        var text = $"Une erreur inattendue s'est produite :\n\n{exception.Message}\n\n"
                 + "L'application reste utilisable. Le détail technique a été enregistré dans :\n"
                 + Log.Folder;

        MessageBox.Show(text, "Mesh Control", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
