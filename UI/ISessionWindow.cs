namespace MeshControl.UI;

/// <summary>
/// Fenêtre de session pouvant être hébergée dans un onglet.
///
/// En mode onglets, la fenêtre porteuse n'est jamais affichée : son événement
/// Loaded ne se déclenche pas. Le démarrage doit donc être déclenché
/// explicitement par l'hôte, d'où cette interface.
/// </summary>
public interface ISessionWindow
{
    /// <summary>
    /// Démarre la session. Idempotent : un second appel est sans effet, ce qui
    /// permet de l'invoquer aussi bien depuis Loaded que depuis l'hôte.
    /// </summary>
    Task StartSessionAsync();

    /// <summary>
    /// Relâche les touches maintenues sur la machine distante.
    ///
    /// Appelé par la fenêtre hôte lorsqu'elle intercepte un raccourci : les
    /// modificateurs déjà transmis « enfoncés » ne recevraient jamais leur
    /// relâchement, et le clavier distant resterait bloqué. Sans effet par
    /// défaut — seules les sessions qui transmettent le clavier ont ce ménage
    /// à faire.
    /// </summary>
    void ReleaseKeyboard() { }
}
