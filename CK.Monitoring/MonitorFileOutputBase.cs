using System;
using System.Collections.Generic;
using System.IO;
using CK.Core;
using System.IO.Compression;
using System.Diagnostics;

namespace CK.Monitoring;

/// <summary>
/// Helper class that encapsulates temporary stream and final renaming for log entries streams.
/// This currently handles only the maximum count of entries per file but this may be extended with options like "SubFolderMode" that can be based 
/// on current time (to group logs inside timed intermediate folders like one per day: 2014/01/12 or 2014-01/12, etc.). 
/// </summary>
public class MonitorFileOutputBase : IDisposable
{
    readonly string _configPath;
    readonly string _fileNameSuffix;
    readonly bool _useGzipCompression;

    int _maxCountPerFile;
    string? _basePath;
    FileStream? _output;
    DateTime _openedTimeUtc;
    int _countRemainder;
    readonly int _fileBufferSize;

    /// <summary>
    /// Initializes a new file for <see cref="IFullLogEntry"/>: the final file name is based on <see cref="FileUtil.FileNameUniqueTimeUtcFormat"/> with a ".ckmon" extension.
    /// You must call <see cref="Initialize"/> before actually using this object.
    /// </summary>
    /// <param name="configuredPath">The path: it can be absolute and when relative, it will be under <see cref="LogFile.RootLogPath"/> (that must be set).</param>
    /// <param name="fileNameSuffix">Suffix of the file including its extension. Must not be null nor empty.</param>
    /// <param name="maxCountPerFile">Maximum number of entries per file. Must be greater than 1.</param>
    /// <param name="useGzipCompression">True to gzip the file.</param>
    protected MonitorFileOutputBase( string configuredPath, string fileNameSuffix, int maxCountPerFile, bool useGzipCompression )
    {
        Throw.CheckNotNullArgument( configuredPath );
        Throw.CheckNotNullOrEmptyArgument( fileNameSuffix );
        Throw.CheckOutOfRangeArgument( maxCountPerFile > 0 );
        _configPath = configuredPath;
        _maxCountPerFile = maxCountPerFile;
        _fileNameSuffix = fileNameSuffix;
        _fileBufferSize = 4096;
        _useGzipCompression = useGzipCompression;
    }

    /// <summary>
    /// Initializes a new file for <see cref="IBaseLogEntry"/> issued from a specific monitor: the final file name is 
    /// based on <see cref="FileUtil.FileNameUniqueTimeUtcFormat"/> with a "-{XXX...XXX}.ckmon" suffix where {XXX...XXX} is the unique identifier (Guid with the B format - 32 digits separated by 
    /// hyphens, enclosed in braces) of the monitor.
    /// You must call <see cref="Initialize"/> before actually using this object.
    /// </summary>
    /// <param name="configuredPath">The path. Can be absolute. When relative, it will be under <see cref="LogFile.RootLogPath"/> that must be set.</param>
    /// <param name="monitorId">Monitor identifier.</param>
    /// <param name="maxCountPerFile">Maximum number of entries per file. Must be greater than 1.</param>
    /// <param name="useGzipCompression">True to gzip the file.</param>
    protected MonitorFileOutputBase( string configuredPath, Guid monitorId, int maxCountPerFile, bool useGzipCompression )
        : this( configuredPath, '-' + monitorId.ToString( "B" ), maxCountPerFile, useGzipCompression )
    {
    }

    /// <summary>
    /// Computes the root path.
    /// </summary>
    /// <param name="m">A monitor (must not be null).</param>
    /// <returns>The final path to use (ends with '\'). Null if unable to compute the path.</returns>
    string? ComputeBasePath( IActivityMonitor m )
    {
        string? rootPath = null;
        if( String.IsNullOrWhiteSpace( _configPath ) ) m.Error( "The configured path is empty." );
        else if( FileUtil.IndexOfInvalidPathChars( _configPath ) >= 0 ) m.Error( $"The configured path '{_configPath}' is invalid." );
        else
        {
            rootPath = _configPath;
            if( !Path.IsPathRooted( rootPath ) )
            {
                string? rootLogPath = LogFile.RootLogPath;
                if( String.IsNullOrWhiteSpace( rootLogPath ) ) m.Error( $"The relative path '{_configPath}' requires that LogFile.RootLogPath be specified." );
                else rootPath = Path.Combine( rootLogPath, _configPath );
            }
        }
        return rootPath != null ? FileUtil.NormalizePathSeparator( rootPath, true ) : null;
    }

