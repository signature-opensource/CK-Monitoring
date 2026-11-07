# CK.Monitoring.Hosting: monitoring from the configuration

This package plugs [CK.Monitoring](../CK.Monitoring/README.md) into a
[.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host) so that the
`GrandOutput.Default` is created and kept in sync with the "CK-Monitoring" section of the
application configuration. This is what an application should use: it brings `CK.Monitoring` along and
configures it from `appsettings.json`. The manual API described in
[CK.Monitoring](../CK.Monitoring/README.md) then only matters when you write a handler.

> ℹ️ For an example of an application configured with this package that dynamically reacts to the changes
> of its appsettings, see the [CK-Sample-Monitoring](https://github.com/signature-opensource/CK-Sample-Monitoring/blob/stable/README.md)
> repository: its [appsettings.json](https://github.com/signature-opensource/CK-Sample-Monitoring/blob/stable/MonitoringDemoApp/appsettings.json)
> is a fully commented configuration file.

## One line, placed early.

```diff
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder( args );
+builder.UseCKMonitoring();
builder.Services.AddHostedService<Worker>();
var host = builder.CKBuild();
await host.RunAsync();
```

`UseCKMonitoring()` is an extension of the `HostApplicationBuilder` of a host
([HostApplicationBuilderMonitoringExtensions](HostApplicationBuilderMonitoringExtensions.cs)). It must
run before any ActivityMonitor is instantiated, and it is safe to call more than once on the same
builder. It is for the builder of a host only: a builder created inside a running host (the web application of an
endpoint for instance) must not reconfigure the `GrandOutput.Default`.

## The "CK-Monitoring" section is the whole configuration surface.

`UseCKMonitoring()` reads the configuration section named "CK-Monitoring" - a section, not a file, so
any of the standard
[configuration providers](https://learn.microsoft.com/dotnet/core/extensions/configuration-providers)
feeds it. With the json provider, a typical configuration is:

```json
{
  "CK-Monitoring": {
    "GrandOutput": {
      "MinimalFilter": "Debug",
      "Handlers": {
        "Console": true,
        "TextFile": {
          "Path": "Text"
        }
      }
    }
  }
}
```

This is a configuration we often use: it logs onto the Console and into "Logs/Text" timed folders.
Each key under "Handlers" is a handler name resolved by the convention described in
[CK.Monitoring](../CK.Monitoring/README.md#naming-conventions-are-load-bearing) - which is how a
handler living in its own assembly needs no registration code at all.

**Changing the configuration reconfigures the live GrandOutput.** Because the underlying
reconfiguration is a diff keyed on each handler identity, editing `appsettings.json` while the
application runs updates, adds or removes handlers without losing the ones that did not change.

## Logging from the builder, before the sink exists.

There is a bootstrap problem: code running inside `CreateHostBuilder` wants to log, but the
GrandOutput does not exist yet. `GetBuilderMonitor()` solves it, on any `IHostApplicationBuilder`:

```csharp
var monitor = builder.GetBuilderMonitor();
```

It can be called at any time, even before `UseCKMonitoring()`. Its entries are retained and replayed
into the GrandOutput as soon as the sink and its handlers are available, so nothing logged during
host construction is lost.

## Deferred configuration: AddAutoConfigure and CKBuild.

Some packages need to configure the builder but can only do so once everything else has been
registered. `AddAutoConfigure` memorizes an action, `ApplyAutoConfigure` applies them in order, and
`CKBuild()` is the `Build()` wrapper that calls `ApplyAutoConfigure` right before building the host:

```csharp
builder.AddAutoConfigure( ( monitor, b ) => { /* ... */ } );
var host = builder.CKBuild();
```

`ApplyAutoConfigure` is idempotent: only the first call applies the configurations.

## What is *not* registered for you.

`UseCKMonitoring()` sets up the sink, the `ILoggerProvider` and, for an independent GrandOutput, an
`IHostedService` - nothing else. The registration of the monitor is yours to write:

```csharp
  services.AddScoped<IActivityMonitor, ActivityMonitor>();
  services.AddScoped( sp => sp.GetRequiredService<IActivityMonitor>().ParallelLogger );
```

The `ActivityMonitor` implementation is not mapped, only `IActivityMonitor` is exposed, and the `IParallelLogger` is the
one of the monitor.

## Microsoft.Extensions.Logging entries are captured too.

An [`ILoggerProvider`](GrandOutputLoggerAdapterProvider.cs) is registered that routes every
`Microsoft.Extensions.Logging` entry - so the framework's own logs, and any third-party library using
`ILogger` - to `GrandOutput.ExternalLog`, filtered by the GrandOutput's `ExternalLogLevelFilter`
(see `GrandOutput.IsExternalLogEnabled`). They land in the same handlers as the ActivityMonitor logs,
which is usually the point of using this package in an ASP.NET application.

## An independent GrandOutput, for hosts inside hosts.

`UseCKMonitoringWithIndependentGrandOutput( out var grandOutput )` configures a **new** GrandOutput
from the same "CK-Monitoring" section instead of touching `GrandOutput.Default`. It is disposed
automatically when `IHost.StopAsync` is called.

This is for advanced scenarios only - a test host, or a host created inside an already running
application, where reconfiguring the process-wide default would be wrong. Everywhere else,
`UseCKMonitoring()` is what you want.
