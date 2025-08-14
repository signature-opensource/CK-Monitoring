using CK.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace CK.Monitoring;

/// <summary>
/// Writes <see cref="IFullLogEntry"/> to a <see cref="StringBuilder"/>.
/// This object is not thread-safe. If it must be used in a concurrent manner, 
/// a lock should protect it.
/// </summary>
public class MulticastLogEntryTextBuilder
{
    const int _maxMonitorCount = 64 * 64 * 64;
    static readonly char[] _b64e = new char[] {  '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
                   'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J',
                   'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T',
                   'U', 'V', 'W', 'X', 'Y', 'Z',
                   'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j',
                   'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't',
                   'u', 'v', 'w', 'x', 'y', 'z',
                   '+', '/'};
    readonly StringBuilder _builder;
    readonly Dictionary<string, string> _monitorNames;
    DateTime _lastLogTime;
    readonly bool _useDeltaTime;
    readonly string _timeFormat;
    readonly int _timeFormatLength;
    readonly string _blankSpacePrefix;
    const string _deltaFormat = @"ss\.fffffff";
    readonly int _deltaBlankSpacing;

    /// <summary>
    /// Initializes a new instance of <see cref="MulticastLogEntryTextBuilder"/>.
    /// </summary>
    /// <param name="useDeltaTime">
    /// When set to false, the log times are displayed with the +delta seconds from its minute: the full time appears
    /// only once per minute.
    /// </param>
    /// <param name="timeInMilliseconds">True to cut the fractional part to milliseconds, otherwise nanoseconds are displayed.</param>
    public MulticastLogEntryTextBuilder( bool useDeltaTime, bool timeInMilliseconds )
        : this( timeInMilliseconds ? @"yyyy-MM-dd HH\hmm.ss.fff" : FileUtil.FileNameUniqueTimeUtcFormat, useDeltaTime )
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="MulticastLogEntryTextBuilder"/>.
    /// </summary>
    /// <param name="timeFormat">Time format string used to display the DateTime before each logged line.</param>
    /// <param name="useDeltaTime">
    /// When set to false, the log times are displayed with the +delta seconds from its minute: the full time appears
    /// only once per minute.
    /// </param>
    public MulticastLogEntryTextBuilder( string timeFormat, bool useDeltaTime )
    {
        _useDeltaTime = useDeltaTime;
        _builder = new StringBuilder();
        _monitorNames = new Dictionary<string, string>
        {
            { ActivityMonitor.ExternalLogMonitorUniqueId, "###" },
            { ActivityMonitor.StaticLogMonitorUniqueId, "§§§" }
        };
        _timeFormat = timeFormat;
        _timeFormatLength = DateTime.UtcNow.ToString( timeFormat ).Length;
        _blankSpacePrefix = new string( ' ', _timeFormatLength + 8 ); //timeString + ' ' + '~001' + ' ' + 'F' + ' '
        _deltaBlankSpacing = _timeFormatLength - _deltaFormat.Length;
    }

    static string B64ConvertInt( int value )
    {
        //https://www.codeproject.com/Articles/27493/Convert-an-integer-to-a-base-64-string-and-back-ag
        return String.Create( 3, value, ( s, v ) => { s[0] = _b64e[(value & 258048) >> 12]; s[1] = _b64e[(value & 4032) >> 06]; s[2] = _b64e[(value & 63)]; } );
    }

    string GetFormattedDate( IFullLogEntry e )
    {
        string output;
        // Log time prefixes the first line only.
        TimeSpan delta = e.LogTime.TimeUtc - _lastLogTime;
        if( !_useDeltaTime || delta >= TimeSpan.FromMinutes( 1 ) )
        {
            output = e.LogTime.TimeUtc.ToString( _timeFormat );
            _lastLogTime = e.LogTime.TimeUtc;
        }
        else
        {
            output = _deltaBlankSpacing + delta.ToString( _deltaFormat );
        }
        return output;
    }

    /// <summary>
    /// Represent a "line" to display.
    /// This is used to change the color of the different parts: 
    /// </summary>
    public readonly struct FormattedEntry
    {
        /// <summary>
        /// Construct a new <see cref="FormattedEntry"/>.
        /// </summary>
        /// <param name="logLevel">Log level.</param>
        /// <param name="indentationPrefix">Indentation prefix.</param>
        /// <param name="monitorId">The monitor id, the constructor will prepend a '~' character.</param>
        /// <param name="date">The formatted date of the entry.</param>
        /// <param name="entryText">The tags and the text entry.</param>
        public FormattedEntry( char logLevel, string indentationPrefix, string monitorId, string date, string entryText )
        {
            LogLevel = logLevel;
            IndentationPrefix = indentationPrefix;
            FormattedDate = date;
            MonitorId = "~" + monitorId;
            EntryText = entryText;
        }

        /// <summary>
        /// Gets whether this line is a valid one or the default struct.
        /// </summary>
        public bool IsValid => IndentationPrefix != null;

        /// <summary>
        /// The level character.
        /// </summary>
        public readonly char LogLevel;

        /// <summary>
        /// The Indentation of the log.
        /// </summary>
        public readonly string IndentationPrefix;

        /// <summary>
        /// Part 1: the formatted date of the entry.
        /// </summary>
        public readonly string FormattedDate;

        /// <summary>
        /// Part 2: the monitor identifier to display.
        /// </summary>
        public readonly string MonitorId;

        /// <summary>
        /// Part 3: the text entry is the <see cref="LogLevel"/> + ' ' + <see cref="IndentationPrefix"/> + the log text itself.
        /// </summary>
        public readonly string EntryText;

        /// <summary>
        /// Writes this entry to a string builder.
        /// </summary>
        /// <param name="b">This builder.</param>
        /// <returns>The builder to enable fluent syntax.</returns>
        public StringBuilder Write( StringBuilder b ) => IndentationPrefix == null
                                                             ? b
                                                             : b.Append( FormattedDate )
                                                                .Append( ' ' )
                                                                .Append( MonitorId )
                                                                .Append( ' ' )
                                                                .Append( LogLevel )
                                                                .Append( ' ' )
                                                                .Append( IndentationPrefix )
                                                                .Append( EntryText );
    }

