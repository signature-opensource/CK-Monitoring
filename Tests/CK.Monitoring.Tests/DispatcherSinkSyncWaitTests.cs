using NUnit.Framework;
using Shouldly;
using System.Threading.Tasks;

namespace CK.Monitoring.Tests;

[TestFixture]
public class DispatcherSinkSyncWaitTests
{
    /// <summary>
    /// SyncWait queues a signal that the dispatcher pulses when it reaches it. The caller must already be waiting
    /// on the signal by then: a Monitor.Pulse without waiter is lost, and the caller would then wait forever.
    /// An idle sink is the worst case: the dispatcher handles the signal as soon as it is queued.
    /// </summary>
    [Test]
    public async Task SyncWait_never_loses_its_signal_Async()
    {
        await using var g = new GrandOutput( new GrandOutputConfiguration() );
        var run = Task.Factory.StartNew( () =>
        {
            for( int i = 0; i < 100_000; ++i )
            {
                g.Sink.SyncWait();
            }
        }, default, TaskCreationOptions.LongRunning, TaskScheduler.Default );
        (await Task.WhenAny( run, Task.Delay( 30_000 ) ))
            .ShouldBeSameAs( run, "SyncWait lost its signal: it waits forever." );
    }
}
