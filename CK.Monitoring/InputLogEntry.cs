using CK.Core;
using CK.Monitoring.Impl;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace CK.Monitoring;

/// <summary>
/// Input entries are cached and reused and can be retained if needed.
/// </summary>
public sealed partial class InputLogEntry : IFullLogEntry
{
    [AllowNull] string _grandOutputId;
    [AllowNull] string _monitorId;
    [AllowNull] CKTrait _tags;
    // Until reusable buffers are implemented, we don't need to
    // retain a ActivityMonitorExternalLogData.
    // ActivityMonitorExternalLogData? _data;
    string? _text;
    CKExceptionData? _exception;
    string? _fileName;
    int _lineNumber;
    int _groupDepth;
    DateTimeStamp _logTime;
    LogLevel _logLevel;
    DateTimeStamp _previousLogTime;
    LogEntryType _previousEntryType;
    LogEntryType _logType;
    IReadOnlyList<ActivityLogGroupConclusion>? _conclusions;
    int _refCount;

    InputLogEntry()
    {
        _refCount = 1;
        _tags = ActivityMonitor.Tags.Empty;
    }

    // For Log, OpenGroup and StaticLogger.
    void Initialize( string grandOutputId,
                     ref ActivityMonitorLogData data,
                     LogEntryType logType,
                     LogEntryType previousEntryType,
                     DateTimeStamp previousLogTime )
    {
        Debug.Assert( logType == LogEntryType.OpenGroup || logType == LogEntryType.Line );
        Debug.Assert( _refCount == 1 && _exception == null && _conclusions == null && _tags == ActivityMonitor.Tags.Empty && _text == null && _monitorId == null,
                      "Fields have been reset." );
        _groupDepth = data.Depth;
        _grandOutputId = grandOutputId;
        _tags = data.Tags;
        // We should not do this once Text may be obtained from pooled buffers.
        _text = data.Text;
        _exception = data.ExceptionData;
        _logType = logType;
        _monitorId = data.MonitorId;
        _previousEntryType = previousEntryType;
        _previousLogTime = previousLogTime;
        _logTime = data.LogTime;
        _logLevel = data.Level;
        _fileName = data.FileName;
        _lineNumber = data.LineNumber;
    }

    // Only for monitor.CloseGroup().
    void Initialize( string grandOutputId,
                     DateTimeStamp closeLogTime,
                     IReadOnlyList<ActivityLogGroupConclusion> conclusions,
                     LogLevel logLevel,
                     int groupDepth,
                     string monitorId,
                     LogEntryType previousEntryType,
                     DateTimeStamp previousLogTime )
    {
        Debug.Assert( _refCount == 1 && _exception == null && _conclusions == null && _tags == ActivityMonitor.Tags.Empty && _text == null && _monitorId == null,
                      "Fields have been reset." );
        _grandOutputId = grandOutputId;
        _groupDepth = groupDepth;
        _logType = LogEntryType.CloseGroup;
        _monitorId = monitorId;
        _logTime = closeLogTime;
        _logLevel = logLevel;
        _conclusions = conclusions;
        _previousEntryType = previousEntryType;
        _previousLogTime = previousLogTime;
    }

    // For ExternalLog.
    void Initialize( string grandOutputId,
                     string monitorId,
                     DateTimeStamp prevLogTime,
                     string text,
                     DateTimeStamp logTime,
                     LogLevel level,
                     CKTrait tags,
                     CKExceptionData? ex )
    {
        Debug.Assert( _refCount == 1 && _exception == null && _conclusions == null && _tags == ActivityMonitor.Tags.Empty && _text == null && _monitorId == null,
                      "Fields have been reset." );
        _grandOutputId = grandOutputId;
        _groupDepth = 0;
        _logType = LogEntryType.Line;
        _previousEntryType = LogEntryType.Line;
        _monitorId = monitorId;
        _logTime = logTime;
        _logLevel = level;
        _text = text;
        _tags = tags;
        _previousLogTime = prevLogTime;
        _exception = ex;
    }

    /// <inheritdoc />
    public int GroupDepth => _groupDepth;

    /// <inheritdoc />
    public LogEntryType LogType => _logType;

    /// <inheritdoc />
    public LogLevel LogLevel => _logLevel;

