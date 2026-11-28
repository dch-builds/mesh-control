using Avalonia.Controls;
using MeshControl.Desktop;

namespace MeshControl.Mac;

/// <summary>
/// Bureau à distance ancré dans un terminal (voir DesktopDock ; portage de
/// Desktop/DesktopWindow.Docked.cs). Même principe que le mode onglets : la
/// fenêtre n'est jamais affichée, son contenu est logé ailleurs. Dans une
/// vignette, la barre d'outils et la barre d'état n'ont pas la place : elles
/// sont masquées, leurs raccourcis (Ctrl+Maj+…) restant actifs. Le plein
/// écran n'aurait pas de sens : Ctrl+Maj+F sort la vignette du terminal.
/// </summary>
public partial class DesktopWindow
{
    private bool _docked;

    /// <summary>Ajustement choisi avant l'ancrage, rétabli à sa sortie.</summary>
    private bool? _fitBeforeDock;

    /// <summary>Vrai tant que le bureau est ancré dans une autre session.</summary>
    public bool IsDocked => _docked;

    /// <summary>Nom de la machine affichée.</summary>
    public string DeviceName => _node.Name;

    /// <summary>État de la connexion, pour l'en-tête de la vignette.</summary>
    public DesktopSessionState SessionState => _session.State;

    /// <summary>Motif de fermeture de la session, s'il est connu.</summary>
    public string? SessionCloseReason => _session.CloseReason;

    /// <summary>L'état de la connexion a changé.</summary>
    public event Action? SessionStateChanged
    {
        add => _session.StateChanged += value;
        remove => _session.StateChanged -= value;
    }

    /// <summary>Demande, depuis la vignette, de passer en grand (Ctrl+Maj+F).</summary>
    public event Action? UndockRequested;

    /// <summary>
    /// Passe en vignette, ou en sort. À appeler AVANT de loger le contenu
    /// ailleurs. <paramref name="host"/> : la fenêtre du terminal, propriétaire
    /// des boîtes de dialogue et du presse-papiers tant que dure l'ancrage.
    /// </summary>
    public void SetDocked(bool docked, Window? host)
    {
        if (_docked == docked) return;
        _docked = docked;
        SetHost(docked ? host : null);

        WindowTitleBar.IsVisible = !docked;
        StatusBar.IsVisible = !docked;

        // Une vignette n'a de sens qu'avec l'écran entier, réduit à sa taille.
        if (docked)
        {
            _fitBeforeDock = FitCheck.IsChecked;
            FitCheck.IsChecked = true;
            HideFullscreenBar();
            HideFullscreenHint();
        }
        else if (_fitBeforeDock is { } fit)
        {
            FitCheck.IsChecked = fit;
            _fitBeforeDock = null;
        }
    }

    /// <summary>Rouvre la session après une coupure (le bouton « Reconnecter » est masqué dans la vignette).</summary>
    public void Reconnect() => _session.RetryNow();

    /// <summary>Donne le clavier à la machine distante.</summary>
    public void FocusRemote() => ScreenHost.Focus();
}
