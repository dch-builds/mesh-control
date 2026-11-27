namespace MeshControl.Core;

/// <summary>
/// Accès au thread de l'interface, indépendant du socle graphique.
///
/// Les sessions (bureau, fichiers, terminal) reçoivent leurs trames sur des
/// fils de fond et doivent prévenir l'interface sur son propre thread. Elles
/// passaient jusqu'ici par le Dispatcher de WPF, ce qui les liait à Windows ;
/// cette interface les rend utilisables par la version macOS (Avalonia), qui
/// fournit sa propre implémentation.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// Programme <paramref name="action"/> sur le thread de l'interface, sans
    /// attendre son exécution.
    ///
    /// <paramref name="onAborted"/> est appelé si l'action ne s'exécutera
    /// jamais (arrêt du thread d'interface à la fermeture) : l'appelant peut y
    /// libérer ce que l'action devait consommer. Une implémentation qui ne sait
    /// pas le détecter peut l'ignorer.
    /// </summary>
    void Post(Action action, Action? onAborted = null);
}
