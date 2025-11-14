using System;
using System.Collections.Generic;
using System.Threading;
using CK.Core;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace CK.Monitoring;

/// <summary>
/// A GrandOutput collects activity of multiple <see cref="IActivityMonitor"/>. 
/// It is usually useless to explicitly create an instance of GrandOutput: the <see cref="Default"/> one is 
/// available as soon as <see cref="EnsureActiveDefault"/> is called 
/// and will be automatically used by new <see cref="ActivityMonitor"/>.
/// </summary>
public sealed partial class GrandOutput : IAsyncDisposable
{
    readonly List<WeakReference<GrandOutputClient>> _clients;
    readonly DispatcherSink _sink;
    readonly IdentityCard.ReadOnly _identityCard;
    LogFilter _minimalFilter;

    static GrandOutput? _default;
    static MonitorTraceListener? _traceListener;
    static readonly object _defaultLock = new object();

    /// <summary>
    /// The tag that marks all external log entry sent by <see cref="AppDomain.UnhandledException"/>
    /// and <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/>.
    /// </summary>
    public static readonly CKTrait UnhandledException = ActivityMonitor.Tags.Register( "UnhandledException" );

    /// <summary>
    /// The unknown grand output identifier.
    /// </summary>
    public static readonly string UnknownGrandOutputId = "§none";

    /// <summary>
    /// Gets the default <see cref="GrandOutput"/> for the current Application Domain.
    /// Note that <see cref="EnsureActiveDefault"/> must have been called, otherwise this static property is null
    /// and that this Default can be <see cref="DisposeAsync()"/> at any time (this static property will be set back to null).
    /// </summary>
    public static GrandOutput? Default => _default;

    /// <summary>
    /// Ensures that the <see cref="Default"/> GrandOutput is created and that any <see cref="ActivityMonitor"/> that will be created in this
    /// application domain will automatically have a <see cref="GrandOutputClient"/> registered for this Default GrandOutput.
    /// If the Default is already initialized, the <paramref name="configuration"/> is applied.
    /// </summary>
    /// <param name="configuration">
    /// Configuration to apply to the default GrandOutput.
    /// When null, a default configuration with a <see cref="Handlers.TextFileConfiguration"/> in a "Text" path is configured.
    /// </param>
    /// <param name="clearExistingTraceListeners">
    /// If the <see cref="Default"/> is actually instantiated, existing <see cref="Trace.Listeners"/>
    /// are cleared before registering a <see cref="MonitorTraceListener"/> associated to this default grand output.
    /// See remarks.
    /// </param>
    /// <returns>The Default GrandOutput that has been created or reconfigured.</returns>
    /// <remarks>
    /// <para>
    /// This method is thread-safe (a simple lock protects it) and uses a <see cref="ActivityMonitor.AutoConfiguration"/> action 
    /// that uses <see cref="EnsureGrandOutputClient(IActivityMonitor)"/> on newly created ActivityMonitor.
    /// </para>
    /// <para>
    /// The Default GrandOutput also adds a <see cref="MonitorTraceListener"/> in the <see cref="Trace.Listeners"/> collection that
    /// has <see cref="MonitorTraceListener.FailFast"/> sets to false: <see cref="MonitoringFailFastException"/> are thrown instead of
    /// calling <see cref="Environment.FailFast(string)"/>.
    /// If this behavior must be changed, please exploit the <see cref="Trace.Listeners"/> that is a <see cref="TraceListenerCollection"/>, wide open
    /// to any modifications, and the fact that <see cref="MonitorTraceListener"/> exposes its associated grand output and
    /// that <see cref="MonitorTraceListener.FailFast"/> property can be changed at any time.
    /// </para>
    /// <para>
    /// The GrandOutput.Default can safely be <see cref="DisposeAsync()"/> at any time: disposing the Default 
    /// sets it to null.
    /// </para>
    /// </remarks>
    public static GrandOutput EnsureActiveDefault( GrandOutputConfiguration? configuration = null, bool clearExistingTraceListeners = true )
    {
        lock( _defaultLock )
        {
            if( _default == null )
            {
                configuration ??= CreateInternalDefaultConfiguration();
                _default = new GrandOutput( true, configuration );
                ActivityMonitor.AutoConfiguration += AutoRegisterDefault;
                _traceListener = new MonitorTraceListener( _default, failFast: false );
                if( clearExistingTraceListeners ) Trace.Listeners.Clear();
                Trace.Listeners.Add( _traceListener );
            }
            else if( configuration != null ) _default.ApplyConfiguration( configuration, true );
        }
        return _default;
    }

