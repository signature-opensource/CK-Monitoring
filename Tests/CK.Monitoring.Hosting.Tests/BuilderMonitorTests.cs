using CK.AspNet.Tester;
using CK.Core;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Shouldly;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CK.Monitoring.Hosting.Tests;

[TestFixture]
public class BuilderMonitorTests
{
    [SetUp]
    public void InitializePath()
    {
        TestHelper.InitalizePaths();
        TestHelper.WaitForNoMoreAliveInputLogEntry();
    }

    [Test]
    public async Task BuilderMonitor_is_always_available_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "FromBuilderMonitor" );
        // Disposes current GrandOutput.Default if any.
        var d = GrandOutput.Default;
        if( d != null ) await d.DisposeAsync();

        // The GetBuilderMonitor() is available.
        var builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings { DisableDefaults = true } );
        builder.GetBuilderMonitor().Info( "[Before configure] This will eventually be logged!" );

        // And we configure the text log output...
        // This works because the BuilderMonitor retains and replays the logs received before the
        // GrandOutput and its handlers are made available.
        var config = new DynamicConfigurationSource();
        config["CK-Monitoring:GrandOutput:Handlers:TextFile:Path"] = "FromBuilderMonitor";
        builder.Configuration.Sources.Add( config );

        builder.UseCKMonitoring();
        GrandOutput.Default.ShouldNotBeNull( "UseCKMonitoring ensures the GrandOuput.Default." );

        builder.GetBuilderMonitor().Info( "After configure." );

        var app = builder.Build();
        await GrandOutput.Default.DisposeAsync();

        var text = TestHelper.FileReadAllText( Directory.EnumerateFiles( folder ).Single() );
        text.ShouldContain( "[Before configure] This will eventually be logged!" )
            .ShouldContain( "After configure." );

        // Just make sure that the replay doesn't leak.
        ActivityMonitorExternalLogData.AliveCount.ShouldBe( 0 );
        InputLogEntry.AliveCount.ShouldBe( 0 );
    }
}
