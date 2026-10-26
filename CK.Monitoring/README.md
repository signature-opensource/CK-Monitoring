# CK.Monitoring: the GrandOutput

`CK.Monitoring` is the [sink](https://en.wikipedia.org/wiki/Sink_(computing)) side of the
[ActivityMonitor](https://github.com/signature-opensource/CK-ActivityMonitor/blob/stable/CK.ActivityMonitor/README.md): monitors emit log entries,
this package collects them centrally and dispatches them to configurable handlers.

> ℹ️ If you are not already familiar with the [ActivityMonitor](https://github.com/signature-opensource/CK-ActivityMonitor/blob/stable/CK.ActivityMonitor/README.md),
> read its documentation first.

> ℹ️ In a hosted application, everything described below is normally driven from the "CK-Monitoring"
> section of the application configuration rather than through this API.

## A GrandOutput is a singleton in practice, not by type.

A [`GrandOutput`](GrandOutput.cs) is a collector for the logs sent to ActivityMonitors. Technically
several instances can coexist - the hosting integration exposes that for advanced scenarios - but
in practice we always use the static `GrandOutput.Default` property. The type is not a singleton
because tests, and hosts running inside another host, need to own their own sink.

The simplest activation is:

```csharp
GrandOutput.EnsureActiveDefault();
```

It must run **before any ActivityMonitor is instantiated**: a monitor created earlier is not attached
to the sink. Called without configuration, the default is equivalent to:

```csharp
new GrandOutputConfiguration().AddHandler(
    new Handlers.TextFileConfiguration()
    {
      Path = "Text",
      TimedFolderMode = { MaxCurrentLogFolderCount = 5 }
    })
```

## Handlers are where the logs actually land.

The handlers shipped in this assembly are:

| Handler | Writes logs to | Usage | Metadata kept |
|---------|----------------|-------|---------------|
| [BinaryFile](Handlers/BinaryFile.cs) | binary file (extension `.ckmon`), optionally compressed. | To be programmatically read. | All of it. |
| [TextFile](Handlers/TextFile.cs) | text files. | To read when no console can be shown, or when persistence is needed. When developing or in production. | log, date, exceptions, monitor ID, and loglevel. |
| [Console](Handlers/Console.cs) | the console. | To read the program output when developing. Does not persist the logs. | log, date, exceptions, monitor ID, and loglevel. |

`BinaryFile` is the only lossless one: the `.ckmon` format keeps every field, which is what the
[log readers](Persistence/LogReader.cs) and [`MultiLogReader`](Persistence/MultiLogReader.cs) need to
rebuild an activity map. Text output is for humans and loses structure.

## Configuring and reconfiguring by hand.

⚠ This is an advanced usage. Skip this part if your GrandOutput is configured from the application
configuration.

The root of the log folders is `LogFile.RootLogPath`. It is initially null, must be an absolute path,
and **can be set only once** - so set it before activating the GrandOutput:

```csharp
  // Sets the absolute root of the log folder.
  // It must be an absolute path and is typically a subfolder of the current application.
  LogFile.RootLogPath = "/RootLogPath";
  GrandOutput.EnsureActiveDefault();
```

From now on, any new ActivityMonitor logs will be routed into text files inside the "/RootLogPath/Text"
directory.

A [`GrandOutputConfiguration`](GrandOutputConfiguration.cs) carries the handler configurations:

```csharp
  LogFile.RootLogPath = System.IO.Path.Combine( AppContext.BaseDirectory, "Logs" );
  var conf = new GrandOutputConfiguration()
                  .SetTimerDuration( TimeSpan.FromSeconds(1) ) // 500ms is the default value.
                  .AddHandler( new Handlers.BinaryFileConfiguration()
                  {
                      Path = "OutputGzip",
                      UseGzipCompression = true
                  })
                  .AddHandler( new Handlers.BinaryFileConfiguration()
                  {
                      Path = "OutputRaw",
                      UseGzipCompression = false
                  }).AddHandler( new Handlers.TextFileConfiguration()
                  {
                      Path = "Text",
                      MaxCountPerFile = 500
                  });
  GrandOutput.EnsureActiveDefault( conf );
```

The GrandOutput can be reconfigured at any time, and can also be disposed - `GrandOutput.Default` is
then reset to null. **Reconfiguration is a diff, not a restart**: create/update/delete of the running
handlers is computed from a key that depends on each handler type. For the file handlers that key is
the `Path`, so changing `MaxCountPerFile` on the same `Path` updates the live handler instead of
closing the file and opening a new one.

## Writing your own handler.

⚠ This is an advanced usage.

A handler implements [`IGrandOutputHandler`](Handlers/IGrandOutputHandler.cs):

```csharp
/// <summary>
/// Handler interface.
/// Object implementing this interface must expose a public constructor that accepts
/// its associated <see cref="IHandlerConfiguration"/> object.
/// </summary>
public interface IGrandOutputHandler
{
    /// <summary>
    /// Prepares the handler to receive events.
    /// This is called before any event will be received.
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    /// <returns>True on success, false on error (this handler will not be added).</returns>
    ValueTask<bool> ActivateAsync( IActivityMonitor monitor );

    /// <summary>
    /// Called on a regular basis.
    /// Enables this handler to do any required housekeeping.
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    /// <param name="timerSpan">Indicative timer duration.</param>
    ValueTask OnTimerAsync( IActivityMonitor monitor, TimeSpan timerSpan );

    /// <summary>
    /// Handles a log event.
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    /// <param name="logEvent">The log event.</param>
    ValueTask HandleAsync( IActivityMonitor monitor, InputLogEntry logEvent );

    /// <summary>
    /// Attempts to apply configuration if possible.
    /// The handler must check the type of the given configuration and any key configuration
    /// before accepting it and reconfigures it (in such case, true must be returned).
    /// If this handler considers that this new configuration does not apply to itself, it must return false.
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    /// <param name="c">Configuration to apply.</param>
    /// <returns>True if the configuration applied.</returns>
    ValueTask<bool> ApplyConfigurationAsync( IActivityMonitor monitor, IHandlerConfiguration c );

    /// <summary>
    /// Closes this handler.
    /// This is called after the handler has been removed.
    /// </summary>
    /// <param name="monitor">The monitor to use.</param>
    ValueTask DeactivateAsync( IActivityMonitor monitor );
}
```

`ApplyConfigurationAsync` is the interesting one: the handler itself decides whether an incoming
configuration is "the same handler reconfigured" or "a different handler". That is how the
reconfiguration diff described above stays extensible.

Its configuration fulfills an even simpler contract -
[`IHandlerConfiguration`](Handlers/IHandlerConfiguration.cs):

```csharp
    public interface IHandlerConfiguration
    {
        /// <summary>
        /// Must return a deep clone of this configuration object.
        /// </summary>
        IHandlerConfiguration Clone();
    }
```

### Naming conventions are load-bearing.

Handlers are resolved by convention, not by registration. For a handler named "MailAlerter":

- The assembly that implements the handler and its configuration must be
  "CK.Monitoring.MailAlerterHandler" (file `CK.Monitoring.MailAlerterHandler.dll`).
- The handler and its configuration must both be in the "CK.Monitoring.Handlers" namespace.
- The configuration type name must be "MailAlerterConfiguration"
  (full name "CK.Monitoring.Handlers.MailAlerterConfiguration").
- The handler type name must be "MailAlerter" (full name "CK.Monitoring.Handlers.MailAlerter").

```csharp
namespace CK.Monitoring.Handlers
{
  public class MailAlerterConfiguration : IHandlerConfiguration
  {
    public string? Email { get; set; }
    //...
  }

  public class MailAlerter : IGrandOutputHandler
  {
    MailAlerterConfiguration _config;

    public MailAlerter( MailAlerterConfiguration c )
    {
      _config = c;
    }

    //...
  }
}
```

With these conventions in place, this configuration (through the json configuration provider):

```json
{
  "CK-Monitoring": {
    "GrandOutput": {
      "Handlers": {
        "Console": true,
        "MailAlerter": {
          "Email": "stupid-dev@signature-code.com"
        }
      }
    }
  }
}
```

loads the "CK.Monitoring.MailAlerterHandler" assembly (it must be in the binary folder of the
application, of course), instantiates the configuration and the handler, and activates it.

Sample code:
[MailAlerterConfiguration](../Tests/CK.Monitoring.MailAlerterHandler/Handler/MailAlerterConfiguration.cs)
and [MailAlerter](../Tests/CK.Monitoring.MailAlerterHandler/Handler/MailAlerter.cs).

## Acting on the running handlers.

⚠ This is an advanced usage.

The running handlers belong to the GrandOutput's dispatcher: they can be reached only by submitting a
`GrandOutputHandlersAction` to it. See [Handler actions](Handlers/HandlersAction/README.md).

## Handler configurations must never leave the host.

GrandOutput configuration and handler configurations must not be serialized and exchanged with the
external world. They must remain local, like a hidden implementation detail of the running host: a
handler configuration names assemblies to load and paths to write to.

If a kind of "remote log configuration feature" is needed, it must be done through specific code and
only strictly controlled changes must be allowed - never by accepting a configuration object coming
from outside.
