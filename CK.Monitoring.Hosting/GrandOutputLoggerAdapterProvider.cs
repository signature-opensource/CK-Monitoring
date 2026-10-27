using System;
using Microsoft.Extensions.Logging;

namespace CK.Monitoring.Hosting;

/// <summary>
/// This <see cref="ILoggerProvider"/> implementation routes
/// logs to GrandOutput.ExternalLog.
/// </summary>
sealed class GrandOutputLoggerAdapterProvider : ILoggerProvider
{
    readonly GrandOutput _grandOutput;
    internal bool _running;

    public GrandOutputLoggerAdapterProvider( GrandOutput grandOutput )
    {
        _grandOutput = grandOutput;
    }

    ILogger ILoggerProvider.CreateLogger( string categoryName )
    {
        return new GrandOutputLoggerAdapter( this, categoryName, _grandOutput );
    }

    void IDisposable.Dispose()
    {
        _running = false;
    }
}
