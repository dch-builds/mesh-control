using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MeshControl.Core;

namespace MeshControl.Desktop;

/// <summary>
/// Vue seule et barre flottante du plein écran.
/// </summary>
public partial class DesktopWindow
{
    // MARK: - Vue seule

    /// <summary>
    /// Vue seule : souris, clavier, collages et raccourcis ne partent plus vers
    /// la machine. L'image, la capture, la loupe, le plein écran et l'envoi de
    /// fichiers par glisser-déposer (geste délibéré) restent disponibles.
    /// </summary>
    private bool _viewOnly;

    /// <summary>Un collage par le presse-papiers distant est en cours.</summary>
    private bool _pasteBusy;

    private void ViewOnly_Click(object sender, RoutedEventArgs e)
    {
        SetViewOnly(!_viewOnly);
        RootPanel.Focus();
    }

    private void SetViewOnly(bool on)
    {
        if (on == _viewOnly) return;
        _viewOnly = on;

        if (on)
        {
            // Rien ne doit rester « enfoncé » côté distant au moment où l'on
            // cesse d'y envoyer quoi que ce soit, ni une saisie continuer.
            _pasteCts?.Cancel();
            _session.ReleaseModifiers();
            _session.ReleaseMouseButtons();
            Mouse.Capture(null);
        }

        ViewOnlyBadge.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SetToggleLook(ViewOnlyButton, BarViewOnlyButton, on);
        ViewOnlyButton.ToolTip = on
            ? "Reprendre le contrôle de la machine (Ctrl+Maj+O)"
            : "Vue seule : observer sans envoyer souris ni clavier (Ctrl+Maj+O)";
        BarViewOnlyButton.ToolTip = on
            ? "Reprendre le contrôle (Ctrl+Maj+O)"
            : "Vue seule (Ctrl+Maj+O)";

        UpdateInputControls();
        StatsText.Text = on
            ? "Vue seule : souris et clavier ne sont plus transmis."
            : "Contrôle repris.";
        Log.Info($"{_node.Name} : vue seule {(on ? "activée" : "désactivée")}.", "Bureau");
    }

    /// <summary>
    /// Vrai (et message à l'appui) si l'action doit être refusée parce que la
    /// session est en vue seule.
    /// </summary>
    private bool RejectedByViewOnly()
    {
        if (!_viewOnly) return false;
        StatsText.Text = "Vue seule — Ctrl+Maj+O pour reprendre le contrôle.";
        return true;
    }

    /// <summary>
    /// État des commandes qui agissent sur la machine, dans la barre d'outils
    /// et dans la barre flottante : grisées en vue seule ou pendant un collage.
    /// </summary>
    private void UpdateInputControls()
    {
        if (!_uiReady) return;

        var canSend = !_viewOnly;
        PasteButton.IsEnabled = BarPasteButton.IsEnabled = canSend && !_typing && !_pasteBusy;
        // « Arrêter » doit rester cliquable pendant une saisie.
        PasteTypingButton.IsEnabled = BarTypingButton.IsEnabled = _typing || canSend;
        ShortcutsButton.IsEnabled = BarShortcutsButton.IsEnabled = canSend;
        BarCtrlAltDelButton.IsEnabled = canSend;
    }

    private void CtrlAltDel_Click(object sender, RoutedEventArgs e) => SendCtrlAltDel();

    private void SendCtrlAltDel()
    {
        if (RejectedByViewOnly()) return;
        _session.SendCtrlAltDel();
        StatsText.Text = "Envoyé : Ctrl+Alt+Suppr";
    }

    // MARK: - Barre flottante du plein écran

    /// <summary>
    /// Masque la barre quand la souris l'a quittée (ou ne l'a jamais atteinte)
    /// depuis ce délai.
    /// </summary>
    private readonly DispatcherTimer _fullscreenBarHideTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(900),
    };

    /// <summary>Hauteur, en pixels depuis le haut, de la bande qui fait apparaître la barre.</summary>
    private const double FullscreenBarTriggerHeight = 3;

    private void InitializeFullscreenBar()
    {
        _fullscreenBarHideTimer.Tick += (_, _) =>
        {
            _fullscreenBarHideTimer.Stop();
            TryHideFullscreenBar();
        };

        // Le menu des raccourcis s'ouvre hors de la barre : la souris la quitte
        // pour l'atteindre. La barre reste donc affichée tant qu'il est ouvert.
        ShortcutsMenu.Closed += (_, _) =>
        {
            if (FullscreenBar.Visibility == Visibility.Visible) RestartFullscreenBarHideTimer();
        };

        Closed += (_, _) => _fullscreenBarHideTimer.Stop();
    }

    private void DisplayArea_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_fullscreen.IsActive || FullscreenBar.Visibility == Visibility.Visible) return;
        // Pas pendant un glisser sur l'écran distant : la barre s'ouvrirait
        // sous le curseur d'une sélection qui remonte vers le haut.
        if (AnyMouseButtonPressed) return;

        if (e.GetPosition(DisplayArea).Y <= FullscreenBarTriggerHeight) ShowFullscreenBar();
    }

    private void ShowFullscreenBar()
    {
        FullscreenBarTitle.Text = _deviceName;
        FullscreenBarDot.Fill = StatusDot.Fill;
        // Le rappel d'entrée en plein écran occupe la même place.
        FullscreenHint.Visibility = Visibility.Collapsed;

        FullscreenBar.Visibility = Visibility.Visible;
        FullscreenBar.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));

        // Si la souris n'y entre pas (bord touché dans un coin), elle repart.
        RestartFullscreenBarHideTimer();
    }

    private void TryHideFullscreenBar()
    {
        if (FullscreenBar.IsMouseOver || ShortcutsMenu.IsOpen) return;
        HideFullscreenBar();
    }

    private void HideFullscreenBar()
    {
        _fullscreenBarHideTimer.Stop();
        FullscreenBar.BeginAnimation(OpacityProperty, null);
        FullscreenBar.Visibility = Visibility.Collapsed;
    }

    private void RestartFullscreenBarHideTimer()
    {
        _fullscreenBarHideTimer.Stop();
        _fullscreenBarHideTimer.Start();
    }

    private void FullscreenBar_MouseEnter(object sender, MouseEventArgs e) =>
        _fullscreenBarHideTimer.Stop();

    private void FullscreenBar_MouseLeave(object sender, MouseEventArgs e) =>
        RestartFullscreenBarHideTimer();
}
