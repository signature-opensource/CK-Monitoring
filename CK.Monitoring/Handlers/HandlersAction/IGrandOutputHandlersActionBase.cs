using System.Threading.Tasks;
using CK.Core;

namespace CK.Monitoring;

/// <summary>
/// Unifies <see cref="GrandOutputHandlersAction"/> and <see cref="GrandOutputHandlersAction{TResult}"/>.
/// <para>
/// This interface cannot be implemented outside CK.Monitoring assembly.
/// </para>
/// </summary>
public interface IGrandOutputHandlersActionBase
{
    /// <summary>
    /// Gets the task that is completed once this action has been executed.
    /// </summary>
    Task Completion { get; }

    internal ValueTask DoRunAsync( IActivityMonitor monitor, DispatcherSink.HandlerList handlers );

    internal void Cancel();
}
