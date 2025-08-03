using CK.AspNet.Tester;
using CK.Core;
using Shouldly;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CK.Monitoring.Hosting.Tests;


[TestFixture]
public partial class HostApplicationBuilderTests
{
    [TestCase( true )]
    [TestCase( false )]
    public async Task GrandOutput_MinimalFilter_configuration_works_Async( bool builderMonitorBeforeUseCKMonitoring )
    {
        // Disposes current GrandOutput.Default if any.
        var d = GrandOutput.Default;
        if( d != null ) await d.DisposeAsync();

        DemoSinkHandler.Reset();
        var config = new DynamicConfigurationSource();
        config["CK-Monitoring:GrandOutput:Handlers:CK.Monitoring.Hosting.Tests.DemoSinkHandler, CK.Monitoring.Hosting.Tests"] = "true";

        var builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings { DisableDefaults = true } );
        builder.Configuration.Sources.Add( config );

        IActivityMonitor? monitor = null;
        if( builderMonitorBeforeUseCKMonitoring ) monitor = builder.GetBuilderMonitor();
        builder.UseCKMonitoring();
        if( !builderMonitorBeforeUseCKMonitoring ) monitor = builder.GetBuilderMonitor();
        Throw.DebugAssert( monitor != null );

        monitor.ActualFilter.ShouldBe( LogFilter.Undefined, "Initially Undefined." );

        config["CK-Monitoring:GrandOutput:MinimalFilter"] = "Debug";

        System.Threading.Thread.Sleep( 200 );
        monitor.ActualFilter.ShouldBe( LogFilter.Debug, "First Debug applied." );

        config["CK-Monitoring:GrandOutput:MinimalFilter"] = "{Fatal,Debug}";
        System.Threading.Thread.Sleep( 200 );
        monitor.ActualFilter.ShouldBe( new LogFilter( LogLevelFilter.Fatal, LogLevelFilter.Debug ), "Explicit {Off,Debug} filter." );

        config["CK-Monitoring:GrandOutput:MinimalFilter"] = null!;
        System.Threading.Thread.Sleep( 200 );
        monitor.ActualFilter.ShouldBe( new LogFilter( LogLevelFilter.Fatal, LogLevelFilter.Debug ), "Null doesn't change anything." );

        // Restores the Debug level (we are on the GrandOutput.Default).
        config["CK-Monitoring:GrandOutput:MinimalFilter"] = "Debug";
        System.Threading.Thread.Sleep( 500 );

