using System;
using System.IO;
using System.Linq;
using CK.Core;
using NUnit.Framework;
using System.Threading.Tasks;
using Shouldly;
using System.Threading;

namespace CK.Monitoring.Tests;

[TestFixture]
public class TextFileTests
{
    static readonly Exception _exceptionWithInner;
    static readonly Exception _exceptionWithInnerLoader;

    #region static initialization of exceptions
    static TextFileTests()
    {
        _exceptionWithInner = ThrowExceptionWithInner( false );
        _exceptionWithInnerLoader = ThrowExceptionWithInner( true );
    }

    static Exception ThrowExceptionWithInner( bool loaderException = false )
    {
        Exception e;
        try { throw new Exception( "Outer", loaderException ? ThrowLoaderException() : ThrowSimpleException( "Inner" ) ); }
        catch( Exception ex ) { e = ex; }
        return e;
    }

    static Exception ThrowSimpleException( string message )
    {
        Exception e;
        try { throw new Exception( message ); }
        catch( Exception ex ) { e = ex; }
        return e;
    }

    static Exception ThrowLoaderException()
    {
        Exception e = null!;
        try { Type.GetType( "A.Type, An.Unexisting.Assembly", true ); }
        catch( Exception ex ) { e = ex; }
        return e;
    }
    #endregion

    [SetUp]
    public void InitializePath()
    {
        TestHelper.InitalizePaths();
        TestHelper.WaitForNoMoreAliveInputLogEntry();
    }

    [TearDown]
    public void WaitForNoMoreAliveInputLogEntry()
    {
        TestHelper.WaitForNoMoreAliveInputLogEntry();
    }

