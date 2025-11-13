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
        Throw.CheckNotNullArgument( config );
        _config = config;
        var rootPath = config.Path;
        _file = new MonitorTextFileOutput( config.Path, config.MaxCountPerFile, false, config.TimedFolderMode.Enabled );
        _countFlush = config.AutoFlushRate;
        _countHousekeeping = config.HousekeepingRate;
        _shouldHandleMetrics = config.HandleMetrics;
    }

    /// <summary>
    /// Gets the <see cref="FileConfigurationBase.Path"/> that is the key to identify this handler among other file handlers.
    /// </summary>
    public string KeyPath => _config.Path;

    /// <summary>
    /// Initialization of the handler: initializes the file and runs TimedFolder cleanup if configured.
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    public ValueTask<bool> ActivateAsync( IActivityMonitor monitor )
    {
        using( monitor.OpenTrace( $"Initializing TextFile handler (MaxCountPerFile = {_file.MaxCountPerFile})." ) )
        {
            return ValueTask.FromResult( _file.Initialize( monitor ) && _file.RunTimedFolderCleanup( monitor, _config.TimedFolderMode ) );
        }
    }

    /// <summary>
    /// Writes a log entry.
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    /// <param name="logEvent">The log entry.</param>
    public ValueTask HandleAsync( IActivityMonitor monitor, InputLogEntry logEvent )
    {
        if( !_shouldHandleMetrics
            && logEvent.MonitorId == ActivityMonitor.StaticLogMonitorUniqueId
            && logEvent.Tags.Overlaps( _metricsTag ) )
        {
            return ValueTask.CompletedTask;
        }
        _file.Write( logEvent );
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Automatically flushes the file based on <see cref="TextFileConfiguration.AutoFlushRate"/>.
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    /// <param name="timerSpan">Indicative timer duration.</param>
    public ValueTask OnTimerAsync( IActivityMonitor monitor, TimeSpan timerSpan )
    {
        // Don't really care of the overflow here.
        if( --_countFlush == 0 )
        {
            _file.Flush();
            _countFlush = _config.AutoFlushRate;
        }

        if( --_countHousekeeping == 0 )
        {
            _file.RunFileHousekeeping( monitor, _config.MinimumTimeSpanToKeep, _config.MaximumTotalKbToKeep * 1000L );
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
    /// <param name="monitor">The monitor to use.</param>
    /// <param name="c">Configuration to apply.</param>
    /// <returns>True if the configuration applied.</returns>
    public ValueTask<bool> ApplyConfigurationAsync( IActivityMonitor monitor, IHandlerConfiguration c )
    {
        if( c is not TextFileConfiguration cF || cF.Path != _config.Path ) return ValueTask.FromResult( false );
        _config = cF;
        _file.Flush();
        _countFlush = _config.AutoFlushRate;
        _countHousekeeping = _config.HousekeepingRate;
        _shouldHandleMetrics = _config.HandleMetrics;
        return ValueTask.FromResult( _file.Reconfigure( monitor, maxCountPerFile: cF.MaxCountPerFile, timedFolderMode: cF.TimedFolderMode.Enabled ) );
    }

    /// <summary>
    /// Closes the file if it is opened.
    /// </summary>
    /// <param name="monitor">The monitor to use to track activity.</param>
    public ValueTask DeactivateAsync( IActivityMonitor monitor )
    {
        monitor.Info( "Closing file for TextFile handler." );
        _file.Close();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Closes the current file if it is opened and has at least one entry.
    /// Does nothing otherwise and returns null.
    /// <para>
    /// This is exposed to support potential <see cref="GrandOutputHandlersAction"/> (or <see cref="GrandOutputHandlersAction{TResult}"/>)
    /// that can be implemented to explicitly close (or forget) the current file.
    /// </para>
    /// </summary>
    /// <param name="forgetCurrentFile">
    /// Suppress the current file, forgetting its content.
    /// <para>
    /// This is to be used in very special scenarii!
    /// </para>
    /// </param>
    /// <returns>
    /// The full path of the closed file.
    /// Null if no file has been created because it would have been empty or <paramref name="forgetCurrentFile"/> is true.
    /// </returns>
    public string? CloseCurrentFile( bool forgetCurrentFile = false ) => _file.Close( forgetCurrentFile );
}
