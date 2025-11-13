namespace CK.Monitoring.Handlers;

/// <summary>
/// Configuration object for <see cref="BinaryFile"/>.
/// </summary>
public class BinaryFileConfiguration : FileConfigurationBase
{
    /// <summary>
    /// Gets or sets whether to use Gzip compression after closing log files.
    /// Defaults to false.
    /// </summary>
    public bool UseGzipCompression { get; set; }


    /// <summary>
    /// Clones this configuration.
    /// </summary>
    /// <returns>Clone of this configuration.</returns>
    public override IHandlerConfiguration Clone()
    {
        return new BinaryFileConfiguration()
        {
            Path = Path,
            MaxCountPerFile = MaxCountPerFile,
            UseGzipCompression = UseGzipCompression,
            HousekeepingRate = HousekeepingRate,
            MinimumTimeSpanToKeep = MinimumTimeSpanToKeep,
            MaximumTotalKbToKeep = MaximumTotalKbToKeep,
            TimedFolderMode =
            {
                MaxCurrentLogFolderCount = TimedFolderMode.MaxCurrentLogFolderCount,
                MaxArchivedLogFolderCount = TimedFolderMode.MaxArchivedLogFolderCount
            }
        };
    }
}
