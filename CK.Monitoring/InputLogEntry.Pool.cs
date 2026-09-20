using CK.Core;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System;

namespace CK.Monitoring;

public sealed partial class InputLogEntry
{
    /// <summary>
    /// The initial pool capacity. <see cref="CurrentPoolCapacity"/> never shrinks below this.
    /// </summary>
    public const int InitialPoolCapacity = 600;

    /// <summary>
    /// Gets the current pool capacity. It starts at <see cref="InitialPoolCapacity"/> and increases silently
    /// until <see cref="MaximalPoolCapacity"/> is reached (above which released entries are garbage collected
    /// instead of being pooled).
    /// <para>
    /// It shrinks back towards the recently observed demand: see <see cref="PoolDiagnostics.RecentPeakAliveCount"/>.
    /// </para>
    /// </summary>
    public static int CurrentPoolCapacity => _currentCapacity;

    /// <summary>
    /// The current pool capacity increment until <see cref="CurrentPoolCapacity"/> reaches <see cref="MaximalPoolCapacity"/>.
    /// This is also the margin kept above the observed demand when the pool trims itself.
    /// </summary>
    public const int PoolCapacityIncrement = 200;

    /// <summary>
    /// Gets the maximal capacity. Once reached, newly released <see cref="InputLogEntry"/> are garbage
    /// collected instead of returned to the pool.
    /// </summary>
    public static int MaximalPoolCapacity => _maximalCapacity;

    /// <summary>
    /// Gets the health and leak diagnostics of this pool.
    /// <para>
    /// Pool saturation is <em>not</em> a leak: it is a peak of activity. Leaks are reported by
    /// <see cref="PoolDiagnostics.LeakedCount"/> (a <see cref="Release()"/> call is missing) and by
    /// <see cref="PoolDiagnostics.CurrentFloor"/> (entries are acquired and retained forever).
    /// </para>
    /// </summary>
    public static PoolDiagnostics PoolDiagnostics => _diagnostics;

    /// <summary>
    /// Gets the current number of <see cref="InputLogEntry"/> that are alive (not yet released).
    /// When there is no log activity and no entry have been cached, this must be 0.
    /// </summary>
    public static int AliveCount => _diagnostics.AliveCount;

    /// <summary>
    /// Gets the current number of cached entries.
    /// This is an approximate value because of concurency.
    /// </summary>
    public static int PooledEntryCount => _numItems + (_fastItem != null ? 1 : 0);

    readonly static ConcurrentQueue<InputLogEntry> _items = new();
    static readonly PoolDiagnostics _diagnostics;
    static InputLogEntry? _fastItem;
    static int _numItems;
    static int _currentCapacity = InitialPoolCapacity;
    static int _maximalCapacity = 2000;

    static InputLogEntry()
    {
        _diagnostics = new PoolDiagnostics( "CK.Monitoring.GrandOutput InputLogEntry" );
        _diagnostics.OnWindowClosed += Trim;
    }

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
        _diagnostics.OnAcquire();
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
            Throw.DebugAssert( "In the pool and new entries have RefCount = 0.", item._refCount == 0 );
        }
        return item;
    }

    static void Release( InputLogEntry c )
    {
        // Must be done before anything else: this feeds the alive count floor that detects retained entries
        // and may close an observation window (that trims this pool).
        _diagnostics.OnRelease();
        if( _fastItem != null || Interlocked.CompareExchange( ref _fastItem, c, null ) != null )
        {
            int poolCount = Interlocked.Increment( ref _numItems );
            // Strictly lower than to account for the _fastItem.
            if( poolCount < _currentCapacity )
            {
                _items.Enqueue( c );
                return;
            }
            // Current capacity is reached: increase it until MaximalPoolCapacity. Above it, the entry is
            // dropped and garbage collected. This is a capacity event (a peak of concurrent activity), NOT a
            // leak: a leaked entry never comes back here in the first place.
            if( poolCount >= MaximalPoolCapacity )
            {
                // Adjust the pool count and drop the entry.
                Interlocked.Decrement( ref _numItems );
                GC.SuppressFinalize( c );
                _diagnostics.OnSaturated();
            }
            else
            {
                Interlocked.Add( ref _currentCapacity, PoolCapacityIncrement );
                _diagnostics.OnCapacityIncreased();
                _items.Enqueue( c );
            }
        }
    }

    /// <summary>
    /// Shrinks the pool back towards the demand observed over the whole recent history. Without this, a single
    /// peak of activity would keep the pool at its maximal capacity forever and every subsequent Release() that
    /// loses the _fastItem race would report a saturation.
    /// </summary>
    static void Trim( PoolDiagnostics d )
    {
        int target = Math.Max( InitialPoolCapacity, d.RecentPeakAliveCount + PoolCapacityIncrement );
        if( target >= _currentCapacity ) return;
        Interlocked.Exchange( ref _currentCapacity, target );
        while( _numItems > target && _items.TryDequeue( out var e ) )
        {
            Interlocked.Decrement( ref _numItems );
            // Dropped on purpose: this is not a leak.
            GC.SuppressFinalize( e );
        }
    }

}
