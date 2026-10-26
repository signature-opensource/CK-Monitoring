Configures CK.Monitoring from the .NET Generic Host: `UseCKMonitoring()` creates the `GrandOutput.Default` from the
"CK-Monitoring" configuration section, keeps it in sync with the configuration and routes Microsoft.Extensions.Logging
entries to it. `GetBuilderMonitor()` provides an ActivityMonitor usable during host construction.
