using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>Vue seule et apparence des interrupteurs (portage de Desktop/DesktopWindow.ViewOnly.cs).</summary>
public partial class DesktopWindow
{
    /// <summary>
    /// Vue seule : souris, clavier, collages et raccourcis ne partent plus vers
    /// la machine. L'image, la capture, la loupe et le plein écran restent disponibles.
    /// </summary>
    private bool _viewOnly;

    /// <summary>Un collage par le presse-papiers distant est en cours.</summary>
    private bool _pasteBusy;

    private void ViewOnly_Click(object? sender, RoutedEventArgs e)
    {
        SetViewOnly(!_viewOnly);
        ScreenHost.Focus();
    }

    private void SetViewOnly(bool on)
    {
        if (on == _viewOnly) return;
        _viewOnly = on;

        if (on)
        {
            // Rien ne doit rester « enfoncé » côté distant au moment où l'on
            // cesse d'y envoyer quoi que ce soit, ni une saisie continuer.
            _typingCts?.Cancel();
            ReleaseAllKeys();
            if (_buttonsDown.Count > 0)
            {
                _buttonsDown.Clear();
                _session.ReleaseMouseButtons();
            }
        }

        ViewOnlyBadge.IsVisible = on;
        SetToggleLook(ViewOnlyButton, BarViewOnlyButton, on);
        ToolTip.SetTip(ViewOnlyButton, on
            ? "Reprendre le contrôle de la machine (Ctrl+Maj+O)"
            : "Vue seule : observer sans envoyer souris ni clavier (Ctrl+Maj+O)");
        ToolTip.SetTip(BarViewOnlyButton, on ? "Reprendre le contrôle (Ctrl+Maj+O)" : "Vue seule (Ctrl+Maj+O)");

        UpdateInputControls();
        ShowStatus(on ? "Vue seule : souris et clavier ne sont plus transmis." : "Contrôle repris.");
        Log.Info($"{_node.Name} : vue seule {(on ? "activée" : "désactivée")}.", "Bureau");
    }

    /// <summary>
    /// Vrai (et message à l'appui) si l'action doit être refusée parce que la
    /// session est en vue seule.
    /// </summary>
    private bool RejectedByViewOnly()
    {
        if (!_viewOnly) return false;
        ShowStatus("Vue seule — Ctrl+Maj+O pour reprendre le contrôle.");
        return true;
    }

    /// <summary>
    /// État des commandes qui agissent sur la machine, dans la barre d'outils
    /// et dans la barre flottante : grisées en vue seule ou pendant un collage.
    /// </summary>
    private void UpdateInputControls()
    {
        if (!_ready) return;
        var canSend = !_viewOnly;
        PasteButton.IsEnabled = BarPasteButton.IsEnabled = canSend && !Typing && !_pasteBusy;
        // « Arrêter » doit rester cliquable pendant une saisie.
        PasteTypingButton.IsEnabled = BarTypingButton.IsEnabled = Typing || canSend;
        ShortcutsButton.IsEnabled = BarShortcutsButton.IsEnabled = canSend;
        BarCtrlAltDelButton.IsEnabled = canSend;
    }

    private void SendCtrlAltDel()
    {
        if (RejectedByViewOnly()) return;
        _session.SendCtrlAltDel();
        ShowStatus("Envoyé : Ctrl+Alt+Suppr");
    }

    // MARK: - Apparence des interrupteurs

    /// <summary>Fond d'un interrupteur actif sur la barre sombre du plein écran.</summary>
    private static readonly IBrush ActiveOverlayBrush = new SolidColorBrush(Color.FromArgb(0x59, 0x0A, 0x84, 0xFF));

    /// <summary>
    /// Met en évidence un interrupteur actif, dans la barre d'outils et dans
    /// la barre flottante du plein écran.
    /// </summary>
    /// <param name="toolbarOffBackground">
    /// Fond au repos du bouton de barre d'outils, quand il diffère de celui
    /// du style (bouton intégré à un bloc segmenté) ; null pour le style.
    /// </param>
    private static void SetToggleLook(Button toolbarButton, Button barButton, bool on, IBrush? toolbarOffBackground = null)
    {
        if (on)
        {
            toolbarButton.Background = Palette.Resource("AccentSoftBrush");
            toolbarButton.Foreground = Palette.Resource("AccentBrush");
            barButton.Background = ActiveOverlayBrush;
        }
        else
        {
            if (toolbarOffBackground is not null) toolbarButton.Background = toolbarOffBackground;
            else toolbarButton.ClearValue(BackgroundProperty);
            toolbarButton.ClearValue(ForegroundProperty);
            barButton.ClearValue(BackgroundProperty);
        }
    }
}
