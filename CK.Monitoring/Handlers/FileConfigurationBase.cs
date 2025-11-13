using CK.Core;
using System;

namespace CK.Monitoring.Handlers;

/// <summary>
/// Configuration base object for files.
/// </summary>
public abstract class FileConfigurationBase : IHandlerConfiguration
{
    readonly TimedFolderConfiguration _timedFolderMode = new TimedFolderConfiguration();

    /// <summary>
    /// Gets or sets the rate of file housekeeping tasks execution (automatic log file deletion).
    /// This is a multiple of <see cref="GrandOutputConfiguration.TimerDuration"/>,
    /// and defaults to 1800 (which is 15 minutes with the default <see cref="GrandOutputConfiguration.TimerDuration"/> of 500ms).
    /// Setting this to zero disables housekeeping entirely (and this is not a good idea).
    /// </summary>
    public int HousekeepingRate { get; set; } = 1800;

    /// <summary>
    /// Gets or sets the minimum number of days to keep log files, when housekeeping is enabled via <see cref="HousekeepingRate"/>.
    /// Log files more recent than this will not be deleted (even if <see cref="MaximumTotalKbToKeep"/> applies).
    /// Setting both this and <see cref="MaximumTotalKbToKeep"/> to 0 suppress any file cleanup.
    /// Defaults to 60 days.
    /// </summary>
    public TimeSpan MinimumTimeSpanToKeep { get; set; } = TimeSpan.FromDays( 60 );

    /// <summary>
    /// Gets or sets the maximum total file size log files can use, in kilobytes.
    /// Log files within <see cref="MinimumTimeSpanToKeep"/> will not be deleted, even if their cumulative
    /// size exceeds this value.
    /// Setting both this and <see cref="MinimumTimeSpanToKeep"/> to 0 suppress any file cleanup.
    /// Defaults to 100 Megabyte.
    /// </summary>
    public int MaximumTotalKbToKeep { get; set; } = 100_000;

    /// <summary>
    /// Gets or sets the number of days in <see cref="MinimumTimeSpanToKeep"/> as an integer.
    /// If a partial day is set in <see cref="MinimumTimeSpanToKeep"/>, the number of complete days is returned.
    /// </summary>
    public int MinimumDaysToKeep
    {
        get => Convert.ToInt32( Math.Floor( MinimumTimeSpanToKeep.TotalDays ) );
        set => MinimumTimeSpanToKeep = TimeSpan.FromDays( value );
    }

    /// <summary>
    /// Gets or sets the path of the file. When not rooted (see <see cref="System.IO.Path.IsPathRooted(string?)"/>),
    /// it is a sub path in <see cref="LogFile.RootLogPath"/>.
    /// It defaults to the empty string: it must be specified.
    /// </summary>
    public string Path { get; set; } = String.Empty;

    /// <summary>
    /// Gets or sets the maximal count of entries per file.
    /// Defaults to 20000.
    /// </summary>
    public int MaxCountPerFile { get; set; } = 20000;

    /// <summary>
    /// Gets the <see cref="TimedFolderConfiguration"/>.
    /// </summary>
    public TimedFolderConfiguration TimedFolderMode => _timedFolderMode;

    /// <summary>
    /// Clones this configuration.
    /// </summary>
    /// <returns>Clone of this configuration.</returns>
    public abstract IHandlerConfiguration Clone();

}