    /// <summary>
    /// Gets the maximum number of entries per file.
    /// </summary>
    public int MaxCountPerFile
    {
        get => _maxCountPerFile;
        set
        {
            if( _maxCountPerFile != value )
            {
                int alreadyWritten = _maxCountPerFile - _countRemainder;
                _maxCountPerFile = value;
                if( _output != null && alreadyWritten >= _maxCountPerFile )
                {
                    CloseCurrentFile();
                }
            }
        }
    }

    /// <summary>
    /// Checks whether this <see cref="MonitorFileOutputBase"/> is valid: its base path is successfully created.
    /// Can be called multiple times.
    /// </summary>
    /// <param name="monitor">Required monitor.</param>
    public bool Initialize( IActivityMonitor monitor )
    {
        if( _basePath != null ) return true;
        Throw.CheckNotNullArgument( monitor );
        string? b = ComputeBasePath( monitor );
        if( b != null )
        {
            try
            {
                Directory.CreateDirectory( b );
                _basePath = b;
                return true;
            }
            catch( Exception ex )
            {
                monitor.Error( ex );
            }
        }
        return false;
    }

    /// <summary>
    /// Gets whether this file is currently opened.
    /// </summary>
    public bool IsOpened => _output != null;

    /// <summary>
    /// Closes the file if it is currently opened.
    /// Does nothing otherwise.
    /// </summary>
    public void Close()
    {
        if( _output != null ) CloseCurrentFile();
    }

    /// <summary>
    /// This method must be called before any write: it calls <see cref="OpenNewFile"/> if needed.
    /// </summary>
    protected void BeforeWriteEntry()
    {
        if( _output == null ) OpenNewFile();
    }

    /// <summary>
    /// This method must be called after write: it closes and produces the final file
    /// if the current file is full.
    /// </summary>
    protected void AfterWriteEntry()
    {
        if( --_countRemainder == 0 )
        {
            CloseCurrentFile();
        }
    }

    /// <summary>
    /// Simply calls <see cref="Close"/>.
    /// </summary>
    public void Dispose()
    {
        Close();
    }

