using CK.Core;
using CK.Monitoring.Handlers;
using CommunityToolkit.HighPerformance;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Xml.Linq;

namespace CK.Monitoring;

/// <summary>
/// Helper class that encapsulates temporary stream and final renaming for log entries streams.
/// This handles the maximum count of entries per file and a <see cref="Handlers.TimedFolderConfiguration"/>. 
/// </summary>
public class MonitorFileOutputBase : IDisposable
{
    const int _fileBufferSize = 4096;

    readonly string _configPath;
    readonly string? _lastRunFileName;
    string? _lastRunFilePath;

    string _fileNameSuffix;
    int _maxCountPerFile;
    bool _useGzipCompression;
    bool _timedFolderMode;
    bool _withLastRunSymLink;

    string? _rootPath;
    string? _basePath;
    FileStream? _output;
    DateTime _openedTimeUtc;
    int _countRemainder;

    // Memorizes the first attempt to create a Symbolic Link to the last closed log file.
    // Once true, no subsequent attempts are done.
    static bool _symLinkPrivilegeError;

    /// <summary>
    /// Initializes a new file for <see cref="IFullLogEntry"/>: the final file name is based on <see cref="FileUtil.FileNameUniqueTimeUtcFormat"/> with a ".ckmon" extension.
    /// You must call <see cref="Initialize"/> before actually using this object.
    /// </summary>
    /// <param name="configuredPath">The path: it can be absolute and when relative, it will be under <see cref="LogFile.RootLogPath"/> (that must be set).</param>
    /// <param name="fileNameSuffix">Suffix of the file including its extension. Must not be null nor empty.</param>
    /// <param name="maxCountPerFile">Maximum number of entries per file. Must be greater than 1.</param>
    /// <param name="useGzipCompression">True to gzip the file.</param>
    /// <param name="timedFolderMode">Whether a timed folder must be created under the <paramref name="configuredPath"/>.</param>
    /// <param name="withLastRunSymLink">True to handle the symbolic link "LastRun.log". <paramref name="lastRunFileName"/> must be specified.</param>
    /// <param name="lastRunFileName">Optional file name of the symbolic link "LastRun.log". When null, <see cref="WithLastRunSymLink"/> cannot be true.</param>
    protected MonitorFileOutputBase( string configuredPath,
                                     string fileNameSuffix,
                                     int maxCountPerFile,
                                     bool useGzipCompression,
                                     bool timedFolderMode,
                                     bool withLastRunSymLink,
                                     string? lastRunFileName )
    {
        Throw.CheckNotNullArgument( configuredPath );
        Throw.CheckNotNullOrEmptyArgument( fileNameSuffix );
        Throw.CheckOutOfRangeArgument( maxCountPerFile > 0 );
        Throw.CheckOutOfRangeArgument( !withLastRunSymLink || !string.IsNullOrWhiteSpace( lastRunFileName ) );
        _configPath = configuredPath;
        _maxCountPerFile = maxCountPerFile;
        _fileNameSuffix = fileNameSuffix;
        _useGzipCompression = useGzipCompression;
        _timedFolderMode = timedFolderMode;
        _withLastRunSymLink = withLastRunSymLink;
        _lastRunFileName = lastRunFileName;
    }