    /// <summary>
    /// Gets a formatted string from a logEntry.
    /// </summary>
    /// <param name="logEntry">Entry to format.</param>
    /// <param name="entrySeparator">Separate the two entries when needed. Default null resolve to <see cref="Environment.NewLine"/>.</param>
    /// <returns>Formatted log entries.</returns>
    public string FormatEntryString( IFullLogEntry logEntry, string? entrySeparator = null )
    {
        if( entrySeparator == null ) entrySeparator = Environment.NewLine;
        var (before, entry) = FormatEntry( logEntry );
        if( before.IsValid )
        {
            before.Write( _builder ).Append( entrySeparator );
        }
        entry.Write( _builder );
        string output = _builder.ToString();
        _builder.Clear();
        return output;
    }

    /// <summary>
    /// Format the <paramref name="logEntry"/>
    /// </summary>
    /// <param name="logEntry"></param>
    /// <returns>A possible first entry - for monitor numbering - and the entry itself.</returns>
    public (FormattedEntry Before, FormattedEntry Entry) FormatEntry( IFullLogEntry logEntry )
    {
        FormattedEntry before = default;
        string formattedDate = GetFormattedDate( logEntry );

        char logLevel = logEntry.LogLevel.ToChar();
        string indentationPrefix = ActivityMonitorTextHelperClient.GetMultilinePrefixWithDepth( logEntry.GroupDepth );

        string? monitorId;
        if( logEntry.MonitorId == ActivityMonitor.ExternalLogMonitorUniqueId )
        {
            monitorId = "###";
        }
        else if( logEntry.MonitorId == ActivityMonitor.StaticLogMonitorUniqueId )
        {
            monitorId = "§§§";
        }
        else if( !_monitorNames.TryGetValue( logEntry.MonitorId, out monitorId ) )
        {
            string _monitorResetLog = "";
            if( _monitorNames.Count - 1 == _maxMonitorCount )
            {
                _monitorNames.Clear();
                _monitorResetLog = $" Monitor reset count {_maxMonitorCount}.";
            }
            monitorId = B64ConvertInt( _monitorNames.Count );
            _monitorNames.Add( logEntry.MonitorId, monitorId );
            Debug.Assert( LogLevel.Info.ToChar() == 'i' );
            before = new FormattedEntry( 'i',
                                         indentationPrefix,
                                         monitorId,
                                         formattedDate,
                                         $" [] Monitor: ~{logEntry.MonitorId}. {_monitorResetLog}" );
        }
        string multiLinePrefix = _blankSpacePrefix + indentationPrefix;

        if( logEntry.Text != null )
        {
            Throw.DebugAssert( logEntry.LogType != LogEntryType.CloseGroup );
            if( logEntry.LogType == LogEntryType.OpenGroup ) _builder.Append( '>' );
            _builder.Append( " [" ).Append( logEntry.Tags ).Append( "] " );
            multiLinePrefix += "   ";
            _builder.AppendMultiLine( multiLinePrefix, logEntry.Text, false );
            if( logEntry.Exception != null )
            {
                _builder.AppendLine();
                logEntry.Exception.ToStringBuilder( _builder, multiLinePrefix, logEntry.Text == logEntry.Exception.Message, false );
            }
        }
        else
        {
            Throw.DebugAssert( logEntry.Conclusions != null );
            _builder.Append( "< " );
            if( logEntry.Conclusions.Count > 0 )
            {
                if( logEntry.Conclusions.Count == 1 )
                {
                    _builder.AppendMultiLine( multiLinePrefix + ' ', logEntry.Conclusions.Single().Text, false );
                }
                else
                {
                    _builder.Append( logEntry.Conclusions.Count ).Append( " conclusion" );
                    if( logEntry.Conclusions.Count > 1 ) _builder.Append( 's' );
                    _builder.Append( ':' ).AppendLine();
                    multiLinePrefix += ' ';
                    bool first = true;
                    foreach( var c in logEntry.Conclusions )
                    {
                        if( !first ) _builder.AppendLine();
                        first = false;
                        _builder.AppendMultiLine( multiLinePrefix + ' ', c.Text, true );
                    }
                }

            }
        }
        string outputLine = _builder.ToString();
        _builder.Clear();
        return (before, new FormattedEntry( logLevel,
                                             indentationPrefix,
                                             monitorId,
                                             formattedDate,
                                             outputLine ));
    }

    /// <summary>
    /// Resets internal states (like monitor's numbering).
    /// </summary>
    public void Reset()
    {
        _monitorNames.Clear();
        _lastLogTime = DateTime.MinValue;
    }
}
