using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace MeshControl.Core;

/// <summary>
/// Vérifie, télécharge et applique les mises à jour.
///
/// Toute la mécanique revient à Velopack : comparaison des versions,
/// téléchargement des seules différences, remplacement du dossier d'application
/// et retour à la version précédente si le remplacement échoue. Ne reste ici que
/// la politique — quand proposer une mise à jour, et quoi en montrer.
///
/// Les binaires sont publiés sur un dépôt public, distinct du dépôt privé qui
/// héberge le code source : aucune authentification n'est nécessaire, et plus
/// aucun jeton n'est compilé dans l'application.
/// </summary>
public static class UpdateService
{
    private const string RepositoryUrl = "https://github.com/dch-builds/mesh-control-releases";

    // Construit à la demande. Hors installation — pendant le développement,
    // typiquement — l'objet reste inutilisé plutôt que de peser au démarrage.
    private static readonly Lazy<UpdateManager> Manager = new(() =>
        new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false)));

    /// <summary>Version en cours d'exécution, lue sur l'assembly.</summary>
    public static Version CurrentVersion
    {
        get
        {
            var raw = Assembly.GetExecutingAssembly().GetName().Version;
            // Normalise en 3 composantes : l'assembly porte « 1.2.0.0 » alors que
            // les versions publiées s'écrivent « 1.2.0 ».
            return raw is null ? new Version(0, 0, 0)
                               : new Version(raw.Major, raw.Minor, raw.Build);
        }
    }

    /// <summary>
    /// Faux lorsque l'application ne tourne pas depuis une installation Velopack
    /// — depuis le dossier de compilation, ou en version portable. Aucune mise à
    /// jour n'est alors possible et il ne faut rien proposer.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            try { return Manager.Value.IsInstalled; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Interroge le dépôt de distribution. Renvoie null s'il n'y a rien de plus
    /// récent, si l'application n'est pas installée, ou en cas d'échec réseau :
    /// une mise à jour indisponible ne doit jamais gêner l'utilisation.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        if (!IsAvailable) return null;

        try
        {
            var update = await Manager.Value.CheckForUpdatesAsync().ConfigureAwait(false);

            if (update is not null)
                Log.Info($"Version {TargetVersion(update)} disponible "
                       + $"(actuelle : {CurrentVersion})", "Mise à jour");

            return update;
        }
        catch (Exception ex)
        {
            // Pas de réseau, dépôt injoignable : sans conséquence.
            Log.Warning($"Vérification des mises à jour impossible : {ex.Message}", "Mise à jour");
            return null;
        }
    }

    /// <summary>
    /// Télécharge la mise à jour. <paramref name="progress"/> reçoit une fraction
    /// entre 0 et 1.
    /// </summary>
    public static Task DownloadAsync(UpdateInfo update,
                                     IProgress<double>? progress = null,
                                     CancellationToken ct = default)
    {
        // Velopack rend un pourcentage entier ; la barre de l'application
        // raisonne en fraction.
        Action<int>? relay = progress is null
            ? null
            : percent => progress.Report(percent / 100.0);

        return Manager.Value.DownloadUpdatesAsync(update, relay, ct);
    }

    /// <summary>
    /// Applique la mise à jour et relance l'application. Ne rend pas la main :
    /// le processus est remplacé. Il n'y a donc rien à fermer soi-même, à la
    /// différence de l'ancien installeur.
    /// </summary>
    public static void ApplyAndRestart(UpdateInfo update)
    {
        Log.Info($"Installation de la version {TargetVersion(update)}.", "Mise à jour");
        Manager.Value.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }

    /// <summary>Version proposée, telle qu'on l'affiche et qu'on la mémorise.</summary>
    public static string TargetVersion(UpdateInfo update)
        => update.TargetFullRelease.Version.ToString();

    /// <summary>
    /// Volume à télécharger, en octets.
    ///
    /// Un paquet delta ne contient que ce qui a changé d'une version à l'autre :
    /// annoncer la taille du paquet complet donnerait une idée très fausse de
    /// l'attente — quelques centaines de kilooctets présentés comme plusieurs
    /// dizaines de mégaoctets. Velopack peut toutefois décider de reprendre le
    /// paquet complet s'il y a trop de deltas à enchaîner, auquel cas le
    /// téléchargement dure plus longtemps qu'annoncé.
    /// </summary>
    public static long DownloadSize(UpdateInfo update)
        => update.DeltasToTarget is { Length: > 0 } deltas
            ? deltas.Sum(d => d.Size)
            : update.TargetFullRelease.Size;

    /// <summary>Notes de version au format Markdown, si la release en porte.</summary>
    public static string? Notes(UpdateInfo update)
        => update.TargetFullRelease.NotesMarkdown;
}