    static GrandOutputConfiguration CreateInternalDefaultConfiguration()
    {
        GrandOutputConfiguration? configuration = new GrandOutputConfiguration()
                            .AddHandler( new Handlers.TextFileConfiguration() { Path = "Text", TimedFolderMode = { MaxCurrentLogFolderCount = 5 } } );
        configuration.InternalClone = true;
        return configuration;
    }

    static void AutoRegisterDefault( IActivityMonitor m )
    {
        Default?.EnsureGrandOutputClient( m );
    }

    /// <summary>
    /// Applies a configuration.
    /// This is thread safe and can be called at any moment.
    /// </summary>
    /// <param name="configuration">The configuration to apply.</param>
    /// <param name="waitForApplication">
    /// True to block until this configuration has been applied.
    /// Note that another (new) configuration may have already replaced the given configuration
    /// once this call ends.
    /// </param>
    public void ApplyConfiguration( GrandOutputConfiguration configuration, bool waitForApplication = false )
    {
        Throw.CheckNotNullArgument( configuration );
        if( !configuration.InternalClone )
        {
            configuration = configuration.Clone();
            configuration.InternalClone = true;
        }
        _sink.ApplyConfiguration( configuration, waitForApplication );
    }

    /// <summary>
    /// Settable factory method for <see cref="IGrandOutputHandler"/>.
    /// Default implementation relies on Handlers that must be in the same 
    /// assembly and namespace as their configuration objects and named the 
    /// same without the "Configuration" suffix.
    /// </summary>
    public static Func<IHandlerConfiguration, IServiceProvider, IGrandOutputHandler> CreateHandler { get; set; } = ( config, sp ) =>
        {
            var t = config.GetType();
            var name = t.FullName;
            Debug.Assert( name != null && t.AssemblyQualifiedName != null );
            if( !name.EndsWith( "Configuration" ) ) Throw.Exception( $"Configuration handler type name must end with 'Configuration': {name}." );
            name = t.AssemblyQualifiedName.Replace( "Configuration,", "," );
            t = Type.GetType( name, throwOnError: true );
            Debug.Assert( t != null );
            return (IGrandOutputHandler)ActivatorUtilities.CreateInstance( sp, t, config );
        };

    static GrandOutput()
    {
        // Attempts to block the destruction until logs are
        // flushed... Just an attempt...
        static void HardStop( object? o, EventArgs e ) => Default?.StopAndWaitForTermination();
        AppDomain.CurrentDomain.DomainUnload += HardStop;
        AppDomain.CurrentDomain.ProcessExit += HardStop;
    }

    /// <summary>
    /// Initializes a new independent <see cref="GrandOutput"/> with a default configuration (the default "Text" handler only).
    /// </summary>
    public GrandOutput()
        : this( false, CreateInternalDefaultConfiguration() )
    {
    }

    /// <summary>
    /// Initializes a new independent <see cref="GrandOutput"/>. 
    /// </summary>
    /// <param name="config">The configuration.</param>
    public GrandOutput( GrandOutputConfiguration config )
        : this( false, config )
    {
    }

    GrandOutput( bool isDefault, GrandOutputConfiguration config )
    {
        Throw.CheckNotNullArgument( config );
        // Creates the identity card and the client list first.
        var identityCard = new IdentityCard();
        _identityCard = identityCard.AsReadOnly();
        _clients = new List<WeakReference<GrandOutputClient>>();
        _minimalFilter = LogFilter.Undefined;
        // Starts the Sink agent. Its monitor will be registered
        // in this GrandOutput.
        _sink = new DispatcherSink( m => DoEnsureGrandOutputClient( m ),
                                    identityCard,
                                    config.TimerDuration ?? TimeSpan.FromMilliseconds( 500 ),
                                    TimeSpan.FromMinutes( 5 ),
                                    DoGarbageDeadClients,
                                    OnFiltersChanged,
                                    isDefault );
        ApplyConfiguration( config, waitForApplication: true );
        ActivityMonitor.OnStaticLog += _sink.OnStaticLog;
    }

    /// <summary>
    /// Ensures that a client for this GrandOutput is registered on a monitor.
    /// There is no need to call this method for the <see cref="Default"/> GrandOutput since
    /// clients are automatically registered for newly created <see cref="ActivityMonitor"/> (thanks
    /// to a <see cref="ActivityMonitor.AutoConfiguration"/> hook).
    /// </summary>
    /// <param name="monitor">The monitor onto which a <see cref="GrandOutputClient"/> must be registered.</param>
    /// <returns>A newly created client or the already existing one.</returns>
    public GrandOutputClient EnsureGrandOutputClient( IActivityMonitor monitor )
    {
        Throw.CheckNotNullArgument( monitor );
        var c = DoEnsureGrandOutputClient( monitor );
        if( c == null ) Throw.ObjectDisposedException( nameof( GrandOutput ) );
        return c;
    }

