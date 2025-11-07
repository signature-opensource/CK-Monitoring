using CK.Core;
using System.Diagnostics;
using System.IO;

namespace CK.Monitoring;

/// <summary>
/// Helper class that encapsulates temporary stream and final renaming for log entries streams.
/// This currently handles only the maximum count of entries per file but this may be extended with options like "SubFolderMode" that can be based 
/// on current time (to group logs inside timed intermediate folders like one per day: 2014/01/12 or 2014-01/12, etc.). 
/// </summary>
public class MonitorTextFileOutput : MonitorFileOutputBase
{
    readonly MulticastLogEntryTextBuilder _builder;
    StreamWriter? _writer;
    bool _canFlush;

    /// <summary>
    /// Initializes a new file for <see cref="IFullLogEntry"/>: the final file name is based on <see cref="FileUtil.FileNameUniqueTimeUtcFormat"/> with a ".log" extension.
    /// You must call <see cref="MonitorFileOutputBase.Initialize">Initialize</see> before actually using this object.
    /// </summary>
    /// <param name="configuredPath">The path: it can be absolute and when relative, it will be under <see cref="LogFile.RootLogPath"/> (that must be set).</param>
    /// <param name="maxCountPerFile">Maximum number of entries per file. Must be greater than 1.</param>
    /// <param name="useGzipCompression">True to gzip the file.</param>
    public MonitorTextFileOutput( string configuredPath, int maxCountPerFile, bool useGzipCompression )
        : base( configuredPath, ".log" + (useGzipCompression ? ".gzip" : string.Empty), maxCountPerFile, useGzipCompression )
    {
        _builder = new MulticastLogEntryTextBuilder( false, false );
    }

    /// <summary>
    /// Writes a log entry.
    /// </summary>
    /// <param name="e">The log entry.</param>
    public void Write( IFullLogEntry e )
    {
        BeforeWriteEntry();
        Debug.Assert( _writer != null );
        string formattedLines = _builder.FormatEntryString( e );
        _writer.WriteLine( formattedLines );
        _canFlush = true;
        AfterWriteEntry();
    }

    /// <summary>
    /// Flushes the file content if needed.
    /// </summary>
    public void Flush()
    {
        if( _canFlush )
        {
            Debug.Assert( _writer != null );
            _writer.Flush();
            _canFlush = false;
        }
    }

    /// <summary>
    /// Called when a new file is created.
    /// </summary>
    /// <returns>The created stream.</returns>
    protected override Stream OpenNewFile()
    {
        Stream s = base.OpenNewFile();
        _writer = new StreamWriter( s );
        _builder.Reset();
        return s;
    }

    /// <inheritdoc />
    protected override string? DoCloseCurrentFile( bool forgetCurrentFile = false )
    {
        Debug.Assert( _writer != null, "Checked by CloseFile." );
        if( !forgetCurrentFile ) _writer.Flush();
        _writer.Dispose();
        _writer = null;
        _canFlush = false;
        return base.DoCloseCurrentFile( forgetCurrentFile );
    }
}
