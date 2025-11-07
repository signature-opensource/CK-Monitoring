namespace CK.Monitoring.Handlers;

/// <summary>
/// Configures the optional TimedFolder mode. In this mode, a timed folder is created at the start of the process
/// (or at the first activation of the handler) and log files are written into this sub folder.
/// <para>
/// This mode is enabled with a <see cref="MaxCurrentLogFolderCount"/> greater than 0. When more timed folders
/// exist than this value, a "Archive" folder is created and the oldest timed folders are moved to it. The maximal
/// archived folders can be set by <see cref="MaxArchivedLogFolderCount"/>: when not 0, exceeding timed folders are
/// automatically suppressed.
/// </para>
/// <para>
/// To stay on the safe side, in this mode, <see cref="FileConfigurationBase.MinimumTimeSpanToKeep"/> and <see cref="FileConfigurationBase.MaximumTotalKbToKeep"/>
/// are honored. If <see cref="MaxArchivedLogFolderCount"/> is specified, it applies regardless of the <see cref="FileConfigurationBase.MinimumTimeSpanToKeep"/>.
/// </para>
/// <para>
/// The initial timed folder is stable across handler reconfiguration: no new timed folder is created, log files after the
/// reconfiguration go to the initial timed folder.
/// </para>
/// </summary>
public sealed class TimedFolderConfiguration
{
    /// <summary>
    /// Gets or sets the maximal number of timed folders to keep at the root <see cref="FileConfigurationBase.Path"/>.
    /// <para>
    /// When not 0, it is tyically set to 5.
    /// </para>
    /// Default to 0: this disables the TimedFolder mode.
    /// </summary>
    public int MaxCurrentLogFolderCount { get; set; }

    /// <summary>
    /// Gets or sets the maximal number of timed folders to keep in the "<see cref="FileConfigurationBase.Path"/>/Archive" folder.
    /// Default to 0: there is no limit by default, <see cref="FileConfigurationBase.MinimumTimeSpanToKeep"/> and
    /// <see cref="FileConfigurationBase.MaximumTotalKbToKeep"/> housekeeping configurations apply.
    /// </summary>
    public int MaxArchivedLogFolderCount { get; set; }

    /// <summary>
    /// Gets whether the timed folder mode is enabled (<see cref="MaxCurrentLogFolderCount"/> is greater than 0).
    /// </summary>
    public bool Enabled => MaxCurrentLogFolderCount > 0;
}