    GrandOutputClient? DoEnsureGrandOutputClient( IActivityMonitor monitor )
    {
        GrandOutputClient? DoRegister()
        {
            var c = new GrandOutputClient( this );
            lock( _clients )
            {
                if( _sink.StoppingToken.IsCancellationRequested ) c = null;
                else _clients.Add( new WeakReference<GrandOutputClient>( c ) );
            }
            return c;
        }
        return monitor.Output.RegisterUniqueClient( b => b.Central == this, DoRegister, replayInitialLogs: true );
    }

    /// <summary>
    /// Gets the read only identity card of this GrandOutput.
    /// </summary>
    public IdentityCard.ReadOnly IdentityCard => _identityCard;

    /// <summary>
    /// Gets this GrandOutput identifier: this is the identifier of the dispatcher sink monitor.
    /// </summary>
    public string GrandOutpuId => _sink.SinkMonitorId;

    /// <summary>
    /// Gets or directly sets the filter level for ExternalLog methods (without using the <see cref="GrandOutputConfiguration.ExternalLogLevelFilter"/> configuration).
    /// Defaults to <see cref="LogLevelFilter.None"/> (<see cref="ActivityMonitor.DefaultFilter"/>.<see cref="LogFilter.Line">Line</see>
    /// is used).
    /// Note that <see cref="ApplyConfiguration(GrandOutputConfiguration, bool)"/> changes this property.
    /// </summary>
    public LogLevelFilter ExternalLogLevelFilter { get; set; }

    /// <summary>
    /// Gets or directly sets the minimal filter (without using the <see cref="GrandOutputConfiguration.MinimalFilter"/> configuration).
    /// This minimal filter impacts all the <see cref="IActivityMonitor"/> that are bound to the <see cref="GrandOutput"/>
    /// (through the <see cref="GrandOutputClient"/>).
    /// Default to <see cref="LogFilter.Undefined"/>: there is no impact on each <see cref="IActivityMonitor.ActualFilter"/>.
    /// Note that <see cref="ApplyConfiguration(GrandOutputConfiguration, bool)"/> changes this property.
    /// </summary>
    public LogFilter MinimalFilter
    {
        get => _minimalFilter;
        set
        {
            var m = _minimalFilter;
            if( m != value )
            {
                _minimalFilter = value;
                SignalClients();
            }
        }
    }

    void OnFiltersChanged( LogFilter? minimalFilter, LogLevelFilter? externalLogFilter )
    {
        if( minimalFilter.HasValue ) MinimalFilter = minimalFilter.Value;
        if( externalLogFilter.HasValue ) ExternalLogLevelFilter = externalLogFilter.Value;
    }

    /// <summary>
    /// Gets whether an <see cref="ExternalLog(LogLevel, string, Exception?)"/> level should be emitted.
    /// </summary>
    /// <param name="level">Log level to test.</param>
    /// <returns>True if this level should be logged otherwise false.</returns>
    public bool IsExternalLogEnabled( LogLevel level ) => ((int)level & (int)LogLevel.Mask) >= (ExternalLogLevelFilter == LogLevelFilter.None
                                                                           ? (int)ActivityMonitor.DefaultFilter.Line
                                                                           : (int)ExternalLogLevelFilter);
    /// <summary>
    /// Gets whether an <see cref="ExternalLog(LogLevel, CKTrait, string, Exception?)"/> level should be emitted.
    /// </summary>
    /// <param name="level">Log level to test.</param>
    /// <param name="tags">Log tags to test.</param>
    /// <returns>True if this should be logged otherwise false.</returns>
    public bool IsExternalLogEnabled( LogLevel level, CKTrait tags ) => ActivityMonitor.Tags.ApplyForLine( level, tags, ExternalLogLevelFilter );

    /// <summary>
    /// Logs an entry from any contextless source to this GrandOutput only (as opposed to using <see cref="ActivityMonitor.StaticLogger"/>
    /// that will be handled by all existing GrandOutput instances).
    /// </summary>
    /// <remarks>
    /// We consider that as long has the log level has <see cref="LogLevel.IsFiltered">IsFiltered</see> bit
    /// set, the decision has already been taken and here we do our job: dispatching the log.
    /// But for logs that do not claim to have been filtered, we challenge the <see cref="ExternalLogLevelFilter"/>.
    /// </remarks>
    /// <param name="level">Log level.</param>
    /// <param name="tags">Optional tags (that must belong to <see cref="ActivityMonitor.Tags.Context"/>).</param>
    /// <param name="message">String message.</param>
    /// <param name="ex">Optional exception.</param>
    public void ExternalLog( LogLevel level, CKTrait tags, string message, Exception? ex = null )
    {
        if( (level & LogLevel.IsFiltered) != 0
            || ActivityMonitor.Tags.ApplyForLine( level, tags, ExternalLogLevelFilter ) )
        {
            _sink.ExternalLog( level, tags, message, ex );
        }
    }

