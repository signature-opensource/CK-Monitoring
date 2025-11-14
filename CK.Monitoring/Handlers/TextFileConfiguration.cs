namespace CK.Monitoring.Handlers;

/// <summary>
/// Configuration object for <see cref="TextFile"/>.
/// </summary>
public class TextFileConfiguration : FileConfigurationBase
{
    /// <summary>
    /// Gets or sets the rate of the auto flush to be able to read the temporary current file content.
    /// This is a multiple of <see cref="GrandOutputConfiguration.TimerDuration"/>
    /// and defaults to 6 (default GrandOutputConfiguration timer duration being 500 milliseconds, this
    /// flushes the text approximately every 3 seconds).
    /// Setting this to zero disables the timed-base flush.
    /// </summary>
    public int AutoFlushRate { get; set; } = 6;

    /// <summary>
    /// True to write the "Metrics" logs.
    /// Default to false.
    /// </summary>
    public bool HandleMetrics { get; set; }

    /// <summary>
    /// Gets or sets whether the "LastRun.log" link file should be handled.
    /// When let to null, defaults to <see cref="TimedFolderConfiguration.Enabled"/>.
    /// <para>
    /// Unfortunately, creating symbolic links on Windows is a security restricted capability.
    /// One way is to enable the developper mode in Windows 10/11.
    /// The user can also be granted the
    /// <see href="https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-10/security/threat-protection/security-policy-settings/create-symbolic-links">
    /// SeCreateSymbolicLinkPrivilege
    /// </see>.
    /// </para>
    /// </summary>
    public bool? WithLastRunLink { get; set; }

    /// <summary>
    /// Clones this configuration.
    /// </summary>
    /// <returns>Clone of this configuration.</returns>
    public override IHandlerConfiguration Clone()
    {
        return new TextFileConfiguration()
        {
            Path = Path,
            MaxCountPerFile = MaxCountPerFile,
            AutoFlushRate = AutoFlushRate,
            HandleMetrics = HandleMetrics,
            WithLastRunLink = WithLastRunLink,
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
