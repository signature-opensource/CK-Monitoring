using CK.Core;
using Shouldly;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CK.Monitoring.Tests;

public class GrandOutputReconfigurationTests
{
    [Test]
    public async Task BinaryGzip_reconfiguration_Async()
    {
        string folder = TestHelper.PrepareLogFolder( nameof( BinaryGzip_reconfiguration_Async ) );
        var h = new Handlers.BinaryFileConfiguration()
        {
            Path = Path.Combine( folder, "FirstPath" ),
            UseGzipCompression = false
        };
        var c = new GrandOutputConfiguration().AddHandler( h );

        var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        GrandOutput g = new GrandOutput( c );
        await using( g.ConfigureAwait(false) )
        {
            g.EnsureGrandOutputClient( m );

            m.Trace( "No Compression." );
            // We must ensure that the log above will use the current configuration.
            // This is by design and is a good thing: there is no causality/ordering between log emission and sink reconfigurations.
            Thread.Sleep( 100 );

            h.UseGzipCompression = true;
            g.ApplyConfiguration( c, true );
            m.Trace( "With Compression." );
            Thread.Sleep( 100 );

            h.Path = Path.Combine( folder, "SecondPath" );
            g.ApplyConfiguration( c, true );
            m.Trace( "With Compression (in second folder)." );
            Thread.Sleep( 100 );

            h.UseGzipCompression = false;
            g.ApplyConfiguration( c, true );
            m.Trace( "No Compression (in second folder)." );
        }
        // The files are sorted by name, that is by time: a folder is enumerated in the order of its file system
        // (by name on NTFS, in no order on Linux).
        // First file is NOT compressed, the second one is.
        var fileNamesFirst = Directory.EnumerateFiles( Path.Combine( folder, "FirstPath" ) ).Order( StringComparer.Ordinal ).ToList();
        fileNamesFirst.Count.ShouldBe( 2 );
        fileNamesFirst.ShouldBeInOrder().ShouldNotContain( s => s.EndsWith( ".tmp" ), "Temporary files have been closed." );
        File.ReadAllText( fileNamesFirst[0] ).ShouldContain( "No Compression." );
        File.ReadAllText( fileNamesFirst[1] ).ShouldNotContain( "With Compression.", "Cannot read it in clear text since it is compressed..." );
        using( var reader = LogReader.Open( fileNamesFirst[1] ) )
        {
            reader.MoveNext().ShouldBeTrue();
            reader.Current.Text.ShouldBe( "With Compression." );
        }
        // First file is compressed, not the second one.
        var fileNamesSecond = Directory.EnumerateFiles( Path.Combine( folder, "SecondPath" ) ).Order( StringComparer.Ordinal ).ToList();
        fileNamesSecond.Count.ShouldBe( 2 );
        fileNamesSecond.ShouldBeInOrder().ShouldNotContain( s => s.EndsWith( ".tmp" ), "Temporary files have been closed." );
        File.ReadAllText( fileNamesSecond[0] ).ShouldNotContain( "With Compression (in second folder).", "The fist file is compressed..." );
        // We restrict the log entries to the one of our monitor: this filters out the logs from the DispatcherSink.
        using( var reader = LogReader.Open( fileNamesSecond[0], filter: new LogReader.BaseEntryFilter( m.UniqueId ) ) )
        {
            reader.MoveNext().ShouldBeTrue();
            reader.Current.Text.ShouldBe( "With Compression (in second folder)." );
        }
        File.ReadAllText( fileNamesSecond[1] ).ShouldContain( "No Compression (in second folder)." );
    }
}
