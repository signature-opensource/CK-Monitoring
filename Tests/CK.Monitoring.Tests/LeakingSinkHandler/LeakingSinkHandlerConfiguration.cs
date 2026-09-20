namespace CK.Monitoring.Tests;

public sealed class LeakingSinkHandlerConfiguration : IHandlerConfiguration
{
    public IHandlerConfiguration Clone() => new LeakingSinkHandlerConfiguration();
}
