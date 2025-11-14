namespace CK.Monitoring;

/// <summary>
/// Configuration interface.
/// </summary>
public interface IHandlerConfiguration
{
    /// <summary>
    /// Must return a deep clone of this configuration object.
    /// </summary>
    /// <returns>A clone of this object.</returns>
    IHandlerConfiguration Clone();
}
