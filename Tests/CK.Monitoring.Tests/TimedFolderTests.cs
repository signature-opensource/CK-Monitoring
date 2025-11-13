using CK.Core;
using NUnit.Framework;
using Shouldly;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CK.Monitoring.Tests;

[TestFixture]
public class TimedFolderTests
{
    [Test]
    public async Task TimedFolder_tests_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "TimedFolder" );

        // No handler inititially.
        await using GrandOutput g = new GrandOutput( new GrandOutputConfiguration() );

        // Directly add the TextFile handler.
        var textConf = new Handlers.TextFileConfiguration()
        {
            Path = "TimedFolder",
            MaxCountPerFile = 1,
            TimedFolderMode =
            {
                MaxCurrentLogFolderCount = 2,
                MaxArchivedLogFolderCount = 5,
            }
        };
        var textHandler = new Handlers.TextFile( textConf );
        (await g.Sink.AddHandlerAsync( textHandler )).ShouldBeTrue();

        // Creates a monitor and attaches it to the GrandOuput.
        var monitor = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        g.EnsureGrandOutputClient( monitor );
        // Logs 5 entries.
        for( int i = 0; i < 5; i++ )
        {
            monitor.Info( $"This the n°{i}" ); 
        }
        await g.Sink.SyncWaitAsync();
        var topFolders = Directory.GetDirectories( folder );
        topFolders.Length.ShouldBe( 1 );
        var oldest = topFolders[0];
        // One entry per file => 5 log files + logs of the initialization itself.
        Directory.GetFiles( oldest ).Length.ShouldBeGreaterThan( 5 );

        // Removes the handler and adds it back: this Deactivate/Activate it.
        (await g.Sink.RemoveHandlerAsync( textHandler )).ShouldBeTrue();
        (await g.Sink.AddHandlerAsync( textHandler )).ShouldBeTrue();

        monitor.Info( $"One more" );
        await g.Sink.SyncWaitAsync();
        topFolders = Directory.GetDirectories( folder );
        topFolders.Length.ShouldBe( 2, "2 TimedFolders." );

        // Deactivate/Activate the handler => MaxCurrentLogFolderCount = 2 kicks in
        // => "Archive/" is created. The oldest top folder is moved to it.
        (await g.Sink.RemoveHandlerAsync( textHandler )).ShouldBeTrue();
        (await g.Sink.AddHandlerAsync( textHandler )).ShouldBeTrue();

        monitor.Info( $"Creating the 3rd TimedFolder." );
        await g.Sink.SyncWaitAsync();
        topFolders = Directory.GetDirectories( folder );
        topFolders.Length.ShouldBe( 3, "3 TimedFolders." );

        var archivePath = topFolders.Single( p => p.EndsWith( "Archive" ) );
        var archiveFolders = Directory.GetDirectories( archivePath );
        archiveFolders.Length.ShouldBe( 1 );
        Path.GetFileName( archiveFolders[0] ).ShouldBe( Path.GetFileName( oldest ) );

        // The "Archive/" contains one file, let's create 4 new archive folders.
        for( int i = 0; i < 4; ++i )
        {
            (await g.Sink.RemoveHandlerAsync( textHandler )).ShouldBeTrue();
            (await g.Sink.AddHandlerAsync( textHandler )).ShouldBeTrue();
        }

        topFolders = Directory.GetDirectories( folder );
        topFolders.Length.ShouldBe( 3, "Still 3 TimedFolders (one Archive)." );
        topFolders.Single( p => p.EndsWith( "Archive" ) ).ShouldBe( archivePath );
        archiveFolders = Directory.GetDirectories( archivePath );
        archiveFolders.Length.ShouldBe( 5, "Now there is 5 files in Archive. There will never be more than MaxArchivedLogFolderCount = 5." );

        for( int i = 0; i < 4; ++i )
        {
            (await g.Sink.RemoveHandlerAsync( textHandler )).ShouldBeTrue();
            (await g.Sink.AddHandlerAsync( textHandler )).ShouldBeTrue();
        }

        archiveFolders = Directory.GetDirectories( archivePath );
        archiveFolders.Length.ShouldBe( 5, "Still 5." );
    }
}