    [Test]
    public async Task text_file_auto_flush_and_reconfiguration_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "AutoFlush" );

        var textConf = new Handlers.TextFileConfiguration() { Path = "AutoFlush" };
        textConf.AutoFlushRate.ShouldBe( 6, "Default AutoFlushRate configuration." );

        // Avoid relying on the internal 500ms default.
        var config = new GrandOutputConfiguration { TimerDuration = TimeSpan.FromMilliseconds( 500 ) }
                            .AddHandler( textConf );

        await using( GrandOutput g = new GrandOutput( config ) )
        {
            var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
            g.EnsureGrandOutputClient( m );
            Thread.Sleep( 5 );
            m.Info( "Must wait 3 seconds..." );
            Thread.Sleep( 700 );
            string tempFile = Directory.EnumerateFiles( folder ).Single();
            TestHelper.FileReadAllText( tempFile ).ShouldBeEmpty();
            Thread.Sleep( 3000 );
            TestHelper.FileReadAllText( tempFile ).ShouldContain( "Must wait 3 seconds..." );

            textConf.AutoFlushRate = 1;
            m.Info( "Reconfiguration triggers a flush..." );
            Thread.Sleep( 10 );
            g.ApplyConfiguration( new GrandOutputConfiguration().AddHandler( textConf ), waitForApplication: true );
            TestHelper.FileReadAllText( tempFile ).ShouldContain( "Reconfiguration triggers a flush..." );
            m.Info( "Wait only approx 500ms..." );
            Thread.Sleep( 700 );
            string final = TestHelper.FileReadAllText( tempFile );
            final.ShouldContain( "Wait only approx 500ms" );
        }
    }

    [Test]
    public async Task external_logs_quick_test_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "ExternalLogsQuickTest" );

        var textConf = new Handlers.TextFileConfiguration() { Path = "ExternalLogsQuickTest" };
        var config = new GrandOutputConfiguration().AddHandler( textConf );
        await using( GrandOutput g = new GrandOutput( config ) )
        {
            await Task.Run( () =>
            {
                ActivityMonitor.StaticLogger.Info( $"Async started from ActivityMonitor.StaticLogger." );
                g.ExternalLog( LogLevel.Info, message: "Async started." );
            } );
            var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
            g.EnsureGrandOutputClient( m );
            m.Info( "Normal monitor starts." );
            Task t = Task.Run( () =>
            {
                for( int i = 0; i < 10; ++i )
                {
                    ActivityMonitor.StaticLogger.Info( $"Async n°{i} from ActivityMonitor.StaticLogger." );
                    g.ExternalLog( LogLevel.Info, $"Async n°{i}." );
                }
            } );
            m.MonitorEnd( "This is the end." );
            await t;
        }
        string textLogged = File.ReadAllText( Directory.EnumerateFiles( folder ).Single() );
        textLogged.ShouldContain( "Normal monitor starts." )
                  .ShouldContain( "Async started from ActivityMonitor.StaticLogger." )
                  .ShouldContain( "Async started." )
                  .ShouldContain( "Async n°0." )
                  .ShouldContain( "Async n°9." )
                  .ShouldContain( "Async n°0 from ActivityMonitor.StaticLogger." )
                  .ShouldContain( "Async n°9 from ActivityMonitor.StaticLogger." )
                  .ShouldContain( "This is the end." );
    }

    [Test]
    public async Task external_logs_stress_test_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "ExternalLogsStressTest" );

        var textConf = new Handlers.TextFileConfiguration() { Path = "ExternalLogsStressTest" };
        var config = new GrandOutputConfiguration().AddHandler( textConf );
        int taskCount = 20;
        int logCount = 10;
        await using( GrandOutput g = new GrandOutput( config ) )
        {
            var tasks = Enumerable.Range( 0, taskCount ).Select( c => Task.Run( () =>
             {
                 for( int i = 0; i < logCount; ++i )
                 {
                     Thread.Sleep( 2 );
                     g.ExternalLog( LogLevel.Info, message: $"{c} n°{i}." );
                 }
             } ) ).ToArray();
            await Task.WhenAll( tasks );
        }
        string textLogged = File.ReadAllText( Directory.EnumerateFiles( folder ).Single() );
        for( int c = 0; c < taskCount; ++c )
            for( int i = 0; i < logCount; ++i )
                textLogged.ShouldContain( $"{c} n°{i}." );
    }

    static readonly CKTrait _myTag = ActivityMonitor.Tags.Register( "external_logs_filtering" );

    [Test]
    public async Task external_logs_filtering_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "ExternalLogsFiltering" );

        var textConf = new Handlers.TextFileConfiguration() { Path = "ExternalLogsFiltering" };
        var config = new GrandOutputConfiguration().AddHandler( textConf );
        ActivityMonitor.DefaultFilter.Line.ShouldBe( LogLevelFilter.Trace );
        await using( GrandOutput g = new GrandOutput( config ) )
        {
            g.ExternalLog( LogLevel.Debug, message: "NOSHOW" );
            g.ExternalLog( LogLevel.Trace, message: "SHOW 0" );
            g.ExternalLogLevelFilter = LogLevelFilter.Debug;
            g.ExternalLog( LogLevel.Debug, message: "SHOW 1" );
            g.ExternalLogLevelFilter = LogLevelFilter.Error;
            g.ExternalLog( LogLevel.Warn, message: "NOSHOW" );
            g.ExternalLog( LogLevel.Error, message: "SHOW 2" );
            g.ExternalLog( LogLevel.Fatal, message: "SHOW 3" );
            g.ExternalLog( LogLevel.Trace | LogLevel.IsFiltered, message: "SHOW 4" );
            g.ExternalLogLevelFilter = LogLevelFilter.None;
            g.ExternalLog( LogLevel.Debug, message: "NOSHOW" );
            g.ExternalLog( LogLevel.Trace, message: "SHOW 4" );

            g.IsExternalLogEnabled( LogLevel.Debug ).ShouldBeFalse();
            g.IsExternalLogEnabled( LogLevel.Trace ).ShouldBeTrue();

            ActivityMonitor.Tags.AddFilter( _myTag, new LogClamper( LogFilter.Verbose, true ) );

            // Verbose allows Info, not Trace lines.
            g.ExternalLog( LogLevel.Info, _myTag, message: "SHOW 5" );
            g.ExternalLog( LogLevel.Trace, _myTag, message: "NOSHOW" );

            g.IsExternalLogEnabled( LogLevel.Info, _myTag ).ShouldBeTrue();
            g.IsExternalLogEnabled( LogLevel.Trace, _myTag ).ShouldBeFalse();

            ActivityMonitor.Tags.RemoveFilter( _myTag );

            g.IsExternalLogEnabled( LogLevel.Trace, _myTag ).ShouldBeTrue();
            g.ExternalLog( LogLevel.Trace, _myTag, message: "SHOW 6" );
        }
        string textLogged = File.ReadAllText( Directory.EnumerateFiles( folder ).Single() );
        textLogged.ShouldContain( "SHOW 0" )
                  .ShouldContain( "SHOW 1" )
                  .ShouldContain( "SHOW 2" )
                  .ShouldContain( "SHOW 3" )
                  .ShouldContain( "SHOW 4" )
                  .ShouldContain( "SHOW 5" )
                  .ShouldContain( "SHOW 6" )
                  .ShouldNotContain( "NOSHOW" );
    }

    [Explicit]
    [Test]
    public async Task dumping_text_file_with_multiple_monitors_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "TextFileMulti" );
        Random r = new Random();
        GrandOutputConfiguration config = new GrandOutputConfiguration()
                                                .AddHandler( new Handlers.TextFileConfiguration() { Path = "TextFileMulti" } );
        await using( GrandOutput g = new GrandOutput( config ) )
        {
            Parallel.Invoke(
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs2( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs2( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs1( r, g ),
                () => DumpSampleLogs2( r, g )
                );
        }
        FileInfo f = new DirectoryInfo( LogFile.RootLogPath + "TextFileMulti" ).EnumerateFiles().Single();
        string text = File.ReadAllText( f.FullName );
        Console.WriteLine( text );
    }

    [Test]
    public async Task dumping_text_file_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "TextFile" );
        Random r = new Random();
        GrandOutputConfiguration config = new GrandOutputConfiguration()
                                                .AddHandler( new Handlers.TextFileConfiguration() { Path = "TextFile" } );
        await using( GrandOutput g = new GrandOutput( config ) )
        {
            DumpSampleLogs1( r, g );
            DumpSampleLogs2( r, g );
        }
        FileInfo f = new DirectoryInfo( LogFile.RootLogPath + "TextFile" ).EnumerateFiles().Single();
        string text = File.ReadAllText( f.FullName );
        Console.WriteLine( text );
        text.ShouldContain( "First Activity..." );
        text.ShouldContain( "End of first activity." );
        text.ShouldContain( "another one" );
        text.ShouldContain( "Something must be said" );
        text.ShouldContain( "My very first conclusion." );
        text.ShouldContain( "My second conclusion." );
        string lineWithSecondConclusion = text.Split( "\n" ).Single( s => s.Contains( "My second conclusion." ) );
        lineWithSecondConclusion
            .Replace( "My second conclusion.", "" )
            .Replace( " ", "" )
            .Replace( "|", "" )
            .Replace( "\n", "" )
            .Replace( "\r", "" )
            .ShouldBeEmpty();

    }

    [Test]
    public async Task text_file_auto_delete_by_date_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "AutoDelete_Date" );

        var textConf = new Handlers.TextFileConfiguration() { Path = "AutoDelete_Date" };
        textConf.HousekeepingRate.ShouldBe( 1800, "Default HousekeepingRate configuration" );
        textConf.MinimumDaysToKeep.ShouldBe( 60, "Default HousekeepingRate configuration" );
        textConf.MinimumTimeSpanToKeep.ShouldBe( TimeSpan.FromDays( 60 ), "Default HousekeepingRate configuration" );
        textConf.MaximumTotalKbToKeep.ShouldBe( 100_000, "Default HousekeepingRate configuration" );

        // Change configuration for tests
        textConf.HousekeepingRate = 1; // Run every 500ms normally (here TimerDuration is set to 100ms).
        textConf.MaximumTotalKbToKeep = 0; // Always delete file beyond max size
        textConf.MinimumTimeSpanToKeep = TimeSpan.FromSeconds( 3 ); // Delete files older than 3 seconds
        var config = new GrandOutputConfiguration().AddHandler( textConf );

        // Changes the default 500 ms to trigger OnTimerAsync more often.
        config.TimerDuration = TimeSpan.FromMilliseconds( 100 );

        // TEST DELETION BY DATE

        await using( GrandOutput g = new GrandOutput( config ) )
        {
            var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
            g.EnsureGrandOutputClient( m );
            Thread.Sleep( 5 );
            m.Info( "Hello world" );
            Thread.Sleep( 30 );

            string tempFile = Directory.EnumerateFiles( folder ).Single();
            File.Exists( tempFile ).ShouldBeTrue( "Log file was created and exists" );

            // Wait for next flush (~100ms), and deletion threshold (3000ms)
            Thread.Sleep( 3200 );

            File.Exists( tempFile ).ShouldBeTrue( "Log file wasn't deleted yet - it's still active" );
        }
        string finalLogFile = Directory.EnumerateFiles( folder ).Single();

        // Open another GrandOutput to trigger housekeeping
        await using( GrandOutput g = new GrandOutput( config ) )
        {
            await Task.Delay( 200 );
        }
