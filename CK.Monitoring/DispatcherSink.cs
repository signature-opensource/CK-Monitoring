using CK.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CK.Monitoring;

/// <summary>
/// Log event sink: <see cref="Handle(InputLogEntry)"/> dispatches the log
/// event to the <see cref="IGrandOutputHandler"/>.
/// </summary>
public sealed partial class DispatcherSink
{
    // Null is the Timer (awaker).
    // InputLogEntry is the most common.
    // AddDynamicHandler pushes a IGrandOutputHandler.
    // RemoveDynamicHandler pushes a IDynamicGrandOutputHandler.
    readonly Channel<object?> _queue;

    readonly Task _runningTask;
    readonly List<IGrandOutputHandler> _handlers;
    readonly IdentityCard _identityCard;
    readonly long _deltaExternalTicks;
    readonly Action _externalOnTimer;
    readonly object _confTrigger;
    readonly Action<IActivityMonitor> _initialRegister;
    readonly Action<LogFilter?, LogLevelFilter?> _filterChange;
    readonly CancellationTokenSource _stopTokenSource;
    // We Dispose the _stopTokenSource: copy a token here that will not
    // throw a ObjectDisposedException when retrieved.
    CancellationToken _stoppingToken;
    readonly object _externalLogLock;
    readonly string _sinkMonitorId;

    HandlerList? _handlerList;
    GrandOutputConfiguration[] _newConf;
    TimeSpan _timerDuration;
    long _deltaTicks;
    long _nextTicks;
    long _nextExternalTicks;
    int _configurationCount;
    InputLogEntry? _closingLog;
    DateTimeStamp _externalLogLastTime;
    readonly bool _isDefaultGrandOutput;
    bool _unhandledExceptionTracking;

    internal DispatcherSink( Action<IActivityMonitor> initialRegister,
                             IdentityCard identityCard,
                             TimeSpan timerDuration,
                             TimeSpan externalTimerDuration,
                             Action externalTimer,
                             Action<LogFilter?, LogLevelFilter?> filterChange,
                             bool isDefaultGrandOutput )
    {
        _initialRegister = initialRegister;
        _identityCard = identityCard;
        _queue = Channel.CreateUnbounded<object?>( new UnboundedChannelOptions() { SingleReader = true } );
        _handlers = new List<IGrandOutputHandler>();
        _confTrigger = new object();
        _stopTokenSource = new CancellationTokenSource();
        _stoppingToken = _stopTokenSource.Token;
        _timerDuration = timerDuration;
        _deltaTicks = timerDuration.Ticks;
        _deltaExternalTicks = externalTimerDuration.Ticks;
        _externalOnTimer = externalTimer;
        _filterChange = filterChange;
        _externalLogLock = new object();
        _externalLogLastTime = DateTimeStamp.MinValue;
        _isDefaultGrandOutput = isDefaultGrandOutput;
        _newConf = Array.Empty<GrandOutputConfiguration>();
        var monitor = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
        // We emit the identity card changed from this monitor (so we need its id).
        // But more importantly, this monitor identifier is the one of the GrandOutput: each log entry
        // references this identifier.
        _sinkMonitorId = monitor.UniqueId;
        _runningTask = ProcessAsync( monitor );
    }

    internal string SinkMonitorId => _sinkMonitorId;

    internal TimeSpan TimerDuration
    {
        get => _timerDuration;
        set
        {
            if( _timerDuration != value )
            {
                _timerDuration = value;
                _deltaTicks = value.Ticks;
            }
        }
    }

    /// <summary>
    /// Gets a cancellation token that is canceled by Stop.
    /// </summary>
    internal CancellationToken StoppingToken => _stoppingToken;

