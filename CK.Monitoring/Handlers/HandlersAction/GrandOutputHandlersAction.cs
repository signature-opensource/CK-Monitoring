using System;
using System.Threading.Tasks;
using CK.Core;

namespace CK.Monitoring;

/// <summary>
/// Template method of an action on the <see cref="DispatcherSink.HandlerList"/> (without result).
/// Use <see cref="GrandOutputHandlersAction{TResult}"/> when a result is needed.
/// <para>
/// This is submitted thanks to <see cref="DispatcherSink.Submit(IGrandOutputHandlersActionBase)"/>.
/// </para>
/// </summary>
public abstract class GrandOutputHandlersAction : IGrandOutputHandlersActionBase
{
    TaskCompletionSource _tcs;

    /// <summary>
    /// Initializes a new <see cref="GrandOutputHandlersAction"/>.
    /// </summary>
    protected GrandOutputHandlersAction()
    {
        _tcs = new TaskCompletionSource();
    }

    /// <summary>
    /// Must implement the action on the <paramref name="handlers"/>.
    /// <para>
    /// This should NOT call <see cref="IGrandOutputHandler.ActivateAsync(IActivityMonitor)"/> or <see cref="IGrandOutputHandler.DeactivateAsync(IActivityMonitor)"/>:
    /// instead use <see cref="DispatcherSink.HandlerList.AddAsync(IActivityMonitor, IGrandOutputHandler)"/> and <see cref="DispatcherSink.HandlerList.RemoveAsync(IActivityMonitor, IGrandOutputHandler)"/>.
    /// </para>
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    /// <param name="handlers">The handler list.</param>
    /// <returns>The awaitable.</returns>
    protected abstract ValueTask RunAsync( IActivityMonitor monitor, DispatcherSink.HandlerList handlers );

    /// <inheritdoc />
    public Task Completion => _tcs.Task;

    async ValueTask IGrandOutputHandlersActionBase.DoRunAsync( IActivityMonitor monitor, DispatcherSink.HandlerList handlers )
    {
        try
        {
            await RunAsync( monitor, handlers ).ConfigureAwait( false );
            _tcs.SetResult();
        }
        catch( Exception ex )
        {
            _tcs.SetException( ex );
        }
    }

    void IGrandOutputHandlersActionBase.Cancel() => _tcs.TrySetCanceled();
}
