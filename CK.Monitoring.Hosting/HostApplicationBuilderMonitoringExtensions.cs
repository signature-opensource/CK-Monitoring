using CK.Core;
using CK.Monitoring.Hosting;
using CK.Monitoring;
using System.Collections.Generic;
using System;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Adds extension methods on <see cref="IHostApplicationBuilder"/>.
/// </summary>
public static class HostApplicationBuilderMonitoringExtensions
{
    /// <summary>
    /// Gets an activity monitor for this builder.
    /// <para>
    /// This can always be called: logs in this monitor are retained and emitted once the code injected by <see cref="UseCKMonitoring{T}(T)"/>
    /// is executed during <see cref="IHostBuilder.Build()"/>.  
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

    sealed class AutoConfigurators<T> : List<Action<IActivityMonitor, T>> where T : IHostApplicationBuilder { }

    /// <summary>
    /// Memorizes a configuration action. These actions will be applied in the same order by <see cref="ApplyAutoConfigure"/>.
    /// </summary>
    /// <typeparam name="T">This builder type.</typeparam>
    /// <param name="builder">This builder.</param>
    /// <param name="configure">A configuration action.</param>
    /// <returns>This builder.</returns>
    public static T AddAutoConfigure<T>( this T builder, Action<IActivityMonitor, T> configure ) where T : IHostApplicationBuilder
    {
        Throw.CheckNotNullArgument( configure );
        if( builder.Properties.TryGetValue( typeof( AutoConfigurators<T> ), out var list ) )
        {
            ((AutoConfigurators<T>)list).Add( configure );
        }
        else
        {
            builder.Properties.Add( typeof( AutoConfigurators<T> ), new AutoConfigurators<T> { configure } );
        }
        return builder;
    }

    /// <summary>
    /// Applies all the memorized actions by <see cref="AddAutoConfigure"/> to this builder.
    /// <para>
    /// <see cref="CKBuild(HostApplicationBuilder)"/> (and CKBuild from CK.AspNet package helper for WebApplicationBuilder)
    /// call this right before building the host.
    /// </para>
    /// <para>
    /// It is safe to call this multiple times: only the first call applies the configurations.
    /// </para>
    /// </summary>
    /// <typeparam name="T">This builder type.</typeparam>
    /// <param name="builder">This builder.</param>
    public static void ApplyAutoConfigure<T>( this T builder ) where T : IHostApplicationBuilder
    {
        var monitor = builder.GetBuilderMonitor();
        if( builder.Properties.TryGetValue( typeof( AutoConfigurators<T> ), out var list ) )
        {
            var actions = (AutoConfigurators<T>)list;
            using( monitor.OpenInfo( $"Applying {actions.Count} Auto configuration actions." ) )
            {
                try
                {
                    foreach( var c in (AutoConfigurators<T>)list ) c( monitor, builder );
                    builder.Properties.Remove( typeof( AutoConfigurators<T> ) );
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
    /// <para>
    /// The package CK.AspNet exposes a similar CKBuild helper on WebApplicationBuilder (in Microsoft.AspNetCore package).
    /// </para>
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
    /// Note that if for <see cref="HostApplicationBuilder"/> the IActivityMonitor registration should be done, this is
    /// not the case for WebApplicationBuilder since the CKBuild from CK.AspNet package handles this.
    /// </para>
    /// </summary>
    /// <param name="builder">Host builder</param>
    /// <returns>The builder.</returns>
    public static T UseCKMonitoring<T>( this T builder ) where T : IHostApplicationBuilder
    {
        var t = typeof( GrandOutputConfigurator );
        if( !builder.Properties.ContainsKey( t ) )
        {
            builder.Properties.Add( t, t );
            _ = new GrandOutputConfigurator( builder, isDefaultGrandOutput: true );
        }
        return builder;
    }

    /// <summary>
    /// Initializes an independent <see cref="GrandOutput"/> from the <see cref="IHostApplicationBuilder.Configuration"/> "CK-Monitoring" section.
    /// This GrandOuput will be automatically disposed when calling <see cref="IHost.StopAsync(System.Threading.CancellationToken)"/>.
    /// <para>
    /// This is for advanced scenario. <see cref="UseCKMonitoring{T}(T)"/> should almost always be used.
    /// </para>
    /// </summary>
    /// <param name="builder">Host builder</param>
    /// <param name="grandOutput">The new GrandOutput instance or the already existing one.</param>
    /// <returns>The builder.</returns>
    public static T UseCKMonitoringWithIndependentGrandOutput<T>( this T builder, out GrandOutput grandOutput ) where T : IHostApplicationBuilder
    {
        var t = typeof( GrandOutput );
        if( !builder.Properties.TryGetValue( t, out var oGrandOutput ) )
        {
            var c = new GrandOutputConfigurator( builder, isDefaultGrandOutput: false );
            builder.Properties.Add( t, oGrandOutput = c.GrandOutputTarget );
        }
        grandOutput = (GrandOutput)oGrandOutput;
        return builder;
    }

}
