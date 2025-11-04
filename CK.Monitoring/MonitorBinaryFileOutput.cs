using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CK.Core;

namespace CK.Monitoring;

/// <summary>
/// Helper class that encapsulates temporary stream and final renaming for log entries streams.
/// This currently handles only the maximum count of entries per file but this may be extended with options like "SubFolderMode" that can be based 
/// on current time (to group logs inside timed intermediate folders like one per day: 2014/01/12 or 2014-01/12, etc.). 
/// </summary>
public class MonitorBinaryFileOutput : MonitorFileOutputBase
{
    CKBinaryWriter? _writer;

    /// <summary>
    /// Initializes a new file for <see cref="IFullLogEntry"/>: the final file name is based on <see cref="FileUtil.FileNameUniqueTimeUtcFormat"/> with a ".ckmon" extension.
    /// You must call <see cref="MonitorFileOutputBase.Initialize">Initialize</see> before actually using this object.
    /// </summary>
    /// <param name="configuredPath">The path: it can be absolute and when relative, it will be under <see cref="LogFile.RootLogPath"/> (that must be set).</param>
    /// <param name="maxCountPerFile">Maximum number of entries per file. Must be greater than 1.</param>
    /// <param name="useGzipCompression">True to gzip the file.</param>
    public MonitorBinaryFileOutput( string configuredPath, int maxCountPerFile, bool useGzipCompression )
        : base( configuredPath, ".ckmon", maxCountPerFile, useGzipCompression )
    {
    }

    /// <summary>
    /// Initializes a new file for <see cref="IBaseLogEntry"/> issued from a specific monitor: the final file name is 
    /// based on <see cref="FileUtil.FileNameUniqueTimeUtcFormat"/> with a "-{XXX...XXX}.ckmon" suffix where {XXX...XXX} is the unique identifier (Guid with the B format - 32 digits separated by 
    /// hyphens, enclosed in braces) of the monitor.
    /// You must call <see cref="MonitorFileOutputBase.Initialize">Initialize</see> before actually using this object.
    /// </summary>
    /// <param name="configuredPath">The path. Can be absolute. When relative, it will be under <see cref="LogFile.RootLogPath"/> that must be set.</param>
    /// <param name="monitorId">Monitor identifier.</param>
    /// <param name="maxCountPerFile">Maximum number of entries per file. Must be greater than 1.</param>
    /// <param name="useGzipCompression">True to gzip the file.</param>
    public MonitorBinaryFileOutput( string configuredPath, string monitorId, int maxCountPerFile, bool useGzipCompression )
        : base( configuredPath, '-' + monitorId + ".ckmon", maxCountPerFile, useGzipCompression )
    {
    }

    #region Write methods

    /// <summary>
    /// Writes a log entry (that can actually be a <see cref="IFullLogEntry"/>).
    /// </summary>
    /// <param name="e">The log entry.</param>
    public void Write( IBaseLogEntry e )
    {
        BeforeWriteEntry();
        Debug.Assert( _writer != null );
        e.WriteLogEntry( _writer );
        AfterWriteEntry();
    }

    /// <summary>
    /// Writes a line entry as a <see cref="IFullLogEntry"/>.
    /// </summary>
    /// <param name="data">The log line.</param>
    /// <param name="adapter">Multi-cast information to be able to write multi-cast entry when needed.</param>
    public void WriteLineEntry( ActivityMonitorLogData data, IFullLogInfo adapter )
    {
        BeforeWriteEntry();
        Debug.Assert( _writer != null );
        LogEntry.WriteLog( _writer, adapter.GrandOutputId, data.MonitorId, adapter.PreviousEntryType, adapter.PreviousLogTime, data.Depth, false, data.Level, data.LogTime, data.Text, data.Tags, data.ExceptionData, data.FileName, data.LineNumber );
        AfterWriteEntry();
    }

    /// <summary>
    /// Writes a group opening entry as a <see cref="IFullLogEntry"/>.
    /// </summary>
    /// <param name="g">The group line.</param>
    /// <param name="adapter">Multi-cast information to be able to write multi-cast entry when needed.</param>
    public void WriteOpenGroupEntry( IActivityLogGroup g, IFullLogInfo adapter )
    {
        BeforeWriteEntry();
        Debug.Assert( _writer != null );
        LogEntry.WriteLog( _writer, adapter.GrandOutputId, g.Data.MonitorId, adapter.PreviousEntryType, adapter.PreviousLogTime, g.Data.Depth, true, g.Data.Level, g.Data.LogTime, g.Data.Text, g.Data.Tags, g.Data.ExceptionData, g.Data.FileName, g.Data.LineNumber );
        AfterWriteEntry();
    }

    /// <summary>
    /// Writes a group closing entry as a <see cref="IFullLogEntry"/>.
    /// </summary>
    /// <param name="g">The group.</param>
    /// <param name="conclusions">Group's conclusions.</param>
    /// <param name="adapter">Information to be able to write full entry.</param>
    public void WriteCloseGroupEntry( IActivityLogGroup g, IReadOnlyList<ActivityLogGroupConclusion>? conclusions, IFullLogInfo adapter )
    {
        BeforeWriteEntry();
        Debug.Assert( _writer != null );
        LogEntry.WriteCloseGroup( _writer, adapter.GrandOutputId, g.Data.MonitorId, adapter.PreviousEntryType, adapter.PreviousLogTime, g.Data.Depth, g.Data.Level, g.CloseLogTime, conclusions );
        AfterWriteEntry();
    }

    #endregion

    /// <summary>
    /// Called when a new file is created.
    /// </summary>
    /// <returns>The created stream.</returns>
    protected override Stream OpenNewFile()
    {
        Stream s = base.OpenNewFile();
        _writer = new CKBinaryWriter( s );
        _writer.Write( LogReader.FileHeader );
        _writer.Write( LogReader.CurrentStreamVersion );
        return s;
    }

    /// <summary>
    /// Called when the current file is closed.
    /// </summary>
    /// <returns>The full path of the closed file. Null if no file has been created because it would have been empty.</returns>
    protected override string? CloseCurrentFile()
    {
        Throw.CheckState( _writer != null );
        _writer.Write( (byte)0 );
        var closedFilePath = base.CloseCurrentFile();
        _writer.Dispose();
        _writer = null;
        return closedFilePath;
    }
}
