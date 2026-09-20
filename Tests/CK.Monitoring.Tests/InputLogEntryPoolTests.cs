using System;
using System.Linq;
using System.Threading.Tasks;
using CK.Core;
using NUnit.Framework;
using Shouldly;

namespace CK.Monitoring.Tests;

[TestFixture]
public class InputLogEntryPoolTests
{
    [SetUp]
    public void ResetPoolDiagnostics()
    {
        TestHelper.InitalizePaths();
        TestHelper.WaitForNoMoreAliveInputLogEntry();
        InputLogEntry.PoolDiagnostics.Reset();
        LeakingSinkHandler.Reset();
    }

    [Test]
    public async Task an_entry_that_is_never_released_is_detected_when_it_is_collected_Async()
    {
        var diag = InputLogEntry.PoolDiagnostics;

        var c = new GrandOutputConfiguration().AddHandler( new LeakingSinkHandlerConfiguration() );
        await using( var g = new GrandOutput( c ) )
        {
            var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
            g.EnsureGrandOutputClient( m );
            m.Info( $"{LeakingSinkHandler.LeakMarker} from a monitor." );
            g.ExternalLog( LogLevel.Info, $"{LeakingSinkHandler.LeakMarker} from an external log." );
            await g.Sink.SyncWaitAsync();
        }
        LeakingSinkHandler.LeakedHere.ShouldBe( 2, "The handler took two references and dropped them." );

        for( int i = 0; i < 5 && diag.LeakedCount < 2; ++i )
        {
            GC.Collect( 2, GCCollectionMode.Forced, blocking: true );
            GC.WaitForPendingFinalizers();
        }

        diag.LeakedCount.ShouldBe( 2 );
        var samples = diag.LeakSamples;
        samples.Count.ShouldBe( 2 );

        // Finalization order is not specified: find the samples instead of indexing them.
        var fromMonitor = samples.Single( s => s.Contains( "from a monitor" ) );
        fromMonitor.ShouldContain( "Line" );
        fromMonitor.ShouldContain( "Info" );
        fromMonitor.ShouldContain( "@", customMessage: "A monitor log carries its source address." );
        fromMonitor.ShouldContain( "InputLogEntryPoolTests.cs" );

        // ExternalLog has no [CallerFilePath].
        var fromExternal = samples.Single( s => s.Contains( "from an external log" ) );
        fromExternal.ShouldContain( "<external>" );

        // A leaked entry never comes back: it holds the alive count up forever. That IS the leak, and it is
        // why the pool saturation could never see it.
        diag.AliveCount.ShouldBe( 2 );
        diag.Reset( resetAliveCount: true );
    }

    // Builds a peak of exactly 'count' concurrently alive entries by holding the sink still while they are
    // logged, then lets it drain. Returns once everything has been released.
    static async Task BurstAsync( int count )
    {
        var c = new GrandOutputConfiguration().AddHandler( new GatedSinkHandlerConfiguration() );
        await using( var g = new GrandOutput( c ) )
        {
            var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
            g.EnsureGrandOutputClient( m );
            // The first entry is what the sink blocks on: log it before closing the gate would be a race,
            // so the gate is closed first and every entry piles up behind it.
            GatedSinkHandler.Close();
            try
            {
                for( int i = 0; i < count; ++i ) m.Info( "A burst of logging activity." );
                InputLogEntry.AliveCount.ShouldBeGreaterThanOrEqualTo( count - 1,
                                                                       "The sink is held: nothing has been released yet." );
            }
            finally
            {
                GatedSinkHandler.Open();
            }
            await g.Sink.SyncWaitAsync();
        }
        TestHelper.WaitForNoMoreAliveInputLogEntry();
    }

    [Test]
    public async Task pool_saturation_is_silent_and_is_not_a_leak_Async()
    {
        var diag = InputLogEntry.PoolDiagnostics;
        var staticLogs = new System.Collections.Generic.List<string>();
        ActivityMonitor.StaticLogHandler h = delegate ( ref ActivityMonitorLogData d ) { staticLogs.Add( d.Text ); };
        ActivityMonitor.OnStaticLog += h;
        try
        {
            // 500 more than the pool can ever hold: the pool is pushed to its maximal capacity and then
            // overflows, which is exactly the burst that used to spam an error every second forever.
            await BurstAsync( InputLogEntry.MaximalPoolCapacity + 500 );

            // Guard against this test silently becoming a no op.
            diag.PeakAliveCount.ShouldBeGreaterThanOrEqualTo( InputLogEntry.MaximalPoolCapacity + 499 );
            diag.SaturatedCount.ShouldBeGreaterThan( 0, "The pool really overflowed." );
            diag.CapacityIncreaseCount.ShouldBeGreaterThan( 0, "The capacity really grew." );

            diag.LeakedCount.ShouldBe( 0, "Nothing leaked." );
            diag.LastLeakReport.ShouldBeNull( "A peak of activity is not a leak." );
            staticLogs.Where( t => t != null && t.Contains( "pool" ) )
                      .ShouldBeEmpty( "Capacity growth and saturation are counted, never logged." );
        }
        finally
        {
            ActivityMonitor.OnStaticLog -= h;
        }
    }

    [Test]
    public async Task the_pool_trims_itself_back_to_the_observed_demand_Async()
    {
        var diag = InputLogEntry.PoolDiagnostics;

        await BurstAsync( InputLogEntry.MaximalPoolCapacity + 500 );

        InputLogEntry.CurrentPoolCapacity.ShouldBe( InputLogEntry.MaximalPoolCapacity,
                                                    "The burst pushed the pool to its maximal capacity." );
        InputLogEntry.PooledEntryCount.ShouldBe( InputLogEntry.MaximalPoolCapacity );

        // Before this trimming existed, the pool stayed there for the rest of the process life and every
        // later Release() that lost the _fastItem race re-armed the error. A quiet window history brings it
        // back down (the history is 6 windows long, so the burst takes that many to be forgotten).
        for( int i = 0; i < 8; ++i ) diag.CloseWindow();

        InputLogEntry.CurrentPoolCapacity.ShouldBe( InputLogEntry.InitialPoolCapacity );
        InputLogEntry.PooledEntryCount.ShouldBeLessThanOrEqualTo( InputLogEntry.InitialPoolCapacity + 1 );
    }
}
