using System.Windows.Threading;
using MeshControl.Core;

namespace MeshControl.UI;

/// <summary>Implémentation WPF de <see cref="IUiDispatcher"/>.</summary>
public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action, Action? onAborted = null)
    {
        var operation = dispatcher.BeginInvoke(action);
        if (onAborted is not null) operation.Aborted += (_, _) => onAborted();
    }
}
