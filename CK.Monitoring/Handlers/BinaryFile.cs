using CK.Core;
using System;
using System.Threading.Tasks;

namespace CK.Monitoring.Handlers;

/// <summary>
/// Binary file handler.
/// </summary>
public class BinaryFile : IGrandOutputHandler
{
    MonitorBinaryFileOutput _file;
    BinaryFileConfiguration _config;
    int _countHousekeeping;

    /// <summary>
    /// Initializes a new <see cref="BinaryFile"/> bound to its <see cref="BinaryFileConfiguration"/>.
    /// </summary>
    /// <param name="config">The configuration.</param>
    public BinaryFile( BinaryFileConfiguration config )
    {
        Throw.CheckNotNullArgument( config );
        _file = new MonitorBinaryFileOutput( config.Path, config.MaxCountPerFile, config.UseGzipCompression );
        _config = config;
        _countHousekeeping = _config.HousekeepingRate;
    }

    /// <summary>
    /// Gets the <see cref="FileConfigurationBase.Path"/> that is the key to identify this handler among other file handlers.
    /// </summary>
    public string KeyPath => _config.Path;

    /// <summary>
    /// Initialization of the handler: computes the path.
    /// </summary>
    /// <param name="monitor"></param>
    public ValueTask<bool> ActivateAsync( IActivityMonitor monitor )
    {
        Throw.CheckNotNullArgument( monitor );
        using( monitor.OpenTrace( $"Initializing BinaryFile handler (MaxCountPerFile = {_file.MaxCountPerFile})." ) )
        {
            return ValueTask.FromResult( _file.Initialize( monitor ) );
        }
    }

    /// <summary>
    /// Writes a log entry.
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    /// <param name="logEvent">The log entry.</param>
    public ValueTask HandleAsync( IActivityMonitor monitor, InputLogEntry logEvent )
    {
        _file.Write( logEvent );
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Does nothing since files are automatically managed (relies on <see cref="FileConfigurationBase.MaxCountPerFile"/>).
    /// </summary>
    /// <param name="m">The monitor to use.</param>
    /// <param name="timerSpan">Indicative timer duration.</param>
    public ValueTask OnTimerAsync( IActivityMonitor m, TimeSpan timerSpan )
    {
        if( --_countHousekeeping == 0 )
        {
            _file.RunFileHousekeeping( m, _config.MinimumTimeSpanToKeep, _config.MaximumTotalKbToKeep * 1000L );
            _countHousekeeping = _config.HousekeepingRate;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Attempts to apply configuration if possible.
    /// </summary>
    /// <param name="m">The monitor to use.</param>
    /// <param name="c">Configuration to apply.</param>
    /// <returns>True if the configuration applied.</returns>
    public ValueTask<bool> ApplyConfigurationAsync( IActivityMonitor m, IHandlerConfiguration c )
    {
        if( c is not BinaryFileConfiguration cF || cF.Path != _config.Path ) return ValueTask.FromResult( false );

        if( _config.UseGzipCompression != cF.UseGzipCompression )
        {
            var f = new MonitorBinaryFileOutput( _config.Path, cF.MaxCountPerFile, cF.UseGzipCompression );
            // If the initialization of the new file fails (should not happen), we fail to apply the configuration:
            // this handler will be Deactivated and another one will be created... and it may work. Who knows...
            if( !f.Initialize( m ) ) return ValueTask.FromResult( false );
            _file.Close();
            _file = f;
        }
        else
        {
            _file.MaxCountPerFile = cF.MaxCountPerFile;
        }
        _config = cF;
        return ValueTask.FromResult( true );
    }

    /// <summary>
    /// Closes the file if it is opened.
    /// </summary>
    /// <param name="m">The monitor to use to track activity.</param>
    public ValueTask DeactivateAsync( IActivityMonitor m )
    {
        m.Info( "Closing file for BinaryFile handler." );
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
