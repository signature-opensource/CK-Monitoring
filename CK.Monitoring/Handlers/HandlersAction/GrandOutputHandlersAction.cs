using System;
using System.Threading.Tasks;
using CK.Core;

namespace CK.Monitoring;

/// <summary>
/// Template method of an action on the <see cref="DispatcherSink.HandlerList"/> (without result).
/// Use <see cref="GrandOutputHandlersAction{TResult}"/> when a result is needed.
/// </summary>
public abstract class GrandOutputHandlersAction : IGrandOutputHandlersActionBase
{
    TaskCompletionSource _tcs;

    protected GrandOutputHandlersAction()
    {
        _tcs = new TaskCompletionSource();
    }

    /// <summary>
    /// Must implement the action on the <paramref name="handlers"/>.
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
