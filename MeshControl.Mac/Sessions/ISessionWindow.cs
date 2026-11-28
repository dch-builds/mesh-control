using Avalonia.Controls;

namespace MeshControl.Mac;

/// <summary>
/// Fenêtre de session (bureau, terminal, fichiers) affichable dans un onglet
/// de la fenêtre commune (portage de l'interface du même nom, version Windows).
/// </summary>
public interface ISessionWindow
{
    /// <summary>Démarre la session : la fenêtre porteuse, jamais affichée, ne reçoit pas Opened.</summary>
    Task StartSessionAsync();

    /// <summary>Relâche les touches maintenues côté distant (changement d'onglet, perte du focus).</summary>
    void ReleaseKeyboard();

    /// <summary>Barre de titre de la session : sous les onglets, elle ne garde que sa barre d'outils.</summary>
    TitleBar SessionTitleBar { get; }

    /// <summary>Contrôle qui reçoit le focus à l'affichage de l'onglet.</summary>
    Control FocusTarget { get; }
}