    /// <summary>
    /// Logs an entry from any contextless source to this GrandOutput only (as opposed to using <see cref="ActivityMonitor.StaticLogger"/>
    /// that will log to all existing GrandOutput instances).
    /// </summary>
    /// <remarks>
    /// We consider that as long has the log level has <see cref="CK.Core.LogLevel.IsFiltered">IsFiltered</see> bit
    /// set, the decision has already being taken and here we do our job: dispatching of the log.
    /// But for logs that do not claim to have been filtered, we challenge the <see cref="ExternalLogLevelFilter"/>.
    /// </remarks>
    /// <param name="level">Log level.</param>
    /// <param name="message">String message.</param>
    /// <param name="ex">Optional exception.</param>
    public void ExternalLog( LogLevel level, string message, Exception? ex = null )
    {
        if( (level & LogLevel.IsFiltered) != 0
            || ((int)level & (int)LogLevel.Mask) >= (ExternalLogLevelFilter == LogLevelFilter.None
                                                                           ? (int)ActivityMonitor.DefaultFilter.Line
                                                                           : (int)ExternalLogLevelFilter) )
        {
            _sink.ExternalLog( level, null, message, ex );
        }
    }

    /// <summary>
    /// Creates a new in-memory collector of <see cref="ILogEntry"/>. This collector must be disposed
    /// once done with it.
    /// <para>
    /// This is intended to be used in tests: there is little to no interest to use this collector elsewhere.
    /// </para>
    /// </summary>
    /// <param name="maxCapacity">The maximal number of collected entries: oldest entries are automatically discarded.</param>
    /// <param name="ignoreCloseGroup">False to also collect <see cref="LogEntryType.CloseGroup"/>.</param>
    /// <returns>A collector that must be disposed.</returns>
    public GrandOutputMemoryCollector CreateMemoryCollector( int maxCapacity, bool ignoreCloseGroup = true )
    {
        return new GrandOutputMemoryCollector( _sink, maxCapacity, ignoreCloseGroup );
    }

    /// <summary>
    /// Gets a cancellation token that is cancelled by the <see cref="Stop()"/>.
    /// </summary>
    public CancellationToken StoppedToken => _sink.StoppingToken;

    void DoGarbageDeadClients()
    {
        lock( _clients )
        {
            for( int i = 0; i < _clients.Count; ++i )
            {
                if( !_clients[i].TryGetTarget( out GrandOutputClient? cw ) || !cw.IsBoundToMonitor )
                {
                    _clients.RemoveAt( i-- );
                }
            }
        }
    }

    /// <summary>
    /// Gets the log event dispatcher.
    /// </summary>
    public DispatcherSink Sink => _sink;

    /// <summary>
    /// Definitely stops this GrandOutput. This can be called concurrently and multiple times.
    /// If this is the default one that is stopped, <see cref="Default"/> is set to null.
    /// <para>
    /// This doesn't wait for the internal agent to terminate. Use <see cref="RunningTask"/> to
    /// await for the actual agent termination.
    /// </para>
    /// </summary>
    /// <returns>
    /// True if this call stopped this GrandOutput, false if it has been already been stopped by another thread.
    /// </returns>
    public bool Stop()
    {
        if( !_sink.Stop() ) return false;
        lock( _defaultLock )
        {
            if( _default == this )
            {
                ActivityMonitor.AutoConfiguration -= AutoRegisterDefault;
                Throw.DebugAssert( _traceListener != null );
                Trace.Listeners.Remove( _traceListener );
                _traceListener = null;
                _default = null;
            }
        }
        ActivityMonitor.OnStaticLog -= _sink.OnStaticLog;
        SignalClients();
        return true;
    }

    internal void StopAndWaitForTermination()
    {
        Stop();
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        _sink.RunningTask.Wait();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
    }

    /// <summary>
    /// Gets a task that will be completed once the internal agent will be done.
    /// </summary>
    public Task RunningTask => _sink.RunningTask;

    /// <summary>
    /// Calss <see cref="Stop"/> and await for the <see cref="RunningTask"/>.
    /// If this is the default one that is disposed, <see cref="Default"/> is set to null.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Stop();
        var t = _sink.RunningTask;
        return t.IsCompleted ? default : new ValueTask( t );
    }

    void SignalClients()
    {
        lock( _clients )
        {
            for( int i = 0; i < _clients.Count; ++i )
            {
                if( _clients[i].TryGetTarget( out GrandOutputClient? cw ) && cw.IsBoundToMonitor )
                {
                    cw.OnGrandOutputDisposedOrMinimalFilterChanged();
                }
            }
        }
    }
}
