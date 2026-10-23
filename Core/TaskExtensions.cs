namespace MeshControl.Core;

/// <summary>Tâches lancées sans être attendues.</summary>
public static class TaskExtensions
{
    /// <summary>
    /// Laisse la tâche s'achever seule, en lisant son éventuel échec. Une
    /// exception que personne ne lit remonte sinon au ramasse-miettes, qui la
    /// signale comme « exception de tâche non observée ».
    /// </summary>
    public static void Forget(this Task task) =>
        task.ContinueWith(static t => _ = t.Exception,
                          CancellationToken.None,
                          TaskContinuationOptions.OnlyOnFaulted
                          | TaskContinuationOptions.ExecuteSynchronously,
                          TaskScheduler.Default);
}
