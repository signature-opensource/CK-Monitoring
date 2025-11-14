using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CK.Core;

namespace CK.Monitoring;

/// <summary>
/// A temporary <see cref="ILogEntry"/> collector created by <see cref="GrandOutput.CreateMemoryCollector(int,bool)"/>
/// that must be disposed.
/// <para>
/// This is intended to be used in tests: there is little to no interest to
/// use this elsewhere.
/// </para>
/// </summary>
public sealed class GrandOutputMemoryCollector : IDisposable
{
    readonly DispatcherSink _sink;
    readonly Handler _handler;
    // There won't be a lot of concurrent access: the handler.HandleAsync will
    // will take the lock directly, except when Extract/CopyTo methods
    // are called.
    // Instead of a ConcurrentQueue, we use a simple locked FIFOBuffer.
    readonly FIFOBuffer<ILogEntry> _buffer;
    readonly List<ILogEntry> _cachedEntries;
    readonly IReadOnlyList<string> _cachedText;
    List<string>? _cachedListText;
    int _disposed;

    internal GrandOutputMemoryCollector( DispatcherSink sink, int maxCapacity, bool ignoreCloseGroup )
    {
        _sink = sink;
        _handler = new Handler( this, ignoreCloseGroup );
        _buffer = new FIFOBuffer<ILogEntry>( Math.Max( 1 + maxCapacity / 4, 512 ), maxCapacity );
        _cachedEntries = new List<ILogEntry>();
        if( ignoreCloseGroup ) _cachedText = new TextAdapter( _cachedEntries );
        else
        {
            _cachedListText = new List<string>();
            _cachedText = _cachedListText;
        }
        _sink.SubmitAddHandler( _handler );
    }

    /// <summary>
    /// Gets the current count of entries. This can change anytime.
    /// </summary>
    public int Count => _buffer.Count;

    /// <summary>
    /// Gets the maximal number of collected entries.
    /// This is a FIFO buffer (oldest entries are automatocally discarded).
    /// </summary>
    public int MaxCapacity => _buffer.MaxDynamicCapacity;

    /// <summary>
    /// Gets a cache of entries.
    /// Use <see cref="UpdateCachedEntries"/> to transfer current entries into this cache.
    /// </summary>
    public IReadOnlyList<ILogEntry> CachedEntries => _cachedEntries;

    /// <summary>
    /// Gets the <see cref="IBaseLogEntry.Text"/> of <see cref="CachedEntries"/>
    /// excluding <see cref="LogEntryType.CloseGroup"/> (for which Text is null).
    /// <para>
    /// <see cref="UpdateCachedEntries"/> also updates this cache.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> CachedTexts => _cachedText;

    /// <summary>
    /// Clears <see cref="CachedEntries"/> and <see cref="CachedTexts"/>.
    /// </summary>
    public void ClearCache()
    {
        _cachedEntries.Clear();
        _cachedListText?.Clear();
    }

    /// <summary>
    /// Calls <see cref="DispatcherSink.SyncWait()"/>, transfers the current <see cref="ILogEntry"/> to
    /// the <see cref="CachedEntries"/> and updates <see cref="CachedTexts"/> accordingly.
    /// Current entries are cleared.
    /// </summary>
    /// <returns>The cached entries.</returns>
    public IReadOnlyList<ILogEntry> UpdateCachedEntries()
    {
        _sink.SyncWait();
        return DoUpdateCachedEntries();
    }

    /// <summary>
    /// Calls <see cref="DispatcherSink.SyncWaitAsync()"/>, transfers the current <see cref="ILogEntry"/> to
    /// the <see cref="CachedEntries"/> and updates <see cref="CachedTexts"/> accordingly.
    /// Current entries are cleared.
    /// </summary>
    /// <returns>The cached entries.</returns>
    public async Task<IReadOnlyList<ILogEntry>> UpdateCachedEntriesAsync()
    {
        await _sink.SyncWaitAsync();
        return DoUpdateCachedEntries();
    }

    IReadOnlyList<ILogEntry> DoUpdateCachedEntries()
    {
        lock( _buffer )
        {
            while( _buffer.Count > 0 )
            {
                ILogEntry e = _buffer.Pop();
                _cachedEntries.Add( e );
                Throw.DebugAssert( "No cached text => Text is never null.", _cachedListText != null || e.Text != null );
                if( _cachedListText != null && e.Text != null )
                {
                    _cachedListText.Add( e.Text );
                }
            }
        }
        return _cachedEntries;
    }

    /// <summary>
    /// Calls <see cref="DispatcherSink.SyncWait()"/>, collects all the current <see cref="ILogEntry"/> and clears them.
    /// </summary>
    /// <returns>The current entries.</returns>
    public ILogEntry[] ExtractCurrentEntries()
    {
        _sink.SyncWait();
        return DoExtractCurrentEntries();
    }

    /// <summary>
    /// Calls <see cref="DispatcherSink.SyncWaitAsync()"/>, collects all the current <see cref="ILogEntry"/> and clears them.
    /// </summary>
    /// <returns>The current entries.</returns>
    public async Task<ILogEntry[]> ExtractCurrentEntriesAsync()
    {
        await _sink.SyncWaitAsync();
        return DoExtractCurrentEntries();
    }