    async Task ProcessAsync( IActivityMonitor monitor )
    {
        // Simple pooling for initial configuration.
        // Starting with the delay avoids a Task.Run() in the constructor.
        GrandOutputConfiguration[] newConf = _newConf;
        do
        {
            await Task.Delay( 5 );
            newConf = _newConf;
        }
        while( newConf.Length == 0 );
        // The initial configuration is available. Registers our loop monitor
        // and applies the configuration.
        _initialRegister( monitor );
        monitor.SetTopic( "CK.Monitoring.DispatcherSink" );
        await DoConfigureAsync( monitor, newConf );
        // Initialize the identity card.
        _identityCard.LocalInitialize( monitor, _isDefaultGrandOutput );
        // First register to the OnChange to avoid missing an update...
        _identityCard.OnChanged += IdentityCardOnChanged;
        // ...then sends the current content of the identity card.
        monitor.UnfilteredLog( LogLevel.Info | LogLevel.IsFiltered, IdentityCard.IdentityCardFull, _identityCard.ToString(), null );
        // Configures the next timer due date.
        long now = Stopwatch.GetTimestamp();
        _nextTicks = now + _timerDuration.Ticks;
        _nextExternalTicks = now + _timerDuration.Ticks;
        // Creates and launch the "awaker". This avoids any CancellationToken.
        Timer awaker = new Timer( _ => _queue.Writer.TryWrite( null ), null, 100u, 100u );
        while( await _queue.Reader.WaitToReadAsync() )
        {
            _queue.Reader.TryRead( out var o );
            newConf = _newConf;
            Throw.DebugAssert( "Except at the start, this is never null.", newConf != null );
            if( newConf.Length > 0 ) await DoConfigureAsync( monitor, newConf );
            List<IGrandOutputHandler>? faulty = null;
            #region Process event if any (including the CloseSentinel).
            if( o is InputLogEntry e )
            {
                // Regular handling.
                // IdentityCardUpdate entries are hooked and if they can't be parsed or if they trigger
                // no change, we filter them out.
                bool skip = false;
                if( e.Tags.Overlaps( ActivityMonitorSimpleSenderExtension.IdentityCard.IdentityCardUpdate ) )
                {
                    var i = IdentityCard.TryUnpack( e.Text );
                    if( i is IReadOnlyList<(string, string)> identityInfo )
                    {
                        var change = _identityCard.Add( identityInfo );
                        skip = change == null;
                    }
                    else
                    {
                        monitor.Error( $"Invalid {nameof( ActivityMonitorSimpleSenderExtension.AddIdentityInformation )} payload: '{e.Text}'." );
                        skip = true;
                    }
                }
                if( !skip )
                {
                    foreach( var h in _handlers )
                    {
                        try
                        {
                            await h.HandleAsync( monitor, e );
                        }
                        catch( Exception ex )
                        {
                            monitor.Fatal( $"{h.GetType()}.Handle() crashed.", ex );
                            faulty ??= new List<IGrandOutputHandler>();
                            faulty.Add( h );
                        }
                    }
                    // The _closingLog is the "soft stop": it ensures that any entries added prior
                    // to the call to stop have been handled (but if _forceClose is set, this is ignored).
                    if( e == _closingLog )
                    {
                        e.Release();
                        break;
                    }
                }
                e.Release();
            }
            else if( o != null )
            {
                if( o is IGrandOutputHandlersActionBase action )
                {
                    await action.DoRunAsync( monitor, _handlerList ??= new HandlerList( this ) );
                }
                else if( o is IGrandOutputHandler toAdd )
                {
                    if( await SafeActivateOrDeactivateAsync( monitor, toAdd, true ) )
                    {
                        _handlers.Add( toAdd );
                    }
                }
                else if( o is Handlers.IDynamicGrandOutputHandler dH )
                {
                    if( await SafeActivateOrDeactivateAsync( monitor, dH.Handler, false ) )
                    {
                        _handlers.Remove( dH.Handler );
                    }
                }
                else if( o is TaskCompletionSource asyncWait )
                {
                    asyncWait.SetResult();
                }
                else if( o is SyncWaitSignal syncWait )
                {
                    lock( syncWait )
                        Monitor.Pulse( syncWait );
                }
            }
            #endregion
            #region if not closing: process OnTimer (on every item).
            if( !_stopTokenSource.IsCancellationRequested )
            {
                now = Stopwatch.GetTimestamp();
                if( now >= _nextTicks )
                {
                    foreach( var h in _handlers )
                    {
                        try
                        {
                            await h.OnTimerAsync( monitor, _timerDuration );
                        }
                        catch( Exception ex )
                        {
                            monitor.Fatal( $"{h.GetType()}.OnTimer() crashed.", ex );
                            faulty ??= new List<IGrandOutputHandler>();
                            faulty.Add( h );
                        }
                    }
                    _nextTicks = now + _deltaTicks;
                    if( now >= _nextExternalTicks )
                    {
                        _externalOnTimer();
                        _nextExternalTicks = now + _deltaExternalTicks;
                    }
                }
            }
            #endregion
            if( faulty != null )
            {
                foreach( var h in faulty )
                {
                    await SafeActivateOrDeactivateAsync( monitor, h, false );
                    _handlers.Remove( h );
                }
            }
        }
        await awaker.DisposeAsync();
        // Whether we are in _forceClose or not, release any remaining entries that may
        // have been written to the channel.
        while( _queue.Reader.TryRead( out var more ) )
        {
            // Do NOT handle these entries!
            // This GrandOuput/Sink is closed, handling them would be too risky
            // and semantically questionable.
            // We only release the entries that have been written to the defunct channel.
            if( more is InputLogEntry e )
            {
                e.Release();
            }
            else if( more is IGrandOutputHandlersActionBase action )
            {
                action.Cancel();
            }
            else if( more is TaskCompletionSource asyncWait )
            {
                asyncWait.SetResult();
            }
            else if( more is SyncWaitSignal syncWait )
            {
                lock( syncWait )
                    Monitor.Pulse( syncWait );
            }
        }
        foreach( var h in _handlers )
        {
            await SafeActivateOrDeactivateAsync( monitor, h, false );
        }
        _stopTokenSource.Dispose();
        // Don't call monitor.MonitorEnd(); here as it's final log would 
        // be handled by other GrandOutput than this one and that is not
        // really interesting. Morevover we only use the the Default in
        // practice.
    }

