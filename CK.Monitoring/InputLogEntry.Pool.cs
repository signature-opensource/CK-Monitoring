using CK.Core;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System;

namespace CK.Monitoring;

public sealed partial class InputLogEntry
{
    /// <summary>
    /// Gets the current pool capacity. It starts at 600 and increases (with a warning) until <see cref="MaximalPoolCapacity"/>
    /// is reached (where errors are emitted).
    /// </summary>
    public static int CurrentPoolCapacity => _currentCapacity;

    /// <summary>
    /// The current pool capacity increment until <see cref="CurrentPoolCapacity"/> reaches <see cref="MaximalPoolCapacity"/>.
    /// </summary>
    public const int PoolCapacityIncrement = 200;

    /// <summary>
    /// Gets the maximal capacity. Once reached, newly acquired <see cref="InputLogEntry"/> are garbage
    /// collected instead of returned to the pool.
    /// </summary>
    public static int MaximalPoolCapacity => _maximalCapacity;

    /// <summary>
    /// Gets the current number of <see cref="InputLogEntry"/> that are alive (not yet released).
    /// When there is no log activity and no entry have been cached, this must be 0.
    /// </summary>
    public static int AliveCount => _aliveItems;

    /// <summary>
    /// Gets the current number of cached entries.
    /// This is an approximate value because of concurency.
    /// </summary>
    public static int PooledEntryCount => _numItems + (_fastItem != null ? 1 : 0);

    readonly static ConcurrentQueue<InputLogEntry> _items = new();
    static InputLogEntry? _fastItem;
    static int _numItems;
    static int _currentCapacity = 600;
    static int _maximalCapacity = 2000;
    static int _aliveItems;
    static long _nextPoolError;

    // For Log, OpenGroup and StaticLogger.
    internal static InputLogEntry AcquireInputLogEntry( string grandOutputId,
                                                        ref ActivityMonitorLogData data,
                                                        LogEntryType logType,
                                                        LogEntryType previousEntryType,
                                                        DateTimeStamp previousLogTime )
    {
        InputLogEntry item = Aquire();
        item.Initialize( grandOutputId, ref data, logType, previousEntryType, previousLogTime );
        return item;
    }

    // Only for monitor.CloseGroup().
    internal static InputLogEntry AcquireInputLogEntry( string grandOutputId,
                                                        DateTimeStamp closeLogTime,
                                                        IReadOnlyList<ActivityLogGroupConclusion> conclusions,
                                                        LogLevel logLevel,
                                                        int groupDepth,
                                                        string monitorId,
                                                        LogEntryType previousEntryType,
                                                        DateTimeStamp previousLogTime )
    {
        InputLogEntry item = Aquire();
        item.Initialize( grandOutputId, closeLogTime, conclusions, logLevel, groupDepth, monitorId, previousEntryType, previousLogTime );
        return item;
    }

    // For ExternalLog.
    internal static InputLogEntry AcquireInputLogEntry( string grandOutputId,
                                                        string monitorId,
                                                        DateTimeStamp prevLogTime,
                                                        string text,
                                                        DateTimeStamp logTime,
                                                        LogLevel level,
                                                        CKTrait tags,
                                                        CKExceptionData? ex )
    {
        InputLogEntry item = Aquire();
        item.Initialize( grandOutputId, monitorId, prevLogTime, text, logTime, level, tags, ex );
        return item;
    }

    static InputLogEntry Aquire()
    {
        Interlocked.Increment( ref _aliveItems );
        var item = _fastItem;
        if( item == null || Interlocked.CompareExchange( ref _fastItem, null, item ) != item )
        {
            if( _items.TryDequeue( out item ) )
            {
                Interlocked.Decrement( ref _numItems );
            }
            else
            {
                item = new InputLogEntry();
            }
            Throw.DebugAssert( "In the pool and new entry have RefCount = 1.", item._refCount == 1 );
        }
        return item;
    }

    static void Release( InputLogEntry c )
    {
        Interlocked.Decrement( ref _aliveItems );
        if( _fastItem != null || Interlocked.CompareExchange( ref _fastItem, c, null ) != null )
        {
            int poolCount = Interlocked.Increment( ref _numItems );
            // Strictly lower than to account for the _fastItem.
            if( poolCount < _currentCapacity )
            {
                _items.Enqueue( c );
                return;
            }
            // Current capacity is reached. Increasing it and emits a warning.
            // If the count reaches the MaximalCapacity, emits an error and don't increase the
            // limit anymore: log data will be garbage collected. If this error persists, it indicates a leak somewhere!
            if( poolCount >= MaximalPoolCapacity )
            {
                // Adjust the pool count.
                Interlocked.Decrement( ref _numItems );
                // Signals the error but no more than once per second.
                var next = _nextPoolError;
                var nextNext = Environment.TickCount64;
                if( next < nextNext && Interlocked.CompareExchange( ref _nextPoolError, nextNext + 1000, next ) == next )
                {
                    ActivityMonitor.StaticLogger.UnfilteredLog( LogLevel.Error | LogLevel.IsFiltered,
                                                                ActivityMonitor.Tags.ToBeInvestigated,
                                                                $"The CK.Monitoring.GrandOutput log data pool reached its maximal capacity of {MaximalPoolCapacity}. This may indicate a peak of activity " +
                                                                $"or a leak (missing InputLogEntry.Release() calls) if this error persists.", null );
                }
            }
            else
            {
                int newCapacity = Interlocked.Add( ref _currentCapacity, PoolCapacityIncrement );
                ActivityMonitor.StaticLogger.UnfilteredLog( LogLevel.Warn | LogLevel.IsFiltered, null, $"The CK.Monitoring.GrandOutput log data pool has been increased to {newCapacity}.", null );
                _items.Enqueue( c );
            }
        }
    }

}

