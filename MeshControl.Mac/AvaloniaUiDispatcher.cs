using Avalonia.Threading;
using MeshControl.Core;

namespace MeshControl.Mac;

/// <summary>Implémentation Avalonia de <see cref="IUiDispatcher"/>.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static AvaloniaUiDispatcher Instance { get; } = new();

    private AvaloniaUiDispatcher() { }

    /// <summary>
    /// Avalonia ne signale pas les actions abandonnées à la fermeture :
    /// <paramref name="onAborted"/> n'est jamais appelé. Sans conséquence ici —
    /// les tampons loués qui ne reviennent pas au pool sont simplement repris
    /// par le ramasse-miettes.
    /// </summary>
    public void Post(Action action, Action? onAborted = null) => Dispatcher.UIThread.Post(action);
}