    void IdentityCardOnChanged( IdentiCardChangedEvent change )
    {
        ExternalLog( LogLevel.Info | LogLevel.IsFiltered, ActivityMonitorSimpleSenderExtension.IdentityCard.IdentityCardUpdate, change.PackedAddedInfo, null, _sinkMonitorId );
    }

    async ValueTask DoConfigureAsync( IActivityMonitor monitor, GrandOutputConfiguration[] newConf )
    {
        Util.InterlockedSet( ref _newConf, t => t.Skip( newConf.Length ).ToArray() );
        var c = newConf[newConf.Length - 1];
        _filterChange( c.MinimalFilter, c.ExternalLogLevelFilter );
        if( c.TimerDuration.HasValue ) TimerDuration = c.TimerDuration.Value;
        SetUnhandledExceptionTracking( c.TrackUnhandledExceptions ?? _isDefaultGrandOutput );
        if( !string.IsNullOrEmpty( c.StaticGates ) ) StaticGateConfigurator.ApplyConfiguration( monitor, c.StaticGates );
        if( !string.IsNullOrEmpty( c.DotNetEventSources ) ) DotNetEventSourceConfigurator.ApplyConfiguration( monitor, c.DotNetEventSources );
        List<IGrandOutputHandler> toKeep = new List<IGrandOutputHandler>();
        for( int iConf = 0; iConf < c.Handlers.Count; ++iConf )
        {
            for( int iHandler = 0; iHandler < _handlers.Count; ++iHandler )
            {
                try
                {
                    if( await _handlers[iHandler].ApplyConfigurationAsync( monitor, c.Handlers[iConf] ) )
                    {
                        // Existing _handlers[iHandler] accepted the new c.Handlers[iConf].
                        c.Handlers.RemoveAt( iConf-- );
                        toKeep.Add( _handlers[iHandler] );
                        _handlers.RemoveAt( iHandler );
                        break;
                    }
                }
                catch( Exception ex )
                {
                    var h = _handlers[iHandler];
                    // Existing _handlers[iHandler] crashed with the proposed c.Handlers[iConf].
                    monitor.Fatal( $"Existing {h.GetType()} crashed with the configuration {c.Handlers[iConf].GetType()}.", ex );
                    // Since the handler can be compromised, we skip it from any subsequent
                    // attempt to reconfigure it and deactivate it.
                    _handlers.RemoveAt( iHandler-- );
                    await SafeActivateOrDeactivateAsync( monitor, h, false );
                }
            }
        }
        // Deactivate and get rid of remaining handlers.
        if( _handlers.Count > 0 )
        {
            foreach( var h in _handlers )
            {
                await SafeActivateOrDeactivateAsync( monitor, h, false );
            }
        }
        _handlers.Clear();
        // Restores reconfigured handlers.
        _handlers.AddRange( toKeep );
        // Creates and activates new handlers.
        // Rather than handling a special case for the IdentityCard, we use
        // a service provider to be able to extend the services one day.
        if( c.Handlers.Count > 0 )
        {
            using( var container = new SimpleServiceContainer() )
            {
                container.Add( _identityCard );
                foreach( var conf in c.Handlers )
                {
                    try
                    {
                        var h = GrandOutput.CreateHandler( conf, container );
                        if( await SafeActivateOrDeactivateAsync( monitor, h, true ) )
                        {
                            _handlers.Add( h );
                        }
                    }
                    catch( Exception ex )
                    {
                        monitor.Fatal( $"While creating handler for {conf.GetType()}.", ex );
                    }
                }
            }
        }
        if( _isDefaultGrandOutput )
        {
            ExternalLog( LogLevel.Info | LogLevel.IsFiltered, ActivityMonitor.Tags.Empty, $"GrandOutput.Default configuration n°{_configurationCount++}." );
        }
        lock( _confTrigger )
            Monitor.PulseAll( _confTrigger );
    }

