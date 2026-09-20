namespace CK.Monitoring.Tests;

public sealed class GatedSinkHandlerConfiguration : IHandlerConfiguration
{
    public IHandlerConfiguration Clone() => new GatedSinkHandlerConfiguration();
}
