using CK.Core;
using CK.Monitoring.Hosting;
using CK.Monitoring;
using System.Collections.Generic;
using System;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Adds extension methods on the <see cref="HostApplicationBuilder"/> of a host (and <see cref="GetBuilderMonitor"/> on any
/// <see cref="IHostApplicationBuilder"/>).
/// <para>
/// They apply to the builder of a host only: a builder created inside a running host (the web application of an endpoint
/// for instance) must not reconfigure the <see cref="GrandOutput.Default"/>.
/// </para>
/// </summary>
public static class HostApplicationBuilderMonitoringExtensions
{
    /// <summary>
    /// Gets an activity monitor for this builder.
    /// <para>
    /// This can always be called: logs in this monitor are retained and emitted once the code injected by <see cref="UseCKMonitoring(HostApplicationBuilder)"/>
    /// is executed during <see cref="HostApplicationBuilder.Build()"/>.
    /// </para>
    /// </summary>
    /// <param name="builder">This builder.</param>
    /// <returns>A monitor for the host and application builder.</returns>
    public static IActivityMonitor GetBuilderMonitor( this IHostApplicationBuilder builder )
    {
        var monitor = (IActivityMonitor?)builder.Properties.GetValueOrDefault( typeof( IActivityMonitor ) );
        if( monitor == null )
        {
            bool hasInitializer = builder.Properties.ContainsKey( typeof( GrandOutputConfigurator ) );
            monitor = hasInitializer
                        ? new ActivityMonitor( nameof( IHostApplicationBuilder ) )
                        : new ActivityMonitor( ActivityMonitorOptions.WithInitialReplay, nameof( IHostApplicationBuilder ) );
            builder.Properties[typeof( IActivityMonitor )] = monitor;
        }
        return monitor;
    }

    sealed class AutoConfigurators : List<Action<IActivityMonitor, HostApplicationBuilder>> { }

    /// <summary>
    /// Memorizes a configuration action. These actions will be applied in the same order by <see cref="ApplyAutoConfigure"/>.
    /// </summary>
    /// <param name="builder">This builder.</param>
    /// <param name="configure">A configuration action.</param>
    /// <returns>This builder.</returns>
    public static HostApplicationBuilder AddAutoConfigure( this HostApplicationBuilder builder, Action<IActivityMonitor, HostApplicationBuilder> configure )
    {
        Throw.CheckNotNullArgument( configure );
        IDictionary<object, object> props = ((IHostApplicationBuilder)builder).Properties;
        if( props.TryGetValue( typeof( AutoConfigurators ), out var list ) )
        {
            ((AutoConfigurators)list).Add( configure );
        }
        else
        {
            props.Add( typeof( AutoConfigurators ), new AutoConfigurators { configure } );
        }
        return builder;
    }

    /// <summary>
    /// Applies all the memorized actions by <see cref="AddAutoConfigure"/> to this builder.
    /// <para>
    /// <see cref="CKBuild(HostApplicationBuilder)"/> calls this right before building the host.
    /// </para>
    /// <para>
    /// It is safe to call this multiple times: only the first call applies the configurations.
    /// </para>
    /// </summary>
    /// <param name="builder">This builder.</param>
    public static void ApplyAutoConfigure( this HostApplicationBuilder builder )
    {
        var monitor = builder.GetBuilderMonitor();
        IDictionary<object, object> props = ((IHostApplicationBuilder)builder).Properties;
        if( props.TryGetValue( typeof( AutoConfigurators ), out var list ) )
        {
            var actions = (AutoConfigurators)list;
            using( monitor.OpenInfo( $"Applying {actions.Count} Auto configuration actions." ) )
            {
                try
                {
                    foreach( var c in actions ) c( monitor, builder );
                    props.Remove( typeof( AutoConfigurators ) );
                }
                catch( Exception ex )
                {
                    monitor.Error( "While applying auto configuration actions.", ex );
                    throw;
                }
            }
        }
        else
        {
            monitor.Info( "No Auto configuration actions to apply." );
        }
    }

    /// <summary>
    /// Wraps the call to <see cref="HostApplicationBuilder.Build"/> by calling <see cref="ApplyAutoConfigure"/>
    /// before building the host.
    /// </summary>
    /// <param name="builder">This builder.</param>
    /// <returns>An initialized <see cref="IHost"/>.</returns>
    public static IHost CKBuild( this HostApplicationBuilder builder )
    {
        ApplyAutoConfigure( builder );
        return builder.Build();
    }

    /// <summary>
    /// Initializes the <see cref="GrandOutput.Default"/> from the <see cref="IHostApplicationBuilder.Configuration"/> "CK-Monitoring" section.
    /// <para>
    /// <see cref="GetBuilderMonitor(IHostApplicationBuilder)">IHostApplicationBuilder.GetBuilderMonitor()</see> can be called anytime (before
    /// this method is called): logs to this builder monitor will automatically be transferred to the GrandOutput once it is setup.
    /// </para>
    /// <para>
    /// This can safely be called multiple times on the same <paramref name="builder"/> but this should not be used
    /// on a host created inside an already running application (this would reconfigure the GrandOutput.Default).
    /// </para>
    /// <para>
    /// Note that NO registrations is done for IActivityMonitor. The standard registration is:
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
    public static HostApplicationBuilder UseCKMonitoring( this HostApplicationBuilder builder )
    {
        var t = typeof( GrandOutputConfigurator );
        IDictionary<object, object> props = ((IHostApplicationBuilder)builder).Properties;
        if( !props.ContainsKey( t ) )
        {
            props.Add( t, t );
            _ = new GrandOutputConfigurator( builder, isDefaultGrandOutput: true );
        }
        return builder;
    }

    /// <summary>
    /// Initializes an independent <see cref="GrandOutput"/> from the <see cref="IHostApplicationBuilder.Configuration"/> "CK-Monitoring" section.
    /// This GrandOuput will be automatically disposed when calling <see cref="IHost.StopAsync(System.Threading.CancellationToken)"/>.
    /// <para>
    /// This is for advanced scenario. <see cref="UseCKMonitoring(HostApplicationBuilder)"/> should almost always be used.
    /// </para>
    /// </summary>
    /// <param name="builder">Host builder</param>
    /// <param name="grandOutput">The new GrandOutput instance or the already existing one.</param>
    /// <returns>The builder.</returns>
    public static HostApplicationBuilder UseCKMonitoringWithIndependentGrandOutput( this HostApplicationBuilder builder, out GrandOutput grandOutput )
    {
        var t = typeof( GrandOutput );
        IDictionary<object, object> props = ((IHostApplicationBuilder)builder).Properties;
        if( !props.TryGetValue( t, out var oGrandOutput ) )
        {
            var c = new GrandOutputConfigurator( builder, isDefaultGrandOutput: false );
            props.Add( t, oGrandOutput = c.GrandOutputTarget );
        }
        grandOutput = (GrandOutput)oGrandOutput;
        return builder;
    }

}
