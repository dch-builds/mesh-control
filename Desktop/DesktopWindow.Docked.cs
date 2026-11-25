using System.Windows;

namespace MeshControl.Desktop;

/// <summary>
/// Bureau à distance ancré dans une autre session (voir DesktopDock) : une
/// vignette posée en bas à droite d'un terminal, pour suivre l'écran pendant
/// qu'on tape des commandes.
///
/// Même principe que le mode onglets : la fenêtre n'est jamais affichée, son
/// contenu est logé ailleurs. Dans une vignette, la barre d'outils et la
/// barre d'état n'ont pas la place : elles sont masquées, leurs raccourcis
/// (Ctrl+Maj+…) restant actifs. Le plein écran, lui, n'aurait pas de sens
/// pour une vignette : Ctrl+Maj+F la sort du terminal (UndockRequested).
/// </summary>
public partial class DesktopWindow
{
    private bool _docked;

    /// <summary>Ajustement choisi avant l'ancrage, rétabli à sa sortie.</summary>
    private bool? _fitBeforeDock;

    /// <summary>Vrai tant que le bureau est ancré dans une autre session.</summary>
    public bool IsDocked => _docked;

    /// <summary>Nom de la machine affichée.</summary>
    public string DeviceName => _deviceName;

    /// <summary>État de la connexion, pour l'en-tête de la vignette.</summary>
    public DesktopSessionState SessionState => _session.State;

    /// <summary>Motif de fermeture de la session, s'il est connu.</summary>
    public string? SessionCloseReason => _session.CloseReason;

    /// <summary>
    /// L'état de la connexion a changé. Distinct de Window.StateChanged, qui
    /// porte sur l'état de la fenêtre (réduite, agrandie).
    /// </summary>
    public event Action? SessionStateChanged
    {
        add => _session.StateChanged += value;
        remove => _session.StateChanged -= value;
    }

    /// <summary>
    /// Demande, depuis la vignette, de passer en grand (Ctrl+Maj+F) : à
    /// l'hôte de la sortir du terminal pour l'ouvrir en onglet ou en fenêtre.
    /// </summary>
    public event Action? UndockRequested;

    /// <summary>
    /// Passe en vignette, ou en sort. À appeler AVANT de loger le contenu
    /// ailleurs : le premier affichage ne doit ni agrandir ni mettre en plein
    /// écran la fenêtre hôte, qui n'est pas la sienne.
    /// </summary>
    public void SetDocked(bool docked)
    {
        if (_docked == docked) return;
        _docked = docked;

        var visibility = docked ? Visibility.Collapsed : Visibility.Visible;
        Toolbar.Visibility = visibility;
        StatusBar.Visibility = visibility;

        // Une vignette n'a de sens qu'avec l'écran entier, réduit à sa taille.
        if (docked)
        {
            _fitBeforeDock = FitCheck.IsChecked;
            FitCheck.IsChecked = true;
        }
        else if (_fitBeforeDock is { } fit)
        {
            FitCheck.IsChecked = fit;
            _fitBeforeDock = null;
        }
    }

    /// <summary>
    /// Rouvre la session après une coupure : la barre d'outils, et donc son
    /// bouton « Reconnecter », est masquée dans la vignette.
    /// </summary>
    public void Reconnect() => Reconnect_Click(this, new RoutedEventArgs());

    /// <summary>Donne le clavier à la machine distante.</summary>
    public void FocusRemote() => RootPanel.Focus();
}