    static async ValueTask<bool> SafeActivateOrDeactivateAsync( IActivityMonitor monitor, IGrandOutputHandler h, bool activate )
    {
        try
        {
            if( activate )
            {
                return await h.ActivateAsync( monitor );
            }
            await h.DeactivateAsync( monitor );
            return true;
        }
        catch( Exception ex )
        {
            monitor.Fatal( $"Handler {h.GetType()} crashed during {(activate ? "activation" : "de-activation")}.", ex );
            return false;
        }
    }

    /// <summary>
    /// Starts stopping this sink, returning true if and only if this call
    /// actually stopped it.
    /// </summary>
    /// <returns>
    /// True if this call stopped this sink, false if it has been already been stopped by another thread.
    /// </returns>
    internal bool Stop()
    {
        lock( _externalLogLock )
        {
            if( !_stopTokenSource.IsCancellationRequested )
            {
                _stopTokenSource.Cancel();
                _closingLog = CreateExternalLog( LogLevel.Info | LogLevel.IsFiltered, null, "Stopping GrandOutput.", null );
                if( _queue.Writer.TryWrite( _closingLog ) ) _queue.Writer.TryComplete();
                _identityCard.LocalUninitialize( _isDefaultGrandOutput );
                SetUnhandledExceptionTracking( false );
                return true;
            }
        }
        return false;
    }

    internal Task RunningTask => _runningTask;

    /// <summary>
    /// Handles a log entry.
    /// </summary>
    /// <param name="logEvent">The input log entry.</param>
    public void Handle( InputLogEntry logEvent )
    {
        // If we cannot write the entry, we must release it right now.
        // A race condition may appear here: Stop() calls TryWrite( CloseSentinel ) && TryComplete(),
        // and we may be here between the 2 calls which means that regular entries have been written
        // after the CloseSentinel. This is why the handling of the CloseSentinel drains any remaining
        // entries before leaving ProcessAsync.
        if( !_queue.Writer.TryWrite( logEvent ) )
        {
            logEvent.Release();
        }
    }

    /// <summary>
    /// Waits for the current waiting queue of entries to be dispatched.
    /// </summary>
    /// <returns>The awaitable.</returns>
    public Task SyncWaitAsync()
    {
        TaskCompletionSource c = new TaskCompletionSource( TaskCreationOptions.RunContinuationsAsynchronously );
        if( !_queue.Writer.TryWrite( c ) )
        {
            c.SetResult();
        }
        return c.Task;
    }

    sealed class SyncWaitSignal {}

