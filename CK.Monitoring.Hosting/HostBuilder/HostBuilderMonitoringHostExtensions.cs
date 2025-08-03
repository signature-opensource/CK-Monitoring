using CK.Monitoring.Hosting;
using CK.Core;
using CK.Monitoring;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;

namespace Microsoft.Extensions.Hosting;


/// <summary>
/// Adds extension methods on <see cref="IHostBuilder"/>.
/// </summary>
public static class HostBuilderMonitoringHostExtensions
{
    /// <summary>
    /// Gets an activity monitor for this builder.
    /// <para>
    /// This can always be called: logs in this monitor are retained and emitted once the code injected by <see cref="UseCKMonitoring(IHostBuilder)"/>
    /// is executed during <see cref="IHostBuilder.Build()"/>.  
    /// </para>
    /// </summary>
    /// <param name="builder">This builder.</param>
    /// <returns>A monitor for the host and application builder.</returns>
    [Obsolete( "Use IHostApplicationBuilder simplified API instead.", false )]
    public static IActivityMonitor GetBuilderMonitor( this IHostBuilder builder ) => GetBuilderMonitor( builder.Properties );

    /// <summary>
    /// Gets an activity monitor for this builder context.
    /// <para>
    /// This can always be called: logs in this monitor are retained and emitted once the code injected by <see cref="UseCKMonitoring(IHostBuilder)"/>
    /// is executed during <see cref="IHostBuilder.Build()"/>.  
    /// </para>
    /// </summary>
    /// <param name="ctx">This builder context.</param>
    /// <returns>A monitor for the host and application builder.</returns>
    [Obsolete( "Use IHostApplicationBuilder simplified API instead.", false )]
    public static IActivityMonitor GetBuilderMonitor( this HostBuilderContext ctx ) => GetBuilderMonitor( ctx.Properties );

    static IActivityMonitor GetBuilderMonitor( IDictionary<object, object> props )
    {
        var monitor = (IActivityMonitor?)props.GetValueOrDefault( typeof( IActivityMonitor ) );
        if( monitor == null )
        {
            monitor = new ActivityMonitor( ActivityMonitorOptions.WithInitialReplay | ActivityMonitorOptions.SkipAutoConfiguration, nameof( IHostBuilder ) );
            props[typeof( IActivityMonitor )] = monitor;
        }
        return monitor;
    }
    /// <summary>
    /// Initializes the <see cref="GrandOutput.Default"/> and bounds the configuration to the provided configuration section.
    /// This automatically registers a <see cref="IActivityMonitor"/> as a scoped service in the services.
    /// <para>
    /// This can safely be called multiple times.
    /// </para>
    /// <para>
    /// This should not be used on a host created inside an already running application (this would reconfigure the GrandOutput.Default).
    /// Instead, simply configure the application services with the IActivityMonitor registrations:
    /// <code>
    ///   // The ActivityMonitor is not mapped, only the IActivityMonitor must 
    ///   // be exposed and the ParallelLogger is the one of the monitor.
    ///   services.AddScoped&lt;IActivityMonitor, ActivityMonitor&gt;();
    ///   services.AddScoped(sp => sp.GetRequiredService&lt;IActivityMonitor&gt;().ParallelLogger );
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="builder">Host builder</param>
    /// <returns>The builder.</returns>
    [Obsolete( "Use IHostApplicationBuilder simplified API instead.", false )]
    public static IHostBuilder UseCKMonitoring( this IHostBuilder builder )
    {
        if( !builder.Properties.ContainsKey( typeof( GrandOutputConfigurationInitializer ) ) )
        {
            builder.Properties.Add( typeof( GrandOutputConfigurationInitializer ), null! );
            DoUseMonitoring( builder, null, c => c.GetSection( "CK-Monitoring" ) );
        }
        return builder;
    }

    /// <summary>
    /// Configures a <see cref="GrandOutput"/> instance that must not be null nor be the <see cref="GrandOutput.Default"/> and
    /// bounds the configuration from the given configuration section path.
    /// This automatically registers a <see cref="IActivityMonitor"/> as a scoped service in the services.
    /// </summary>
    /// <param name="builder">This Host builder</param>
    /// <param name="grandOutput">The target <see cref="GrandOutput"/>.</param>
    /// <param name="configurationPath">The path of the monitoring configuration in the global configuration.</param>
    /// <returns>The builder.</returns>
    [Obsolete( "Use IHostApplicationBuilder simplified API instead.", false )]
    public static IHostBuilder UseMonitoring( this IHostBuilder builder, GrandOutput grandOutput, string configurationPath )
    {
        Throw.CheckNotNullArgument( grandOutput );
        Throw.CheckArgument( grandOutput != GrandOutput.Default );
        return DoUseMonitoring( builder, grandOutput, c => c.GetSection( configurationPath ) );
    }

    /// <summary>
    /// Configures a <see cref="GrandOutput"/> instance that must not be null nor be the <see cref="GrandOutput.Default"/> and
    /// bounds the configuration from the given configuration section.
    /// This automatically registers a <see cref="IActivityMonitor"/> as a scoped service in the services.
    /// </summary>
    /// <param name="builder">This Web host builder.</param>
    /// <param name="grandOutput">The target <see cref="GrandOutput"/>.</param>
    /// <param name="section">The configuration section.</param>
    /// <returns>The builder.</returns>
    [Obsolete( "Use IHostApplicationBuilder simplified API instead.", false )]
    public static IHostBuilder UseMonitoring( this IHostBuilder builder, GrandOutput grandOutput, IConfigurationSection section )
    {
        Throw.CheckNotNullArgument( grandOutput );
        Throw.CheckArgument( grandOutput != GrandOutput.Default );
        Throw.CheckNotNullArgument( section );
        return DoUseMonitoring( builder, grandOutput, c => section );
    }

    [Obsolete( "Use IHostApplicationBuilder simplified API instead.", false )]
    static IHostBuilder DoUseMonitoring( IHostBuilder builder, GrandOutput? grandOutput, Func<IConfiguration, IConfigurationSection> configSection )
    {
        var initializer = new GrandOutputConfigurationInitializer( grandOutput );
        initializer.ConfigureBuilder( builder, configSection );
        return builder;
    }

}
