using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Velopack;
using MeshControl.Core;

namespace MeshControl;

public partial class App : Application
{
    /// <summary>
    /// Point d'entrée, écrit à la main plutôt que généré par WPF (voir
    /// StartupObject dans le .csproj).
    ///
    /// Un lancement de l'exécutable n'est pas toujours un démarrage
    /// d'application : Velopack le réutilise pour ses propres besoins — appliquer
    /// une mise à jour, exécuter un hook d'installation ou de désinstallation — et
    /// Run() termine alors le processus sans revenir ici. Amorcer WPF avant lui
    /// ferait payer le chargement du framework à chacune de ces opérations, dont
    /// certaines sont minutées par l'updater.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        VelopackApp.Build().Run();

        // Avant tout le reste : la reprise déplace les dossiers de données, et
        // Log comme AppSettings créent le leur dès le premier appel. Un dossier
        // créé trop tôt, même vide, bloquerait le déplacement.
        LegacyMigration.Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>Version affichée et journalisée.</summary>
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // L'application vit dans la zone de notification : elle ne doit pas
        // s'arrêter quand la fenêtre principale est masquée, ni quand on ferme la
        // dernière fenêtre de bureau à distance. Seul Shutdown() explicite quitte.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Log.StartSession(Version);

        // Le compte rendu de la reprise attendait d'avoir un journal où aller.
        LegacyMigration.WriteReport();

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
            // Les enregistrements de réglages sont différés : forcer l'écriture,
            // sinon les dernières modifications seraient perdues à l'arrêt.
            AppSettings.FlushToDisk();
            Log.Info("Arrêt de l'application");
        };

        // Pas de StartupUri : elle afficherait la fenêtre d'office, alors qu'un
        // démarrage réduit ne doit jamais la montrer (voir MainWindow.Start).
        var main = new UI.MainWindow();
        MainWindow = main;
        main.Start();
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
