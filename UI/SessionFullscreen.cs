using System.Windows;
using System.Windows.Threading;

namespace MeshControl.UI;

/// <summary>
/// Plein écran d'une session : masque ce qui entoure le contenu et, en mode
/// onglets, le bandeau de la fenêtre hôte.
///
/// Le plein écran appartient à la FENÊTRE et non à la session. En mode onglets,
/// plusieurs sessions partagent la même fenêtre hôte : une autre session a pu
/// l'y mettre — ou l'en sortir — pendant que celle-ci était en arrière-plan,
/// d'où <see cref="Sync"/>, à appeler lorsqu'elle redevient visible.
/// </summary>
public sealed class SessionFullscreen
{
    private readonly FrameworkElement _root;
    private readonly UIElement? _hint;
    private readonly UIElement[] _chrome;

    /// <summary>
    /// Fenêtre que cette session a passée en plein écran, s'il y en a une. En
    /// mode onglets, c'est la fenêtre hôte et non la fenêtre porteuse.
    /// </summary>
    private Window? _host;

    /// <summary>Durée d'affichage du rappel.</summary>
    private readonly DispatcherTimer _hintTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5),
    };

    /// <param name="root">
    /// Contenu de la session : sert à retrouver la fenêtre qui l'héberge
    /// réellement, et à savoir si la session est à l'écran.
    /// </param>
    /// <param name="hint">
    /// Rappel affiché quelques secondes à l'entrée en plein écran. La barre
    /// d'outils disparaissant alors, le moyen d'en sortir serait sinon
    /// invisible. Échap n'est pas utilisé : la touche doit rester disponible
    /// pour la machine distante.
    /// </param>
    /// <param name="chrome">Ce qui entoure le contenu : barres d'outils, d'état…</param>
    public SessionFullscreen(FrameworkElement root, UIElement? hint, params UIElement[] chrome)
    {
        _root = root;
        _hint = hint;
        _chrome = chrome;
        _hintTimer.Tick += (_, _) => HideHint();
    }

    public bool IsActive => _host is not null;

    /// <summary>
    /// L'état vient de changer. Laisse la session ajuster ce qui lui est propre,
    /// l'info-bulle de son bouton par exemple.
    /// </summary>
    public event Action<bool>? Changed;

    /// <summary>Bascule ; renvoie vrai si la session est désormais en plein écran.</summary>
    public bool Toggle()
    {
        var host = _host ?? Window.GetWindow(_root);
        if (host is null) return false;

        var fullscreen = FullscreenController.Toggle(host);
        _host = fullscreen ? host : null;
        Apply(host, fullscreen);
        return fullscreen;
    }

    /// <summary>Sort du plein écran ; sans effet si la session n'y est pas.</summary>
    public void Exit()
    {
        HideHint();
        if (_host is not { } host) return;

        FullscreenController.Exit(host);
        _host = null;
        Apply(host, false);
    }

    /// <summary>
    /// Réaligne les barres de cette session sur l'état réel de la fenêtre hôte.
    /// Sans ce recalage, on retrouverait un onglet fenêtré dont la barre
    /// d'outils serait restée masquée.
    /// </summary>
    public void Sync()
    {
        if (!_root.IsVisible) return;

        var host = Window.GetWindow(_root);
        if (host is null) return;

        var fullscreen = FullscreenController.IsFullscreen(host);
        _host = fullscreen ? host : null;
        Apply(host, fullscreen);
    }

    private void Apply(Window host, bool fullscreen)
    {
        var visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        foreach (var element in _chrome) element.Visibility = visibility;

        if (host is SessionsWindow sessions) sessions.SetTabStripVisible(!fullscreen);

        if (fullscreen) ShowHint();
        else HideHint();

        Changed?.Invoke(fullscreen);
    }

    private void ShowHint()
    {
        if (_hint is null) return;
        _hint.Visibility = Visibility.Visible;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void HideHint()
    {
        _hintTimer.Stop();
        if (_hint is not null) _hint.Visibility = Visibility.Collapsed;
    }
}
