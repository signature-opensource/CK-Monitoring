using System;
using System.Threading.Tasks;
using CK.Core;

namespace CK.Monitoring;

/// <summary>
/// Template method of an action on the <see cref="DispatcherSink.HandlerList"/> with a result.
/// <para>
/// This is submitted thanks to <see cref="DispatcherSink.Submit(IGrandOutputHandlersActionBase)"/>.
/// </para>
/// </summary>
public abstract class GrandOutputHandlersAction<TResult> : IGrandOutputHandlersActionBase
{
    TaskCompletionSource<TResult> _tcs;

    /// <summary>
    /// Initializes a new <see cref="GrandOutputHandlersAction{TResult}"/>.
    /// </summary>
    protected GrandOutputHandlersAction()
    {
        _tcs = new TaskCompletionSource<TResult>();
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
    /// <returns>The action result.</returns>
    protected abstract ValueTask<TResult> RunAsync( IActivityMonitor monitor, DispatcherSink.HandlerList handlers );

    Task IGrandOutputHandlersActionBase.Completion => _tcs.Task;

    /// <summary>
    /// Gets the task that is completed once this action has been executed.
    /// </summary>
    public Task<TResult> Completion => _tcs.Task;

    async ValueTask IGrandOutputHandlersActionBase.DoRunAsync( IActivityMonitor monitor, DispatcherSink.HandlerList handlers )
    {
        try
        {
            var r = await RunAsync( monitor, handlers ).ConfigureAwait( false );
            _tcs.SetResult( r );
        }
        catch( Exception ex )
        {
            _tcs.SetException( ex );
        }
    }

    void IGrandOutputHandlersActionBase.Cancel() => _tcs.TrySetCanceled();

}
