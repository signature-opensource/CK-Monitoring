# Handler actions

The activated [`IGrandOutputHandler`](../IGrandOutputHandler.cs) belong to the GrandOutput's
`DispatcherSink`: the dispatcher loop calls them one log entry at a time. Code that needs to act on the
running handlers (find one, call one of its methods, add or remove a handler) must not do it from
another thread: it submits an action that the dispatcher loop executes between two log entries, so that no
handler is concurrently handling a log entry.

## The action types

An action is a template method. Its `RunAsync` receives the dispatcher's monitor and the
`DispatcherSink.HandlerList`:

- [`GrandOutputHandlersAction`](GrandOutputHandlersAction.cs) is an action without result:
  ```csharp
  protected abstract ValueTask RunAsync( IActivityMonitor monitor, DispatcherSink.HandlerList handlers );
  ```
- [`GrandOutputHandlersAction<TResult>`](GrandOutputHandlersAction{T}.cs) is an action with a result:
  ```csharp
  protected abstract ValueTask<TResult> RunAsync( IActivityMonitor monitor, DispatcherSink.HandlerList handlers );
  ```

Both implement [`IGrandOutputHandlersActionBase`](IGrandOutputHandlersActionBase.cs) (that cannot be
implemented outside of CK.Monitoring) and expose a `Completion` task (a `Task<TResult>` for the action with
a result) that completes once the action has been executed. An exception thrown by `RunAsync` faults the
`Completion`. Actions that are still waiting in the dispatcher's queue when the GrandOutput is disposed
are canceled, and so are the ones submitted once it is disposed.

## Submitting an action

An action is submitted to the `DispatcherSink` exposed by `GrandOutput.Sink`:
```csharp
public void Submit( IGrandOutputHandlersActionBase action )
```
An action can be used only once: submitting an action whose `Completion` is completed throws an
`ArgumentException`.

## The HandlerList

The `DispatcherSink.HandlerList` is only available to the actions:

- `IReadOnlyList<IGrandOutputHandler> Handlers` gets the current activated handlers.
- `ValueTask<bool> AddAsync( IActivityMonitor monitor, IGrandOutputHandler handler )` activates the handler
  and, on success, adds it to the `Handlers`.
- `ValueTask<bool> RemoveAsync( IActivityMonitor monitor, IGrandOutputHandler handler )` deactivates the
  handler and removes it from the `Handlers` regardless of the success of the deactivation.

An action must not call `IGrandOutputHandler.ActivateAsync` or `IGrandOutputHandler.DeactivateAsync`
itself: it uses `AddAsync` and `RemoveAsync` instead.

The `DispatcherSink` itself uses such an action to implement its `AddHandlerAsync`, `RemoveHandlerAsync`,
`SubmitAddHandler` and `SubmitRemoveHandler` methods:
```csharp
    sealed class AddRemoveHandler : GrandOutputHandlersAction<bool>
    {
        readonly IGrandOutputHandler _handler;
        readonly bool _add;

        public AddRemoveHandler( IGrandOutputHandler handler, bool add )
        {
            _handler = handler;
            _add = add;
        }

        protected override ValueTask<bool> RunAsync( IActivityMonitor monitor, HandlerList handlers )
        {
            return _add ? handlers.AddAsync( monitor, _handler ) : handlers.RemoveAsync( monitor, _handler );
        }
    }
```

The [`TextFile`](../TextFile.cs) and [`BinaryFile`](../BinaryFile.cs) handlers expose
`string? CloseCurrentFile( bool forgetCurrentFile = false )` for such actions: an action can find them in
the `Handlers` and explicitly close (or forget) their current file.