    /// <summary>
    /// Automatically deletes files that are older than the specified <paramref name="timeSpanToKeep"/>,
    /// and those that would make the cumulated file size exceed <paramref name="totalBytesToKeep"/>.
    /// </summary>
    /// <param name="monitor">The monitor to use when logging.</param>
    /// <param name="timeSpanToKeep">
    /// The minimum time during which log files should be kept.
    /// Log files within this span will never be deleted (even if they exceed <paramref name="totalBytesToKeep"/>).
    /// If zero, there is no "time to keep", only the size limit applies and totalBytesToKeep parameter MUST be positive.
    /// </param>
    /// <param name="totalBytesToKeep">
    /// The maximum total size in bytes of the log files.
    /// If zero, there is no size limit, only "time to keep" applies and all "old" files (<paramref name="timeSpanToKeep"/>
    /// MUST be positive) are deleted.
    /// </param>
    public void RunFileHousekeeping( IActivityMonitor monitor, TimeSpan timeSpanToKeep, long totalBytesToKeep )
    {
        if( _basePath == null ) return;
        if( timeSpanToKeep <= TimeSpan.Zero && totalBytesToKeep <= 0 )
        {
            throw new ArgumentException( $"Either {nameof( timeSpanToKeep )} or {nameof( totalBytesToKeep )} must be positive." );
        }

        var candidates = new List<KeyValuePair<DateTime, FileInfo>>();

        int preservedByDateCount = 0;
        long byteLengthOfPreservedByDate = 0;
        long totalByteLength = 0;
        DateTime minDate = DateTime.UtcNow - timeSpanToKeep;
        DirectoryInfo logDirectory = new DirectoryInfo( _basePath );
        foreach( FileInfo file in logDirectory.EnumerateFiles() )
        {
            // Temporary files are "T-" + <date> + _fileNameSuffix + ".tmp" (See OpenNewFile())
            if( file.Name.EndsWith( ".tmp" ) && file.Name.StartsWith( "T-" ) )
            {
                if( _output != null && _output.Name == file.FullName )
                {
                    // Skip currently-opened temporary file
                    continue;
                }
                var datePart = file.Name.AsSpan( 2 );
                if( FileUtil.TryMatchFileNameUniqueTimeUtcFormat( ref datePart, out DateTime d ) )
                {
                    if( d >= minDate )
                    {
                        ++preservedByDateCount;
                        byteLengthOfPreservedByDate += file.Length;
                    }
                    totalByteLength += file.Length;
                    candidates.Add( new KeyValuePair<DateTime, FileInfo>( d, file ) );
                }
            }
            // Final files are <date> + _fileNameSuffix (see CloseCurrentFile())
            else if( file.Name.EndsWith( _fileNameSuffix ) )
            {
                var datePart = file.Name.AsSpan();
                if( FileUtil.TryMatchFileNameUniqueTimeUtcFormat( ref datePart, out DateTime d ) )
                {
                    if( d >= minDate )
                    {
                        ++preservedByDateCount;
                        byteLengthOfPreservedByDate += file.Length;
                    }
                    totalByteLength += file.Length;
                    candidates.Add( new KeyValuePair<DateTime, FileInfo>( d, file ) );
                }
            }
        }
        int canBeDeletedCount = candidates.Count - preservedByDateCount;
        bool hasBytesOverflow = totalByteLength > totalBytesToKeep;
        if( canBeDeletedCount > 0 && hasBytesOverflow )
        {
            // Note: The comparer is a reverse comparer. The most RECENT log file is the FIRST.
            candidates.Sort( ( a, b ) => DateTime.Compare( b.Key, a.Key ) );
            candidates.RemoveRange( 0, preservedByDateCount );
            monitor.Debug( $"Considering {candidates.Count} log files to delete." );

            long totalFileSize = byteLengthOfPreservedByDate;
            foreach( var kvp in candidates )
            {
                var file = kvp.Value;
                totalFileSize += file.Length;
                if( totalFileSize > totalBytesToKeep )
                {
                    monitor.Trace( $"Deleting file {file.FullName} (housekeeping)." );
                    try
                    {
                        file.Delete();
                    }
                    catch( Exception ex )
                    {
                        monitor.Warn( $"Failed to delete file {file.FullName} (housekeeping).", ex );
                    }
                }
            }
        }
    }

    /// <summary>
    /// Opens a new file named "T-" + Unique-Timed-File-Utc + fileNameSuffix + ".tmp".
    /// </summary>
    /// <returns>The opened stream to write to.</returns>
    protected virtual Stream OpenNewFile()
    {
        _openedTimeUtc = DateTime.UtcNow;
        _output = FileUtil.CreateAndOpenUniqueTimedFile( _basePath + "T-", _fileNameSuffix + ".tmp", _openedTimeUtc, FileAccess.Write, FileShare.Read, _fileBufferSize, FileOptions.SequentialScan );
        _countRemainder = _maxCountPerFile;
        return _output;
    }

    /// <summary>
    /// Closes the currently opened file.
    /// </summary>
    protected virtual void CloseCurrentFile()
    {
        Debug.Assert( _output != null && _basePath != null );
        string fName = _output.Name;
        _output.Dispose();
        if( _countRemainder == _maxCountPerFile )
        {
            // No entries were written: we try to delete file.
            // If this fails, this is not an issue.
            try
            {
                File.Delete( fName );
            }
            catch( IOException )
            {
                // Forget it.
            }
        }
        else
        {
            if( _useGzipCompression )
            {
                const int bufferSize = 64 * 1024;
                using( var source = new FileStream( fName, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan | FileOptions.DeleteOnClose ) )
                using( var destination = FileUtil.CreateAndOpenUniqueTimedFile( _basePath, _fileNameSuffix, _openedTimeUtc, FileAccess.Write, FileShare.None, bufferSize, FileOptions.SequentialScan ) )
                {
                    using( GZipStream gZipStream = new GZipStream( destination, CompressionLevel.Optimal ) )
                    {
                        source.CopyTo( gZipStream, bufferSize );
                    }
                }
            }
            else
            {
                FileUtil.MoveToUniqueTimedFile( fName, _basePath, _fileNameSuffix, _openedTimeUtc );
            }
        }
        _output = null;
    }
}
