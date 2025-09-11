
namespace CK.Monitoring;


/// <summary>
/// Unified interface for full log entries whatever their <see cref="IBaseLogEntry.LogType"/> is.
/// All log entries can be exposed through this "rich" interface.
/// </summary>
public interface IFullLogEntry : ILogEntry, IFullLogInfo
{
    /// <summary>
    /// Creates a <see cref="ILogEntry"/> entry from this <see cref="IFullLogEntry"/> one.
    /// The <see cref="IFullLogInfo.GrandOutputId"/>, <see cref="IFullLogInfo.PreviousEntryType"/>
    /// and <see cref="IFullLogInfo.PreviousLogTime"/> are lost (but less memory is used).
    /// <para>
    /// This creates a snapshot: used on a <see cref="InputLogEntry"/>, this captures the input data
    /// independently of the input being released to the pool.
    /// </para>
    /// </summary>
    ILogEntry CreateSimpleLogEntry();
}
