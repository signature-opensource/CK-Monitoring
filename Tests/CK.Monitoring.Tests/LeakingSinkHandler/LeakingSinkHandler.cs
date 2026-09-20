using System;
using System.Threading;
using System.Threading.Tasks;
using CK.Core;

namespace CK.Monitoring.Tests;

/// <summary>
/// Test artifact that reproduces the bug the leak detection is about: it <see cref="InputLogEntry.AddRef()"/> the
/// entries whose text starts with <see cref="LeakMarker"/> and drops them without calling Release().
/// <para>
/// This goes through the real handler path: once the <see cref="DispatcherSink"/> has released its own reference,
/// the entry is alive with nothing referencing it, which is exactly what the finalizer detects.
/// </para>
/// </summary>
public sealed class LeakingSinkHandler : IGrandOutputHandler
{
    /// <summary>
    /// Entries whose <see cref="InputLogEntry.Text"/> starts with this are leaked on purpose.
    /// </summary>
    public const string LeakMarker = "LEAK-ME";

    static int _leakedHere;

    /// <summary>
    /// Gets the number of entries this handler has deliberately leaked.
    /// </summary>
    public static int LeakedHere => _leakedHere;

    /// <summary>
    /// Resets <see cref="LeakedHere"/>.
    /// </summary>
    public static void Reset() => Interlocked.Exchange( ref _leakedHere, 0 );

    public LeakingSinkHandler( LeakingSinkHandlerConfiguration c )
    {
    }

    public ValueTask<bool> ActivateAsync( IActivityMonitor m ) => ValueTask.FromResult( true );

    public ValueTask<bool> ApplyConfigurationAsync( IActivityMonitor m, IHandlerConfiguration c )
        => ValueTask.FromResult( c is LeakingSinkHandlerConfiguration );

    public ValueTask DeactivateAsync( IActivityMonitor m ) => ValueTask.CompletedTask;

    public ValueTask HandleAsync( IActivityMonitor m, InputLogEntry logEvent )
    {
        if( logEvent.Text != null && logEvent.Text.StartsWith( LeakMarker, StringComparison.Ordinal ) )
        {
            // The bug: takes a reference and forgets to release it.
            logEvent.AddRef();
            Interlocked.Increment( ref _leakedHere );
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTimerAsync( IActivityMonitor m, TimeSpan timerSpan ) => ValueTask.CompletedTask;
}
