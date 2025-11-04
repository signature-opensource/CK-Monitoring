using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CK.Core;

namespace CK.Monitoring;

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