#if RELEASE
        await Task.Delay( 400 );
#endif
        File.Exists( finalLogFile ).ShouldBeFalse( "Inactive log file was deleted" );
    }

    [Test]
    public async Task text_file_auto_delete_by_size_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "AutoDelete_Size" );

        var textConf = new Handlers.TextFileConfiguration() { Path = "AutoDelete_Size" };
        // Change configuration for tests
        textConf.HousekeepingRate = 1; // Run every 500ms normally (here TimerDuration is set to 100ms).
        textConf.MaximumTotalKbToKeep = 1; // Always delete file beyond max size
        textConf.MinimumTimeSpanToKeep = TimeSpan.Zero; // Make minimum timespan
        var config = new GrandOutputConfiguration().AddHandler( textConf );

        int lineLengthToLogToGet1000bytes = 500;

        // Changes the default 500 ms to trigger OnTimerAsync more often.
        config.TimerDuration = TimeSpan.FromMilliseconds( 100 );

        // Create 3*1 KB log files
        for( int i = 0; i < 3; i++ )
        {
            await using( GrandOutput g = new GrandOutput( config ) )
            {
                var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
                g.EnsureGrandOutputClient( m );
                m.Info( new string( 'X', lineLengthToLogToGet1000bytes ) );
            }
        }

        long GetTotalLogSize()
        {
            return Directory.EnumerateFiles( folder ).Sum( x => new FileInfo( x ).Length );
        }

        var totalLogSize = GetTotalLogSize();
        totalLogSize.ShouldBeGreaterThan( 2500 );

        // Open another GrandOutput to trigger housekeeping.
        // Note: this DOES create a file!
        await using( GrandOutput g = new GrandOutput( config ) )
        {
            // Wait for next flush (~100 ms)
            Thread.Sleep( 200 );
        }