        var texts = DemoSinkHandler.LogEvents.OrderBy( e => e.LogTime ).Select( e => e.Text ).ToArray();
        texts.Where( e => e != null && e.StartsWith( "GrandOutput.Default configuration n°4." ) ).ShouldNotBeEmpty();
        texts.Where( e => e != null && e.StartsWith( "GrandOutput.Default configuration n°5." ) )
            .ShouldBeEmpty( "There has been the initial configuration (n°0) and 4 reconfigurations." );
    }

    [Test]
    public async Task Invalid_configurations_are_skipped_and_errors_go_to_the_current_handlers_Async()
    {
        DemoSinkHandler.Reset();
        var config = new DynamicConfigurationSource();
        config["CK-Monitoring:GrandOutput:Handlers:CK.Monitoring.Hosting.Tests.DemoSinkHandler, CK.Monitoring.Hosting.Tests"] = "true";

        var builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings { DisableDefaults = true } );
        builder.Configuration.Sources.Add( config );

        var app = builder.UseCKMonitoring()
                         .Build();
        await app.StartAsync();

        var m = new ActivityMonitor( "The topic!" );

        m.Info( "BEFORE" );
        config["CK-Monitoring:GrandOutput:Handlers:Invalid Handler"] = "true";
        m.Info( "AFTER" );

        await app.StopAsync();

        DemoSinkHandler.LogEvents.Select( e => e.Text ).ShouldContain( "Topic: The topic!" )
               .ShouldContain( "BEFORE" )
               .ShouldContain( "While applying dynamic configuration." )
               .ShouldContain( "AFTER" );
    }


    [Test]
    public async Task Configuration_changes_dont_stutter_Async()
    {
        DemoSinkHandler.Reset();
        var config = new DynamicConfigurationSource();
        config["CK-Monitoring:GrandOutput:Handlers:CK.Monitoring.Hosting.Tests.DemoSinkHandler, CK.Monitoring.Hosting.Tests"] = "true";

        var builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings { DisableDefaults = true } );
        builder.Configuration.Sources.Add( config );

        var app = builder.UseCKMonitoring()
                         .Build();
        await app.StartAsync();

        var m = new ActivityMonitor( "The starting topic!" );

        config["CK-Monitoring:GrandOutput:Handlers:Console"] = "true";

        await Task.Delay( 200 );

        m.Info( "DONE!" );

        await app.StopAsync();

        var texts = DemoSinkHandler.LogEvents.OrderBy( e => e.LogTime ).Select( e => e.Text ).Concatenate( System.Environment.NewLine );
        texts.ShouldContain( "GrandOutput.Default configuration n°0" )
               .ShouldContain( "GrandOutput.Default configuration n°1" )
               .ShouldNotContain( "GrandOutput.Default configuration n°2" )
               .ShouldContain( "DONE!" );
    }

    [Test]
    public async Task TagFilters_works_Async()
    {
        CKTrait Sql = ActivityMonitor.Tags.Register( "Sql" );
        CKTrait Machine = ActivityMonitor.Tags.Register( "Machine" );

        DemoSinkHandler.Reset();
        var config = new DynamicConfigurationSource();
        config["CK-Monitoring:GrandOutput:Handlers:CK.Monitoring.Hosting.Tests.DemoSinkHandler, CK.Monitoring.Hosting.Tests"] = "true";
        config["CK-Monitoring:GrandOutput:MinimalFilter"] = "Trace";
        config["CK-Monitoring:TagFilters:0:0"] = "Sql";
        config["CK-Monitoring:TagFilters:0:1"] = "Debug";
        config["CK-Monitoring:TagFilters:1:0"] = "Machine";
        config["CK-Monitoring:TagFilters:1:1"] = "Release!";

        var builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings { DisableDefaults = true } );
        builder.Configuration.Sources.Add( config );
        builder.UseCKMonitoring();

        var app = builder.UseCKMonitoring()
                         .Build();
        await app.StartAsync();

        var m = new ActivityMonitor();

        RunWithTagFilters( Sql, Machine, m );

        // Removing the TagFilters totally should keep the current filters.
        using( config.StartBatch() )
        {
            config.Remove( "CK-Monitoring:TagFilters:0:0" );
            config.Remove( "CK-Monitoring:TagFilters:0:1" );
            config.Remove( "CK-Monitoring:TagFilters:1:0" );
            config.Remove( "CK-Monitoring:TagFilters:1:1" );
        }

        await Task.Delay( 200 );

        RunWithTagFilters( Sql, Machine, m );

        config["CK-Monitoring:TagFilters:0:0"] = "Sql";
        config["CK-Monitoring:TagFilters:0:1"] = "Trace";

        await Task.Delay( 200 );

        m.Debug( Sql, "NOP! This is in Debug!" );
        m.Trace( Machine, "SHOW!" );
        m.Trace( Machine | Sql, "Yes again!" );
        m.Trace( "DONE!" );

        await app.StopAsync();

        var texts = DemoSinkHandler.LogEvents.OrderBy( e => e.LogTime ).Select( e => e.Text ).Concatenate( System.Environment.NewLine );
        texts.ShouldContain( "SHOW!" )
               .ShouldContain( "Yes again!" )
               .ShouldNotContain( "NOP! This is in Debug!" )
               .ShouldContain( "DONE!" );

        static void RunWithTagFilters( CKTrait Sql, CKTrait Machine, ActivityMonitor m )
        {
            m.Debug( Sql, "YES: Sql!" );
            m.Trace( Machine, "NOSHOW" );
            m.Trace( Machine | Sql, "Yes again!" );
            m.Trace( "DONE!" );

            System.Threading.Thread.Sleep( 200 );

            var texts = DemoSinkHandler.LogEvents.OrderBy( e => e.LogTime ).Select( e => e.Text ).Concatenate( System.Environment.NewLine );
            texts.ShouldContain( "YES: Sql!" )
                   .ShouldContain( "Yes again!" )
                   .ShouldNotContain( "NOSHOW" )
                   .ShouldContain( "DONE!" );

            DemoSinkHandler.Reset();
        }

        DemoSinkHandler.Reset();
    }


    [Test]
    public void finding_MailAlerter_handler_by_conventions()
    {
        // Define tag since this assembly doesn't depend on CK.Monitoring.MailAlerterHandler.
        var sendMailTag = ActivityMonitor.Tags.Register( "SendMail" );

        // Copy the assembly.
        var runningDir = new NormalizedPath( AppContext.BaseDirectory );
        var source = new NormalizedPath( AppContext.BaseDirectory.Replace( "CK.Monitoring.Hosting.Tests", "CK.Monitoring.MailAlerterHandler" ) )
                            .AppendPart( "CK.Monitoring.MailAlerterHandler.dll" );
        File.Copy( source, runningDir.AppendPart( "CK.Monitoring.MailAlerterHandler.dll" ), overwrite: true );

        var config = new DynamicConfigurationSource();
        config["CK-Monitoring:GrandOutput:Handlers:MailAlerter:Email"] = "test@test.com";

        var builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings { DisableDefaults = true } );
        builder.Configuration.Sources.Add( config );
        builder.UseCKMonitoring();

        var m = new ActivityMonitor();
        m.Info( sendMailTag, "Hello World!" );

        // The assembly has been loaded.
        var a = AppDomain.CurrentDomain.GetAssemblies().Single( a => a.GetName().Name == "CK.Monitoring.MailAlerterHandler" );
        var t = a.GetType( "CK.Monitoring.Handlers.MailAlerter" );
        Debug.Assert( t != null );
        var sent = (string?)t.GetField( "LastMailSent", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static )!.GetValue( null );
        sent.ShouldBe( "Hello World!" );

    }

    [Test]
    public void StaticGates_works()
    {
        AsyncLock.Gate.IsOpen.ShouldBeFalse();
        AsyncLock.Gate.HasDisplayName.ShouldBeTrue( "Otherwise it wouldn't be configurable." );
        AsyncLock.Gate.DisplayName.ShouldBe( "AsyncLock" );
        try
        {
            var config = new DynamicConfigurationSource();
            config["CK-Monitoring:GrandOutput:StaticGates"] = "AsyncLock";

            var builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings { DisableDefaults = true } );
            builder.Configuration.Sources.Add( config );
            builder.UseCKMonitoring();
            AsyncLock.Gate.IsOpen.ShouldBeTrue();
        }
        finally
        {
            AsyncLock.Gate.IsOpen = false;
        }
    }

    [Test]
    public async Task DotNetEventSources_works_Async()
    {
        DemoSinkHandler.Reset();

        System.Diagnostics.Tracing.EventLevel? current = DotNetEventSourceCollector.GetLevel( "System.Runtime", out var found );
        found.ShouldBeTrue();
        current.ShouldBeNull();
        try
        {
            var config = new DynamicConfigurationSource();
            config["CK-Monitoring:GrandOutput:DotNetEventSources"] = "System.Runtime:W(arning only W matters);Microsoft-Extensions-DependencyInjection:V";
            config["CK-Monitoring:GrandOutput:Handlers:CK.Monitoring.Hosting.Tests.DemoSinkHandler, CK.Monitoring.Hosting.Tests"] = "true";

            var builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings { DisableDefaults = true } );
            builder.Configuration.Sources.Add( config );

            var app = builder.UseCKMonitoring()
                             .Build();
            await app.StartAsync();

            var rtConf = DotNetEventSourceCollector.GetLevel( "System.Runtime", out _ );
            rtConf.ShouldBe( System.Diagnostics.Tracing.EventLevel.Warning );

            var diConf = DotNetEventSourceCollector.GetLevel( "Microsoft-Extensions-DependencyInjection", out _ );
            diConf.ShouldBe( System.Diagnostics.Tracing.EventLevel.Verbose );

            await app.StopAsync();

            var texts = DemoSinkHandler.LogEvents.OrderBy( e => e.LogTime ).Select( e => e.Text ).Concatenate( System.Environment.NewLine );
            texts.ShouldContain( "Applying .Net EventSource configuration: 'System.Runtime:W(arning only W matters);Microsoft-Extensions-DependencyInjection:V'." )
                   .ShouldContain( "[Microsoft-Extensions-DependencyInjection:7] EventName='ServiceProviderBuilt'" );
        }
        finally
        {
            DotNetEventSourceCollector.Disable( "System.Runtime" );
            DemoSinkHandler.Reset();
        }
    }

    [Test]
    public async Task MS_Logging_Adapter_works_Async()
    {
        // Disposes current GrandOutput.Default if any.
        var d = GrandOutput.Default;
        if( d != null ) await d.DisposeAsync();

        DemoSinkHandler.Reset();
        try
        {
            var config = new DynamicConfigurationSource();
            config["CK-Monitoring:GrandOutput:Handlers:CK.Monitoring.Hosting.Tests.DemoSinkHandler, CK.Monitoring.Hosting.Tests"] = "true";
            config["CK-Monitoring:GrandOutput:HandleDotNetLogs"] = "true";
            config["CK-Monitoring:GrandOutput:MinimalFilter"] = "Debug";

            var builder = Host.CreateEmptyApplicationBuilder( new HostApplicationBuilderSettings { DisableDefaults = true } );
            builder.Configuration.Sources.Add( config );

            builder.UseCKMonitoring();
            builder.Services.AddLogging();
            var host = builder.Build();
            System.Threading.Thread.Sleep( 200 );

            var logger = host.Services.GetRequiredService<ILogger<HostApplicationBuilderTests>>();
            logger.LogInformation( "Hello world (MS.Extensions.Logging)" );
            System.Threading.Thread.Sleep( 200 );

            var texts = DemoSinkHandler.LogEvents.OrderBy( e => e.LogTime ).Select( e => e.Text ).ToArray();
            texts.ShouldContain( "[CK.Monitoring.Hosting.Tests.HostApplicationBuilderTests] Hello world (MS.Extensions.Logging)" );
        }
        finally
        {
            DemoSinkHandler.Reset();
        }
    }
}