    ILogEntry[] DoExtractCurrentEntries()
    {
        lock( _buffer )
        {
            var a = _buffer.ToArray();
            _buffer.Clear();
            return a;
        }
    }

    /// <summary>
    /// Calls <see cref="DispatcherSink.SyncWait()"/>, collects the current texts and clears the current entries.
    /// </summary>
    /// <returns>The current log texts.</returns>
    public ImmutableArray<string> ExtractCurrentTexts()
    {
        var a = ExtractCurrentEntries();
        return ExtractedToText( a );
    }

    /// <summary>
    /// Calls <see cref="DispatcherSink.SyncWaitAsync()"/>, collects the current texts and clears the current entries.
    /// </summary>
    /// <returns>The current log texts.</returns>
    public async Task<ImmutableArray<string>> ExtractCurrentTextsAsync()
    {
        var a = await ExtractCurrentEntriesAsync().ConfigureAwait( false );
        return ExtractedToText( a );
    }

    static ImmutableArray<string> ExtractedToText( ILogEntry[] a )
    {
        var b = ImmutableArray.CreateBuilder<string>( a.Length );
        bool hasCloseGroup = false;
        foreach( var e in a )
        {
            if( e.Text == null ) hasCloseGroup = true;
            else b.Add( e.Text );
        }
        return hasCloseGroup ? b.ToImmutableArray() : b.MoveToImmutable();
    }

    /// <summary>
    /// Calls <see cref="DispatcherSink.SyncWait()"/> and copies the latest entries, optionally clears all
    /// the current entries and returns the number of entries copied into <paramref name="oldest"/>.
    /// </summary>
    /// <param name="oldest">A target destination.</param>
    /// <returns>
    /// The number of entries copied in newest. <see cref="Span{T}.Slice(int, int)"/> should be used.
    /// </returns>
    public int ExtractOldestTo( Span<ILogEntry> oldest )
    {
        _sink.SyncWait();
        lock( _buffer )
        {
            return _buffer.PopRange( oldest );
        }
    }

    /// <summary>
    /// Calls <see cref="DispatcherSink.SyncWait()"/> and copies the latest entries, optionally clears all
    /// the current entries and returns the number of entries copied into <paramref name="newest"/>.
    /// </summary>
    /// <param name="newest">A target destination.</param>
    /// <param name="clearAll">True to clear all the current entries.</param>
    /// <returns>
    /// The number of entries copied in newest. <see cref="Span{T}.Slice(int, int)"/> should be used.
    /// </returns>
    public int ExtractNewestTo( Span<ILogEntry> newest, bool clearAll = false )
    {
        _sink.SyncWait();
        lock( _buffer )
        {
            int c = _buffer.PopRange( newest );
            if( clearAll ) _buffer.Clear();
            return c;
        }
    }

    /// <summary>
    /// Gets whether this handler is disposed.
    /// Note that the entries are not cleared when the collector is disposed.
    /// </summary>
    public bool IsDisposed => _disposed != 0;

    /// <summary>
    /// Dispose this handler.
    /// No more entries will be collected but the recevied entries, the <see cref="CachedEntries"/>
    /// and <see cref="CachedTexts"/> are kept.
    /// </summary>
    public void Dispose()
    {
        if( Interlocked.Exchange( ref _disposed, 1 ) == 0 )
        {
            _sink.SubmitRemoveHandler( _handler );
        }
    }

    sealed class Handler : IGrandOutputHandler
    {
        readonly GrandOutputMemoryCollector _h;
        readonly bool _ignoreCloseGroup;

        public Handler( GrandOutputMemoryCollector h, bool ignoreCloseGroup )
        {
            _h = h;
            _ignoreCloseGroup = ignoreCloseGroup;
        }

        public ValueTask<bool> ActivateAsync( IActivityMonitor monitor ) => ValueTask.FromResult( true );

        public ValueTask<bool> ApplyConfigurationAsync( IActivityMonitor monitor, IHandlerConfiguration c ) => ValueTask.FromResult( false );

        public ValueTask DeactivateAsync( IActivityMonitor monitor )
        {
            _h.Dispose();
            return default;
        }

        public ValueTask HandleAsync( IActivityMonitor monitor, InputLogEntry logEvent )
        {
            // No race condition here. The buffer is always here and if we add a new
            // entry in it when it is disposed, we don't care.
            if( _h._disposed == 0 && (!_ignoreCloseGroup || logEvent.LogType != LogEntryType.CloseGroup) )
            {
                var e = logEvent.CreateSimpleLogEntry();
                var b = _h._buffer;
                lock( b )
                {
                    b.Push( e );
                }
            }
            return default;
        }

        public ValueTask OnTimerAsync( IActivityMonitor monitor, TimeSpan timerSpan ) => default;
    }

    sealed class TextAdapter : IReadOnlyList<string>
    {
        readonly List<ILogEntry> _e;

        public TextAdapter( List<ILogEntry> e ) => _e = e;

        public string this[int index] => _e[index].Text!;

        public int Count => _e.Count;

        public IEnumerator<string> GetEnumerator() => _e.Select( e => e.Text! ).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
