Configures CK.Monitoring from the .NET Generic Host: `UseCKMonitoring()` creates the `GrandOutput.Default` from the
"CK-Monitoring" configuration section and keeps it in sync with the configuration.

It also routes Microsoft.Extensions.Logging entries to it. `GetBuilderMonitor()` provides an ActivityMonitor usable
during host construction.