#if RELEASE
        await Task.Delay( 400 );
#endif
        var files = Directory.GetFiles( folder ).Select( f => Path.GetFileName( f ) );
        files.Count().ShouldBe( 2, $"Only 2 files should be kept - the last log file, and 1x~1KB file: {files.Concatenate()}" );
    }

    static void DumpSampleLogs1( Random r, GrandOutput g )
    {
        var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        g.EnsureGrandOutputClient( m );
        m.SetTopic( "First Activity..." );
        if( r.Next( 3 ) == 0 ) System.Threading.Thread.Sleep( 100 + r.Next( 2500 ) );
        using( m.OpenTrace( "Opening trace" ) )
        {
            if( r.Next( 3 ) == 0 ) System.Threading.Thread.Sleep( 100 + r.Next( 2500 ) );
            m.Trace( "A trace in group." );
            if( r.Next( 3 ) == 0 ) System.Threading.Thread.Sleep( 100 + r.Next( 2500 ) );
            m.Info( "An info in group." );
            if( r.Next( 3 ) == 0 ) System.Threading.Thread.Sleep( 100 + r.Next( 2500 ) );
            m.Warn( "A warning in group." );
            if( r.Next( 3 ) == 0 ) System.Threading.Thread.Sleep( 100 + r.Next( 2500 ) );
            m.Error( "An error in group." );
            if( r.Next( 3 ) == 0 ) System.Threading.Thread.Sleep( 100 + r.Next( 2500 ) );
            m.Fatal( "A fatal in group." );
        }
        if( r.Next( 3 ) == 0 ) System.Threading.Thread.Sleep( 100 + r.Next( 2500 ) );
        m.Trace( "End of first activity." );
    }

    static void DumpSampleLogs2( Random r, GrandOutput g )
    {
        var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        g.EnsureGrandOutputClient( m );

        m.Fatal( "An error occured", _exceptionWithInner );
        m.Fatal( "Same error occured (wrapped in CKException)", new CKException( CKExceptionData.CreateFrom( _exceptionWithInner ) ) );
        m.SetTopic( "This is a topic..." );
        m.Trace( "a trace" );
        m.Trace( "another one" );
        m.SetTopic( "Please, show this topic!" );
        m.Trace( "Anotther trace." );
        using( m.OpenTrace( "A group trace." ) )
        {
            m.Trace( "A trace in group." );
            m.Info( "An info..." );
            using( m.OpenInfo( @"A group information... with a 
multi
-line
message. 
This MUST be correctly indented!" ) )
            {
                m.Info( "Info in info group." );
                m.Info( "Another info in info group." );
                m.Error( "An error.", _exceptionWithInnerLoader );
                m.Error( "Same error occured (wrapped in CKException)", new CKException( CKExceptionData.CreateFrom( _exceptionWithInnerLoader ) ) );
                m.Warn( "A warning." );
                m.Trace( "Something must be said." );
                m.CloseGroup( "Everything is in place." );
            }
        }
        m.SetTopic( null );
        using( m.OpenTrace( "A group with multiple conclusions." ) )
        {
            using( m.OpenTrace( "A group with no conclusion." ) )
            {
                m.Trace( "Something must be said." );
            }
            m.CloseGroup( new[] {
                    new ActivityLogGroupConclusion( "My very first conclusion." ),
                    new ActivityLogGroupConclusion( "My second conclusion." ),
                    new ActivityLogGroupConclusion( @"My very last conclusion
is a multi line one.
and this is fine!" )
                } );
        }
        m.Trace( "This is the final trace." );
    }

}
