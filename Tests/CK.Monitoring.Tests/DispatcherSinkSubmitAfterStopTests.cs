using CK.Core;
using NUnit.Framework;
using Shouldly;
using System;
using System.Threading.Tasks;

namespace CK.Monitoring.Tests;

/// <summary>
/// Once the sink is stopped, nothing can be queued anymore: a submitted action is canceled, like the
/// actions still queued when the sink stops. Its completion must never hang.
/// </summary>
[TestFixture]
public class DispatcherSinkSubmitAfterStopTests
{
    sealed class NoOpAction : GrandOutputHandlersAction
    {
        protected override ValueTask RunAsync( IActivityMonitor monitor, DispatcherSink.HandlerList handlers ) => default;
    }

    sealed class NoOpHandler : IGrandOutputHandler
    {
        public ValueTask<bool> ActivateAsync( IActivityMonitor monitor ) => ValueTask.FromResult( true );
        public ValueTask<bool> ApplyConfigurationAsync( IActivityMonitor monitor, IHandlerConfiguration c ) => ValueTask.FromResult( false );
        public ValueTask DeactivateAsync( IActivityMonitor monitor ) => default;
        public ValueTask HandleAsync( IActivityMonitor monitor, InputLogEntry logEvent ) => default;
        public ValueTask OnTimerAsync( IActivityMonitor monitor, TimeSpan timerSpan ) => default;
    }

    static async Task<DispatcherSink> GetStoppedSinkAsync()
    {
        var g = new GrandOutput( new GrandOutputConfiguration() );
        await g.DisposeAsync();
        return g.Sink;
    }

    [Test]
    public async Task an_action_submitted_after_stop_is_canceled_Async()
    {
        var sink = await GetStoppedSinkAsync();
        var action = new NoOpAction();
        sink.Submit( action );
        (await Task.WhenAny( action.Completion, Task.Delay( 5000 ) )).ShouldBeSameAs( action.Completion, "The completion hangs." );
        action.Completion.IsCanceled.ShouldBeTrue();
    }

    [Test]
    public async Task adding_or_removing_a_handler_after_stop_is_canceled_Async()
    {
        var sink = await GetStoppedSinkAsync();
        var add = sink.AddHandlerAsync( new NoOpHandler() );
        var remove = sink.RemoveHandlerAsync( new NoOpHandler() );
        var both = Task.WhenAll( add, remove );
        (await Task.WhenAny( both, Task.Delay( 5000 ) )).ShouldBeSameAs( both, "The completion hangs." );
        add.IsCanceled.ShouldBeTrue();
        remove.IsCanceled.ShouldBeTrue();
    }
}