    /// <summary>
    /// Waits for the current waiting queue of entries to be dispatched.
    /// </summary>
    public void SyncWait()
    {
        var c = new SyncWaitSignal();
        if( !_queue.Writer.TryWrite( c ) )
        {
            return;
        }
        lock( c )
        {
            Monitor.Wait( c );
        }
    }

    /// <summary>
    /// Submits a <see cref="GrandOutputHandlersAction"/> or <see cref="GrandOutputHandlersAction{TResult}"/>.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    public void Submit( IGrandOutputHandlersActionBase action )
    {
        Throw.CheckNotNullArgument( action );
        _queue.Writer.TryWrite( action );
    }

    internal void ApplyConfiguration( GrandOutputConfiguration configuration, bool waitForApplication )
    {
        Debug.Assert( configuration.InternalClone );
        Util.InterlockedAdd( ref _newConf, configuration );
        if( waitForApplication )
        {
            lock( _confTrigger )
            {
                GrandOutputConfiguration[] newConf;
                while( !_stopTokenSource.IsCancellationRequested && (newConf = _newConf) != null && newConf.Contains( configuration ) )
                    Monitor.Wait( _confTrigger );
            }
        }
    }

    internal void ExternalLog( LogLevel level,
                               CKTrait? tags,
                               string message,
                               Exception? ex = null,
                               string monitorId = ActivityMonitor.ExternalLogMonitorUniqueId )
    {
        InputLogEntry e = CreateExternalLog( level, tags, message, ex, monitorId );
        Handle( e );
    }

    InputLogEntry CreateExternalLog( LogLevel level, CKTrait? tags, string message, Exception? ex, string monitorId = ActivityMonitor.ExternalLogMonitorUniqueId )
    {
        DateTimeStamp prevLogTime;
        DateTimeStamp logTime;
        lock( _externalLogLock )
        {
            prevLogTime = _externalLogLastTime;
            _externalLogLastTime = logTime = new DateTimeStamp( _externalLogLastTime, DateTime.UtcNow );
        }
        var e = InputLogEntry.AcquireInputLogEntry( _sinkMonitorId,
                                                    monitorId,
                                                    prevLogTime,
                                                    string.IsNullOrEmpty( message ) ? ActivityMonitor.NoLogText : message,
                                                    logTime,
                                                    level,
                                                    tags ?? ActivityMonitor.Tags.Empty,
                                                    CKExceptionData.CreateFrom( ex ) );
        return e;
    }

    internal void OnStaticLog( ref ActivityMonitorLogData d )
    {
        var e = InputLogEntry.AcquireInputLogEntry( _sinkMonitorId,
                                                    ref d,
                                                    logType: LogEntryType.Line,
                                                    previousEntryType: LogEntryType.Line,
                                                    previousLogTime: DateTimeStamp.MinValue );
        Handle( e );
    }

    void SetUnhandledExceptionTracking( bool trackUnhandledException )
    {
        if( trackUnhandledException != _unhandledExceptionTracking )
        {
            if( trackUnhandledException )
            {
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            }
            else
            {
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            }
            _unhandledExceptionTracking = trackUnhandledException;
        }
    }

    void OnUnobservedTaskException( object? sender, UnobservedTaskExceptionEventArgs e )
    {
        ExternalLog( LogLevel.Fatal, GrandOutput.UnhandledException, "TaskScheduler.UnobservedTaskException raised.", e.Exception );
        e.SetObserved();
    }

    void OnUnhandledException( object sender, UnhandledExceptionEventArgs e )
    {
        if( e.ExceptionObject is Exception ex )
        {
            ExternalLog( LogLevel.Fatal, GrandOutput.UnhandledException, "AppDomain.CurrentDomain.UnhandledException raised.", ex );
        }
        else
        {
            string? exText = e.ExceptionObject.ToString();
            ExternalLog( LogLevel.Fatal, GrandOutput.UnhandledException, $"AppDomain.CurrentDomain.UnhandledException raised with Exception object '{exText}'." );
        }
    }

    internal void AddDynamicHandler( IGrandOutputHandler handler )
    {
        _queue.Writer.TryWrite( handler );
    }

    internal void RemoveDynamicHandler( Handlers.IDynamicGrandOutputHandler handler )
    {
        _queue.Writer.TryWrite( handler );
    }

}