    /// <summary>
    /// Reconfigures this file output.
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    /// <param name="fileNameSuffix">The new file name suffix if not null.</param>
    /// <param name="maxCountPerFile">The new maximal number of entries per file if not null.</param>
    /// <param name="useGzipCompression">The new GZip compression if not null.</param>
    /// <param name="timedFolderMode">The new TimedFolder mode if not null.</param>
    /// <param name="withLastRunSymLink">The new WithLastRunSymLink mode if not null.</param>
    /// <returns>True on success, false otherwise.</returns>
    public bool Reconfigure( IActivityMonitor monitor,
                             string? fileNameSuffix = null,
                             int? maxCountPerFile = null,
                             bool? useGzipCompression = null,
                             bool? timedFolderMode = null,
                             bool? withLastRunSymLink = null )
    {
        if( withLastRunSymLink.HasValue && withLastRunSymLink.Value != _withLastRunSymLink )
        {
            if( _lastRunFilePath == null )
            {
                Throw.InvalidOperationException( "Cannot set WithLastRunSymLink since this MonitorFileOutputBase cannot handle it." );
            }
            _withLastRunSymLink = withLastRunSymLink.Value;
            if( !_withLastRunSymLink )
            {
                try
                {
                    File.Delete( _lastRunFilePath );
                }
                catch( Exception ex )
                {
                    monitor.Warn( $"While deleting '{_lastRunFilePath}' symbolic link.", ex );
                }
            }
        }
        if( fileNameSuffix != null && _fileNameSuffix != fileNameSuffix )
        {
            Close();
            _fileNameSuffix = fileNameSuffix;
        }
        if( maxCountPerFile.HasValue && maxCountPerFile.Value != _maxCountPerFile )
        {
            int alreadyWritten = _maxCountPerFile - _countRemainder;
            _maxCountPerFile = maxCountPerFile.Value;
            if( _output != null && alreadyWritten >= _maxCountPerFile )
            {
                Close();
            }
        }
        if( useGzipCompression.HasValue && useGzipCompression.Value != _useGzipCompression )
        {
            Close();
            _useGzipCompression = useGzipCompression.Value;
        }
        if( timedFolderMode.HasValue && timedFolderMode.Value != _timedFolderMode )
        {
            Close();
            _timedFolderMode = timedFolderMode.Value;
            if( !DoInitialize( monitor ) )
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Uninitializes this file output.
    /// The current file is closed, <see cref="Initialize(IActivityMonitor)"/> must be called again.
    /// <para>
    /// This enables a file handler to be added/removed with <see cref="DispatcherSink.SubmitAddHandler(IGrandOutputHandler"/>
    /// and <see cref="DispatcherSink.SubmitRemoveHandler(IGrandOutputHandler)"/> transparently.
    /// </para>
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    public void Deactivate( IActivityMonitor monitor )
    {
        Close();
        _basePath = null;
    }

    /// <summary>
    /// Gets the maximum number of entries per file.
    /// </summary>
    public int MaxCountPerFile => _maxCountPerFile;

    /// <summary>
    /// Gets whether the final file is GZiped.
    /// </summary>
    public bool UseGzipCompression => _useGzipCompression;

    /// <summary>
    /// Gets whether log files are in a timed folder.
    /// </summary>
    public bool TimedFolderMode => _timedFolderMode;

    /// <summary>
    /// Gets whether this object is initialized.
    /// </summary>
    [MemberNotNullWhen( true, nameof( _rootPath ), nameof( _basePath ) )]
    public bool IsInitialized => _basePath != null;

    /// <summary>
    /// Gets whether this file is currently opened.
    /// </summary>
    public bool IsOpened => _output != null;

    /// <summary>
    /// Gets whether the "LastRun.log" symbolic link is handled.
    /// </summary>
    public bool WithLastRunSymLink => _withLastRunSymLink;

    /// <summary>
    /// Gets the root path that is the result of the <see cref="LogFile.RootLogPath"/> and <see cref="FileConfigurationBase.Path"/>.
    /// This is not null after the first successful call to <see cref="Initialize(IActivityMonitor)"/> and remains unchanged
    /// across multiple calls to <see cref="Deactivate(IActivityMonitor)"/> and Initialize.
    /// </summary>
    public string? RootPath => _rootPath;

    /// <summary>
    /// Gets the base path of the log files. In regular mode, it is the same as the <see cref="RootPath"/> but in
    /// <see cref="TimedFolderMode"/>, this is the Timed folder in RootPath.
    /// <para>
    /// This is not null after the first successful call to <see cref="Initialize(IActivityMonitor)"/>. This can be changed
    /// by calls to <see cref="Reconfigure"/> when time folder mode changes.
    /// </para>
    /// </summary>
    public string? BasePath => _basePath;

    /// <summary>
    /// Checks whether this <see cref="MonitorFileOutputBase"/> is valid: its base path is successfully created.
    /// Can be called multiple times: will do nothing unless <see cref="Deactivate(IActivityMonitor)"/> is called.
    /// </summary>
    /// <param name="monitor">Required monitor.</param>
    public bool Initialize( IActivityMonitor monitor )
    {
        if( _basePath != null ) return true;
        Throw.CheckNotNullArgument( monitor );
        return DoInitialize( monitor );
    }

    bool DoInitialize( IActivityMonitor monitor )
    {
        // Computes the root path only once.
        if( _rootPath == null )
        {
            _rootPath = ComputeRootPath( monitor, _configPath );
            if( _rootPath == null ) return false;
            if( _lastRunFileName != null )
            {
                _lastRunFilePath = _rootPath + _lastRunFileName;
            }
        }
        if( _timedFolderMode )
        {
            // Even if we call DoInitialize multiple times in TimedFolder mode, keeps
            // the current, initial, folder name if it is set to a folder that is not the root path.
            if( _basePath == null || _basePath == _rootPath )
            {
                _basePath = _rootPath + DateTime.UtcNow.ToString( FileUtil.FileNameUniqueTimeUtcFormat ) + Path.DirectorySeparatorChar;
            }
        }
        else
        {
            // No TimedFolder mode: the base path is the root path.
            _basePath = _rootPath;
        }
        try
        {
            Directory.CreateDirectory( _basePath );
            return true;
        }
        catch( Exception ex )
        {
            monitor.Error( ex );
        }
        return false;

        static string? ComputeRootPath( IActivityMonitor monitor, string configPath )
        {
            string? rootPath = null;
            if( String.IsNullOrWhiteSpace( configPath ) ) monitor.Error( "The configured path is empty." );
            else if( FileUtil.IndexOfInvalidPathChars( configPath ) >= 0 ) monitor.Error( $"The configured path '{configPath}' is invalid." );
            else
            {
                rootPath = configPath;
                if( !Path.IsPathRooted( rootPath ) )
                {
                    string? rootLogPath = LogFile.RootLogPath;
                    if( String.IsNullOrWhiteSpace( rootLogPath ) ) monitor.Error( $"The relative path '{configPath}' requires that LogFile.RootLogPath be specified." );
                    else
                    {
                        rootPath = Path.Combine( rootLogPath, configPath );
                    }
                }
            }
            return rootPath != null ? FileUtil.NormalizePathSeparator( rootPath, true ) : null;
        }
    }

    /// <summary>
    /// Applies the <see cref="TimedFolderConfiguration.MaxCurrentLogFolderCount"/> and <see cref="TimedFolderConfiguration.MaxArchivedLogFolderCount"/>.
    /// Does nothing if <see cref="TimedFolderConfiguration.Enabled"/> is false.
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    /// <param name="c">The TimedFolder configuration to apply.</param>
    /// <returns>True on success, false on error.</returns>
    public bool RunTimedFolderCleanup( IActivityMonitor monitor, TimedFolderConfiguration c )
    {
        Throw.CheckState( IsInitialized );
        if( c.Enabled )
        {
            // Note: The comparer is a reverse comparer. The most RECENT timed folder is the FIRST.
            GetTimedFolders( _rootPath, _basePath, out SortedDictionary<DateTime, string> timedFolders, out string? archivePath );
            // We use >= here and we skip MaxCurrentLogFolderCount - 1 because the current _basePath is skipped by the GetTimedFolders
            // here: we are sure that the existing folders here are candidates to be archived and we let the current folder where it is.
            if( timedFolders.Count >= c.MaxCurrentLogFolderCount )
            {
                int retryCount = 5;
                retry:
                try
                {
                    if( archivePath == null )
                    {
                        monitor.Trace( "Creating Archive folder." );
                        Directory.CreateDirectory( archivePath = _rootPath + "Archive" + Path.DirectorySeparatorChar );
                    }
                    foreach( var old in timedFolders.Values.Skip( c.MaxCurrentLogFolderCount - 1 ) )
                    {
                        var fName = Path.GetFileName( old );
                        monitor.Trace( $"Moving '{fName}' folder into Archive folder." );
                        var target = archivePath + fName;
                        if( Directory.Exists( target ) )
                        {
                            target += '-' + Guid.NewGuid().ToString();
                        }
                        try
                        {
                            Directory.Move( old, target );
                        }
                        catch( Exception ex )
                        {
                            monitor.Warn( $"Error while moving folder '{fName}' into 'Archive/'.", ex );
                        }
                    }
                    int maxArchive = c.MaxArchivedLogFolderCount;
                    if( maxArchive > 0 )
                    {
                        GetTimedFolders( archivePath, null, out timedFolders, out _ );
                        foreach( var tooOld in timedFolders.Values.Skip( maxArchive ) )
                        {
                            try
                            {
                                Directory.Delete( tooOld, recursive: true );
                            }
                            catch( Exception ex )
                            {
                                monitor.Warn( $"While deleting 'Archive/{Path.GetFileName( tooOld.AsSpan() )}'.", ex );
                            }
                        }
                    }
                }
                catch( Exception ex )
                {
                    if( --retryCount < 0 )
                    {
                        monitor.Error( $"Aborting Log's cleanup of timed folders in '{_basePath}' after 5 retries.", ex );
                        return false;
                    }
                    monitor.Warn( $"Log's cleanup of timed folders in '{_basePath}' failed. Retrying in {retryCount * 100} ms.", ex );
                    Thread.Sleep( retryCount * 100 );
                    goto retry;
                }
            }
        }
        return true;

        static void GetTimedFolders( string folder,
                                     string? currentBasepath,
                                     out SortedDictionary<DateTime, string> timedFolders,
                                     out string? archivePath )
        {
            timedFolders = new SortedDictionary<DateTime, string>( Comparer<DateTime>.Create( ( x, y ) => y.CompareTo( x ) ) );
            bool inRoot = currentBasepath != null;
            archivePath = null;
            foreach( var d in Directory.EnumerateDirectories( folder ) )
            {
                var name = d.AsSpan( folder.Length );
                if( inRoot && name.Equals( "Archive", StringComparison.OrdinalIgnoreCase ) )
                {
                    archivePath = d + FileUtil.DirectorySeparatorString;
                }
                else if( FileUtil.TryMatchFileNameUniqueTimeUtcFormat( ref name, out DateTime date ) )
                {
                    // If we are in "Archive/", we allow the directory name to have a suffix (the -Guid on duplicates).
                    if( inRoot )
                    {
                        // When not in "Archive/", the directory name must be the time format without suffix to be considered.
                        if( !name.IsEmpty ) continue;
                        // And it must not be the current _basePath. If it's the case, we ignore it: it doesn't count as
                        // an existing Timed folder.
                        Throw.DebugAssert( currentBasepath != null && currentBasepath[^1] == Path.DirectorySeparatorChar );
                        if( d.Length == currentBasepath.Length - 1
                            && currentBasepath.AsSpan( 0, d.Length ).Equals( d, StringComparison.Ordinal ) )
                        {
                            continue;
                        }
                    }
                    // Take no risk: ignore (highly unlikely to happen) duplicates. 
                    timedFolders[date] = d;
                }
            }
        }
    }

    /// <summary>
    /// Closes (and optionally forgets) the currently opened file (if it has at least one entry) and renames it.
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
    public string? Close( bool forgetCurrentFile = false )
    {
        return _output != null ? DoCloseCurrentFile( forgetCurrentFile ) : null;
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
            DoCloseCurrentFile();
        }
    }

    /// <summary>
    /// Simply calls <see cref="Close"/>.
    /// </summary>
    public void Dispose() => Close();

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

        // We (exceptionnaly) use FileInfo here because we use the cached FileLength in the second pass.
        var candidates = new List<KeyValuePair<DateTime, FileInfo>>();

        int preservedByDateCount = 0;
        long byteLengthOfPreservedByDate = 0;
        long totalByteLength = 0;
        DateTime minDate = DateTime.UtcNow - timeSpanToKeep;
        //
        // Discovers log files in the _basePath and also in any timed folders that may appear in _basePath:
        // this handles any TimedFolder transparently without too much overhead:
        // - In TimedFolder mode, there is no file at the root.
        // - In regular mode, there is no directory at the root.
        //
        // Processing both fully secures the housekeeping when switching from with or without TimedFolder mode.
        //
        var baseDirectory = new DirectoryInfo( _basePath );
        GetCandidateFilesToDelete( candidates,
                                   ref preservedByDateCount,
                                   ref byteLengthOfPreservedByDate,
                                   ref totalByteLength,
                                   minDate,
                                   baseDirectory );
        //
        // TimedFolder discovering. To remove eventually empty directories, we avoid a second pass here
        // by handling empty directories now: the empty folders are removed by the second call to RunFileHousekeeping.
        // This is not a big deal to have a few empty folders for some time.
        // This method recurses on "Archive" folder name for simplicity.
        //
        HandleHousekeepingTimedFolders( monitor,
                            candidates,
                            ref preservedByDateCount,
                            ref byteLengthOfPreservedByDate,
                            ref totalByteLength,
                            minDate,
                            baseDirectory );
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
                    monitor.Trace( $"Deleting file '{file.FullName}' (housekeeping)." );
                    try
                    {
                        file.Delete();
                    }
                    catch( Exception ex )
                    {
                        monitor.Warn( $"Failed to delete file '{file.FullName}' (housekeeping).", ex );
                    }
                }
            }
        }
    }

    void HandleHousekeepingTimedFolders( IActivityMonitor monitor,
                                         List<KeyValuePair<DateTime, FileInfo>> candidates,
                                         ref int preservedByDateCount,
                                         ref long byteLengthOfPreservedByDate,
                                         ref long totalByteLength,
                                         DateTime minDate,
                                         DirectoryInfo baseDirectory )
    {
        foreach( var timedDirectory in baseDirectory.EnumerateDirectories() )
        {
            var lastName = Path.GetFileName( timedDirectory.FullName.AsSpan() );
            if( lastName.Equals( "Archive", StringComparison.OrdinalIgnoreCase ) )
            {
                HandleHousekeepingTimedFolders( monitor,
                                    candidates,
                                    ref preservedByDateCount,
                                    ref byteLengthOfPreservedByDate,
                                    ref totalByteLength,
                                    minDate,
                                    timedDirectory );
            }
            else if( FileUtil.TryMatchFileNameUniqueTimeUtcFormat( ref lastName, out _ ) )
            {
                int fileCount = GetCandidateFilesToDelete( candidates,
                                                           ref preservedByDateCount,
                                                           ref byteLengthOfPreservedByDate,
                                                           ref totalByteLength,
                                                           minDate,
                                                           timedDirectory );
                // We don't try to delete a TimedFolder that contains a file (any file) or any unexpected directory.
                if( fileCount == 0 && !timedDirectory.EnumerateDirectories().Any() )
                {
                    try
                    {
                        timedDirectory.Delete( recursive: false );
                    }
                    catch( Exception ex )
                    {
                        monitor.Warn( $"Failed to delete empty Timed folder '{timedDirectory.FullName}' (housekeeping).", ex );
                    }
                }
            }
        }
    }

    int GetCandidateFilesToDelete( List<KeyValuePair<DateTime, FileInfo>> candidates,
                                   ref int preservedByDateCount,
                                   ref long byteLengthOfPreservedByDate,
                                   ref long totalByteLength,
                                   DateTime minDate,
                                   DirectoryInfo logDirectory )
    {
        // Counts the total number of files in the directory. This is used for empty TimedFolder removal.
        // We consider that any remaining file in the folder prevents the removal. If the user puts extra file
        // in a TimedFolder, we don't try to delete it (like a readme.md for instance, even if it is weird).
        int fileCount = 0;
        foreach( FileInfo file in logDirectory.EnumerateFiles() )
        {
            ++fileCount;
            var fName = Path.GetFileName( file.FullName.AsSpan() );
            // Temporary files are "T-" + <date> + _fileNameSuffix + ".tmp" (See OpenNewFile())
            if( fName.EndsWith( ".tmp" ) && fName.StartsWith( "T-" ) )
            {
                if( _output != null && _output.Name == file.FullName )
                {
                    // Skip currently-opened temporary file
                    continue;
                }
                var datePart = fName.Slice( 2 );
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
            else if( fName.EndsWith( _fileNameSuffix ) )
            {
                if( FileUtil.TryMatchFileNameUniqueTimeUtcFormat( ref fName, out DateTime d ) )
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
        return fileCount;
    }

    /// <summary>
    /// Opens a new file named "T-" + Unique-Timed-File-Utc + fileNameSuffix + ".tmp".
    /// </summary>
    /// <returns>The opened stream to write to.</returns>
    protected virtual Stream OpenNewFile()
    {
        _openedTimeUtc = DateTime.UtcNow;
        _output = FileUtil.CreateAndOpenUniqueTimedFile( _basePath + "T-",
                                                         _fileNameSuffix + ".tmp",
                                                         _openedTimeUtc,
                                                         FileAccess.Write,
                                                         FileShare.Read,
                                                         _fileBufferSize,
                                                         FileOptions.SequentialScan );
        _countRemainder = _maxCountPerFile;
        return _output;
    }

    /// <inheritdoc cref="Close(bool)"/>
    protected virtual string? DoCloseCurrentFile( bool forgetCurrentFile = false )
    {
        Throw.CheckState( _output != null );
        string fName = _output.Name;
        _output.Dispose();
        _output = null;
        if( forgetCurrentFile || _countRemainder == _maxCountPerFile )
        {
            // No entries were written (or we must forget the file):
            // we try to delete file. If this fails, this is not an issue.
            try
            {
                File.Delete( fName );
            }
            catch( IOException )
            {
                // Forget it.
            }
            return null;
        }
        var closed = DoClose( fName );
        if( _withLastRunSymLink && !_symLinkPrivilegeError )
        {
            Throw.DebugAssert( _lastRunFilePath != null );
            try
            {
                if( File.Exists( _lastRunFilePath ) ) File.Delete( _lastRunFilePath );
                File.CreateSymbolicLink( _lastRunFilePath, closed );
            }
            catch( Exception ex )
            {
                // A required privilege is not held by the client (0x80070522).
                if( ex.HResult == -2147023582 )
                {
                    _symLinkPrivilegeError = true;
                    ActivityMonitor.StaticLogger.Warn( $"Not enough privilege to create symbolic link. Disabling LastRunSymLink feature." );
                }
                else
                {
                    ActivityMonitor.StaticLogger.Warn( $"While updating symbolic link '{_lastRunFilePath}' to '{closed}'.", ex );
                }
            }
        }
        return closed;
    }

    string DoClose( string fName )
    {
        Throw.DebugAssert( _basePath != null );
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
                return destination.Name;
            }
        }
        return FileUtil.MoveToUniqueTimedFile( fName, _basePath, _fileNameSuffix, _openedTimeUtc );
    }
}
