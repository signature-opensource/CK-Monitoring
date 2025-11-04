using CK.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CK.Monitoring;

public sealed partial class DispatcherSink
{
    /// <summary>
    /// List of the current activated <see cref="IGrandOutputHandler"/>.
    /// This is visible only to <see cref="GrandOutputHandlersAction"/> and <see cref="GrandOutputHandlersAction{TResult}"/>.
    /// </summary>
    public sealed class HandlerList
    {
        readonly DispatcherSink _sink;

        internal HandlerList( DispatcherSink sink )
        {
            _sink = sink;
        }

        /// <summary>
        /// Gets the current activated handlers.
        /// </summary>
        public IReadOnlyList<IGrandOutputHandler> Handlers => _sink._handlers;

        /// <summary>
        /// Activates the provided handler (calls <see cref="IGrandOutputHandler.ActivateAsync(IActivityMonitor)"/>)
        /// and on success, adds it to the <see cref="Handlers"/>.
        /// </summary>
        /// <param name="monitor">The monitor to use.</param>
        /// <param name="handler">The handler to activate and add.</param>
        /// <returns>True on success, false on error. Errors have been logged.</returns>
        public async ValueTask<bool> AddAsync( IActivityMonitor monitor, IGrandOutputHandler handler )
        {
            Throw.CheckNotNullArgument( handler );
            Throw.CheckState( !Handlers.Contains( handler ) );
            if( await SafeActivateOrDeactivateAsync( monitor, handler, true ) )
            {
                _sink._handlers.Add( handler );
                return true;
            }
            return false;
        }

        /// <summary>
        /// Deactivates (calls <see cref="IGrandOutputHandler.DeactivateAsync(IActivityMonitor)"/>) and
        /// removes the handler from the <see cref="Handlers"/> regardless of the success of the de-activation.
        /// <para>
        /// This returns true in the nominal case: the handler exists, it has been successfully deactivated and
        /// removed from the handlers list.
        /// </para>
        /// Even when false is returned, the handler is guaranteed to not exist in the handlers list.
        /// </summary>
        /// <param name="monitor">The monitor to use.</param>
        /// <param name="handler">The handler to deactivate and remove.</param>
        /// <returns>
        /// True on success, false on error (errors have been logged) but the handler is
        /// not in the handler list anymore.
        /// </returns>
        public async ValueTask<bool> RemoveAsync( IActivityMonitor monitor, IGrandOutputHandler handler )
        {
            Throw.CheckNotNullArgument( handler );
            int idx = _sink._handlers.IndexOf( handler );
            if( idx < 0 )
            {
                monitor.Error( $"IGrandOutputHandler instance to remove doesn't belong to the handler list." );
                return false;
            }
            bool success = await SafeActivateOrDeactivateAsync( monitor, handler, false );
            _sink._handlers.RemoveAt( idx );
            return success;
        }
    }

}
