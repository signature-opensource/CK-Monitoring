using System;
using System.Threading.Tasks;
using CK.Core;

namespace CK.Monitoring.Handlers;

/// <summary>
/// Text file handler.
/// </summary>
public sealed class TextFile : IGrandOutputHandler
{
    static readonly CKTrait _metricsTag = ActivityMonitor.Tags.Register( "Metrics" );
    readonly MonitorTextFileOutput _file;
    TextFileConfiguration _config;
    int _countFlush;
    int _countHousekeeping;
    bool _shouldHandleMetrics;

    /// <summary>
    /// Initializes a new <see cref="TextFile"/> based on a <see cref="TextFileConfiguration"/>.
    /// </summary>
    /// <param name="config">The configuration.</param>
    public TextFile( TextFileConfiguration config )
    {
        _config = config ?? throw new ArgumentNullException( nameof( config ) );
        _file = new MonitorTextFileOutput( config.Path, config.MaxCountPerFile, false );
        _countFlush = _config.AutoFlushRate;
        _countHousekeeping = _config.HousekeepingRate;
        _shouldHandleMetrics = config.HandleMetrics;
    }

    /// <summary>
    /// Initialization of the handler: computes the path.
    /// </summary>
    /// <param name="m">The monitor to use.</param>
    public ValueTask<bool> ActivateAsync( IActivityMonitor m )
    {
        using( m.OpenTrace( $"Initializing TextFile handler (MaxCountPerFile = {_file.MaxCountPerFile})." ) )
        {
            _file.Initialize( m );
            return ValueTask.FromResult( true );
        }
    }

    /// <summary>
    /// Writes a log entry.
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    /// <param name="logEvent">The log entry.</param>
    public ValueTask HandleAsync( IActivityMonitor monitor, InputLogEntry logEvent )
    {
        if( _shouldHandleMetrics
            && logEvent.MonitorId == ActivityMonitor.StaticLogMonitorUniqueId
            && logEvent.Tags.Overlaps( _metricsTag ) )
        {

        }

        _file.Write( logEvent );
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Automatically flushes the file based on <see cref="TextFileConfiguration.AutoFlushRate"/>.
    /// </summary>
    /// <param name="m">The monitor to use.</param>
    /// <param name="timerSpan">Indicative timer duration.</param>
    public ValueTask OnTimerAsync( IActivityMonitor m, TimeSpan timerSpan )
    {
        // Don't really care of the overflow here.
        if( --_countFlush == 0 )
        {
            _file.Flush();
            _countFlush = _config.AutoFlushRate;
        }

        if( --_countHousekeeping == 0 )
        {
            _file.RunFileHousekeeping( m, _config.MinimumTimeSpanToKeep, _config.MaximumTotalKbToKeep * 1000L );
            _countHousekeeping = _config.HousekeepingRate;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Attempts to apply configuration if possible.
    /// The key is the <see cref="FileConfigurationBase.Path"/>: the <paramref name="c"/>
    /// must be a <see cref="TextFileConfiguration"/> with the exact same path
    /// for this reconfiguration to be applied.
    /// </summary>
    /// <param name="m">The monitor to use.</param>
    /// <param name="c">Configuration to apply.</param>
    /// <returns>True if the configuration applied.</returns>
    public ValueTask<bool> ApplyConfigurationAsync( IActivityMonitor m, IHandlerConfiguration c )
    {
        if( c is not TextFileConfiguration cF || cF.Path != _config.Path ) return ValueTask.FromResult( false );
        _config = cF;
        _file.MaxCountPerFile = cF.MaxCountPerFile;
        _file.Flush();
        _countFlush = _config.AutoFlushRate;
        _countHousekeeping = _config.HousekeepingRate;
        _shouldHandleMetrics = _config.HandleMetrics;
        return ValueTask.FromResult( true );
    }

    /// <summary>
    /// Closes the file if it is opened.
    /// </summary>
    /// <param name="m">The monitor to use to track activity.</param>
    public ValueTask DeactivateAsync( IActivityMonitor m )
    {
        m.Info( "Closing file for TextFile handler." );
        _file.Close();
        return ValueTask.CompletedTask;
    }

}
