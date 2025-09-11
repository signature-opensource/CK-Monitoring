using CK.Core;
using Shouldly;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace CK.Monitoring.Hosting.Tests;


public class DemoSinkHandler : IGrandOutputHandler
{
    DemoSinkHandlerConfiguration _config;

    public static int ActivateCount;
    public static int ApplyConfigurationCount;
    public static ConcurrentBag<InputLogEntry> LogEvents = new();
    public static int DeactivateCount;
    public static int OnTimerCount;

    public static void Reset()
    {
        ActivateCount = 0;
        ApplyConfigurationCount = 0;
        foreach( var e in LogEvents ) e.Release();
        LogEvents.Clear();
        DeactivateCount = 0;
        OnTimerCount = 0;
    }

    public static async Task ResetAndDisposeGrandOutputDefaultAsync()
    {
        var existingGrandOutput = GrandOutput.Default;
        if( existingGrandOutput != null ) await existingGrandOutput.DisposeAsync();
        GrandOutput.Default.ShouldBeNull();

        ActivateCount = 0;
        ApplyConfigurationCount = 0;
        foreach( var e in LogEvents ) e.Release();
        LogEvents.Clear();
        DeactivateCount = 0;
        OnTimerCount = 0;
    }

    public DemoSinkHandler( DemoSinkHandlerConfiguration c )
    {
        _config = c;
    }

    public ValueTask<bool> ActivateAsync( IActivityMonitor m )
    {
        m.Info( "DemoSinkHandler: Activation!" );
        Interlocked.Increment( ref ActivateCount );
        return ValueTask.FromResult( true );
    }

    public ValueTask<bool> ApplyConfigurationAsync( IActivityMonitor m, IHandlerConfiguration c )
    {
        var conf = c as DemoSinkHandlerConfiguration;
        if( conf != null )
        {
            m.Info( "DemoSinkHandler: ApplyConfiguration!" );
            Interlocked.Increment( ref ApplyConfigurationCount );
            return ValueTask.FromResult( true );
        }
        return ValueTask.FromResult( false );
    }

    public ValueTask HandleAsync( IActivityMonitor m, InputLogEntry logEvent )
    {
        logEvent.AddRef();
        LogEvents.Add( logEvent );
        return ValueTask.CompletedTask;
    }

    public ValueTask DeactivateAsync( IActivityMonitor m )
    {
        m.Info( "DemoSinkHandler: Deactivation!" );
        Interlocked.Increment( ref DeactivateCount );
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTimerAsync( IActivityMonitor m, TimeSpan timerSpan )
    {
        m.Info( "DemoSinkHandler: OnTimer!" );
        Interlocked.Increment( ref OnTimerCount );
        return ValueTask.CompletedTask;
    }

}
