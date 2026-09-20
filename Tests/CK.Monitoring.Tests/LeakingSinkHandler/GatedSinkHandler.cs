using System;
using System.Threading;
using System.Threading.Tasks;
using CK.Core;

namespace CK.Monitoring.Tests;

/// <summary>
/// Test artifact that can hold the <see cref="DispatcherSink"/> still.
/// <para>
/// While <see cref="Gate"/> is closed the sink blocks on the first entry it dequeues, so everything logged
/// afterwards piles up in the (unbounded) dispatcher queue and stays alive. This builds a peak of concurrently
/// alive <see cref="InputLogEntry"/> of an exact size, instead of relying on the producer outrunning the sink,
/// which depends on the machine and on how warm the JIT happens to be.
/// </para>
/// </summary>
public sealed class GatedSinkHandler : IGrandOutputHandler
{
    static readonly ManualResetEventSlim _gate = new ManualResetEventSlim( true );

    public GatedSinkHandler( GatedSinkHandlerConfiguration c )
    {
    }

    /// <summary>
    /// Blocks the sink. Entries logged from now on accumulate in the dispatcher queue.
    /// </summary>
    public static void Close() => _gate.Reset();

    /// <summary>
    /// Lets the sink drain what accumulated.
    /// </summary>
    public static void Open() => _gate.Set();

    public ValueTask<bool> ActivateAsync( IActivityMonitor m ) => ValueTask.FromResult( true );

    public ValueTask<bool> ApplyConfigurationAsync( IActivityMonitor m, IHandlerConfiguration c )
        => ValueTask.FromResult( c is GatedSinkHandlerConfiguration );

    public ValueTask DeactivateAsync( IActivityMonitor m )
    {
        // Never leave the sink blocked: it must be able to terminate.
        _gate.Set();
        return ValueTask.CompletedTask;
    }

    public ValueTask HandleAsync( IActivityMonitor m, InputLogEntry logEvent )
    {
        // Deliberately synchronous: this holds the single reader of the dispatcher queue.
        _gate.Wait();
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTimerAsync( IActivityMonitor m, TimeSpan timerSpan ) => ValueTask.CompletedTask;
}