    /// <inheritdoc />
    public string? Text => _text;

    /// <inheritdoc />
    public CKTrait Tags => _tags;

    /// <inheritdoc />
    public DateTimeStamp LogTime => _logTime;

    /// <inheritdoc />
    public CKExceptionData? Exception => _exception;

    /// <inheritdoc />
    public string? FileName => _fileName;

    /// <inheritdoc />
    public int LineNumber => _lineNumber;

    /// <inheritdoc />
    public IReadOnlyList<ActivityLogGroupConclusion>? Conclusions => _conclusions;

    /// <inheritdoc />
    public string GrandOutputId => _grandOutputId;

    /// <inheritdoc />
    public string MonitorId => _monitorId;

    /// <inheritdoc />
    public LogEntryType PreviousEntryType => _previousEntryType;

    /// <inheritdoc />
    public DateTimeStamp PreviousLogTime => _previousLogTime;

    /// <inheritdoc />
    public IBaseLogEntry CreateLightLogEntry()
    {
        return LogType switch
        {
            LogEntryType.Line => new BaseLineEntry( _text!, _logTime, _fileName, _lineNumber, _logLevel, _tags, _exception ),
            LogEntryType.OpenGroup => new BaseOpenGroupEntry( _text!, _logTime, _fileName, _lineNumber, _logLevel, _tags, _exception ),
            LogEntryType.CloseGroup => new BaseCloseGroupEntry( _logTime, _logLevel, _conclusions ),
            _ => Throw.InvalidOperationException<IBaseLogEntry>()
        }; ;
    }

    /// <inheritdoc />
    public ILogEntry CreateSimpleLogEntry()
    {
        return LogType switch
        {
            LogEntryType.Line => new StdLineEntry( _monitorId, _groupDepth, _text!, _logTime, _fileName, _lineNumber, _logLevel, _tags, _exception ),
            LogEntryType.OpenGroup => new StdOpenGroupEntry( _monitorId, _groupDepth, _text!, _logTime, _fileName, _lineNumber, _logLevel, _tags, _exception ),
            LogEntryType.CloseGroup => new StdCloseGroupEntry( _monitorId, _groupDepth, _logTime, _logLevel, _conclusions ),
            _ => Throw.InvalidOperationException<ILogEntry>()
        }; ;
    }

    /// <inheritdoc />
    public void WriteLogEntry( CKBinaryWriter w )
    {
        if( _logType == LogEntryType.CloseGroup )
        {
            LogEntry.WriteCloseGroup( w,
                                        _grandOutputId,
                                        _monitorId,
                                        _previousEntryType,
                                        _previousLogTime,
                                        _groupDepth,
                                        _logLevel,
                                        _logTime,
                                        _conclusions );
        }
        else
        {
            Debug.Assert( _logType == LogEntryType.OpenGroup || _logType == LogEntryType.Line );
            Debug.Assert( _text != null );
            LogEntry.WriteLog( w,
                            _grandOutputId,
                            _monitorId,
                            _previousEntryType,
                            _previousLogTime,
                            _groupDepth,
                            _logType == LogEntryType.OpenGroup,
                            _logLevel,
                            _logTime,
                            _text,
                            _tags,
                            _exception,
                            _fileName,
                            _lineNumber );
        }
    }

    /// <summary>
    /// Adds a reference to this cached data. <see cref="Release()"/> must be called once for each call to AddRef.
    /// </summary>
    public void AddRef() => Interlocked.Increment( ref _refCount );

    /// <summary>
    /// Releases this cached data.
    /// </summary>
    public void Release()
    {
        int refCount = Interlocked.Decrement( ref _refCount );
        if( refCount == 0 )
        {
            _tags = ActivityMonitor.Tags.Empty;
            _conclusions = null;
            _exception = null;
            _text = null;
            _monitorId = null;
            _refCount = 1;
            // The Initialize() overloads for ExternaLog and CloseGroup do not set _fileName and _lineNumber.
            _fileName = null;
            _lineNumber = 0;
            // The remaining fields will be reset by all three Initialize() overloads on reuse:
            // _groupDepth, _logType, _previousEntryType, _logTime, _logLevel and _previousLogTime
            Release( this );
            return;
        }
        Throw.CheckState( refCount > 0 );
    }
}

