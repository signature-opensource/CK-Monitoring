using CK.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace CK.Monitoring;

/// <summary>
/// Simple thread safe collector of identity informations. See <see cref="Identities"/>.
/// This is a "convergent data type": Add methods are both idempotent and commutative.
/// <para>
/// The characters 0 to 8 (NUl, SOH, STX, ETX, EOT, ENQ, ACK, BEL, BS) are invalid in a key and in a value.
/// </para> 
/// <para>
/// A key cannot contain newlines: the Unicode Standard, Sec. 5.8, Recommendation R4 and Table 5-2 state
/// that the CR, LF, CRLF, NEL, LS, FF, and PS sequences are considered newline functions.
/// </para>
/// <para>
/// This identity card has been designed to be used independently of the GrandOuput: each GrandOuput has its own identity card
/// but this can be used by log receivers to update the identity from <see cref="IdentityCardFull"/>
/// and <see cref="ActivityMonitorSimpleSenderExtension.IdentityCard.IdentityCardUpdate"/> log entries.
/// </para>
/// <para>
/// Thread safe change tracking can be done "on the outside" thanks to the returned values of the <see cref="Add(ValueTuple{string, string}[])"/>
/// and <see cref="Add(string, string)"/> methods or "behind" by using the <see cref="OnChanged"/> event.
/// </para>
/// <para>
/// Since identity is mostly stable during the life of the application (it will be updated during application start), we use
/// snapshots of an internal dictionary as the exposed Identities and a simple lock to update them.
/// </para>
/// <para>
/// The current TimeZone identifier is by default in any GrandOutput identity card.
/// See https://devblogs.microsoft.com/dotnet/date-time-and-time-zone-enhancements-in-net-6/ to understand windows and IANA time zones.
/// </para>
/// </summary>
public sealed partial class IdentityCard
{
    // We use the _card dictionary instance as the lock.
    readonly Dictionary<string, string[]> _card;
    Action<IdentiCardChangedEvent>? _onChange;
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> _exposed;
    string? _toString;
    // We don't use timer nor ManualResetEvent. We can avoid Dispose call.
    readonly CancellationTokenSource _hasApplicationIdentity;

    /// <summary>
    /// The current version of the identity card.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Gets the tag that identify an <see cref="IdentityCardFull"/> snapshot.
    /// The log text is the identity's packed <see cref="ToString()"/>.
    /// </summary>
    /// <remarks>
    /// Current implementation uses string packing to exchange identity card.
    /// One day, it should use a more efficient (binary representation). To handle this new
    /// representation, This tag should be deprecated, internalized, and a simple "IdentityCard"
    /// should replace it.
    /// </remarks>
    public static readonly CKTrait IdentityCardFull = ActivityMonitor.Tags.Context.FindOrCreate( nameof( IdentityCardFull ) );

    /// <summary>
    /// Initializes a new empty <see cref="IdentityCard"/>.
    /// </summary>
    public IdentityCard()
        : this( new Dictionary<string, string[]>() )
    {
    }

    IdentityCard( Dictionary<string, string[]> card )
    {
        _card = card;
        _exposed = card.AsIReadOnlyDictionary<string, string[], IReadOnlyCollection<string>>();
        _hasApplicationIdentity = new CancellationTokenSource();
    }

    /// <summary>
    /// Event raised on change.
    /// Caution: this event can be raised concurrently.
    /// </summary>
    public event Action<IdentiCardChangedEvent>? OnChanged
    {
        add { _onChange += value; }
        remove { _onChange -= value; }
    }

    /// <summary>
    /// Gets an immutable capture of the current identity informations.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> Identities => _exposed;

    /// <summary>
    /// Gets whether these Identities contain the "AppIdentity" key.
    /// </summary>
    public bool HasApplicationIdentity => _hasApplicationIdentity.IsCancellationRequested;

    /// <summary>
    /// Registers a call back that will be called once "AppIdentity" is available
    /// or immediately if it is already in the Identities.
    /// </summary>
    /// <param name="action"></param>
    public void OnApplicationIdentityAvailable( Action<IdentityCard> action )
    {
        _hasApplicationIdentity.Token.UnsafeRegister( _ => action( this ), null );
    }

    /// <summary>
    /// Gets a readonly safe view on this card.
    /// </summary>
    /// <returns></returns>
    public ReadOnly AsReadOnly() => new ReadOnly( this );

    /// <summary>
    /// Adds a single identity information. If the key/value are invalid, null is returned and an
    /// error log is emitted in the <see cref="ActivityMonitor.StaticLogger"/>.
    /// <para>
    /// The characters 0 to 8 (NUl, SOH, STX, ETX, EOT, ENQ, ACK, BEL, BSP) are invalid in a key and in a value.
    /// </para> 
    /// <para>
    /// A key cannot contain newlines: the Unicode Standard, Sec. 5.8, Recommendation R4 and Table 5-2 state
    /// that the CR, LF, CRLF, NEL, LS, FF, and PS sequences are considered newline functions.
    /// </para>
    /// </summary>
    /// <param name="key">The identity key. Must not be null, empty or white space or contain any line delimiter.</param>
    /// <param name="value">The identity information. Must not be empty.</param>
    /// <returns>The event that <see cref="OnChanged"/> has raised or null if nothing changed.</returns>
    public IdentiCardChangedEvent? Add( string key, string value )
    {
        IdentiCardChangedEvent? result = null;
        AddOneResult a;
        lock( _card )
        {
            a = DoAdd( key, value );
            if( a == AddOneResult.None ) return result;
            var copy = new Dictionary<string, string[]>( _card );
            var r = copy.AsIReadOnlyDictionary<string, string[], IReadOnlyCollection<string>>();
            result = new IdentiCardChangedEvent( this, new[] { (key, value) }, r );
            _exposed = r;
            _toString = null;
        }
        if( a == AddOneResult.AddedAppIdentity ) _hasApplicationIdentity.Cancel();
        _onChange?.Invoke( result );
        return result;
    }

    /// <summary>
    /// Adds multiple identity information at once. See <see cref="Add(string, string)"/> for allowed characters
    /// in key and value strings.
    /// </summary>
    /// <param name="info">Multiple identity informations.</param>
    /// <returns>The event that <see cref="OnChanged"/> has raised or null if nothing changed.</returns>
    public IdentiCardChangedEvent? Add( IEnumerable<(string Key, string Value)> info ) => Add( info, true );

    /// <summary>
    /// Merges another identity card with this one.
    /// </summary>
    /// <param name="other">The other identity card.</param>
    /// <returns>The event that <see cref="OnChanged"/> has raised or null if nothing changed.</returns>
    public IdentiCardChangedEvent? Add( IdentityCard other ) => Add( other.Identities.SelectMany( kv => kv.Value.Select( v => (kv.Key, v) ) ), false );

    enum AddOneResult { None, Added, AddedAppIdentity };

    IdentiCardChangedEvent? Add( IEnumerable<(string Key, string Value)> info, bool checkKeyValues )
    {
        IdentiCardChangedEvent? result = null;
        bool addedAppIdentity = false;
        lock( _card )
        {
            List<(string, string)>? applied = null;
            bool atLeastOne = false;
            bool atLeastNotOne = false;
            int i = 0;
            foreach( var kv in info )
            {
                var a = checkKeyValues
                        ? DoAdd( kv.Key, kv.Value )
                        : DoAddWithoutChecks( _card, kv.Key, kv.Value );
                if( a == AddOneResult.None )
                {
                    if( atLeastOne && applied == null )
                    {
                        applied = info.Take( i ).ToList();
                    }
                    atLeastNotOne = true;
                }
                else
                {
                    if( atLeastNotOne && applied == null ) applied = new List<(string, string)>();
                    if( applied != null ) applied.Add( kv );
                    atLeastOne = true;
                    addedAppIdentity |= a == AddOneResult.AddedAppIdentity;
                }
                ++i;
            }
            if( atLeastOne )
            {
                var copy = new Dictionary<string, string[]>( _card );
                var r = copy.AsIReadOnlyDictionary<string, string[], IReadOnlyCollection<string>>();
                IReadOnlyList<(string Key, string Value)>? added = applied
                                                                   ?? info as IReadOnlyList<(string Key, string Value)>
                                                                   ?? info.ToArray();
                _exposed = r;
                _toString = null;
                result = new IdentiCardChangedEvent( this, added, r );
            }
        }
        if( result != null )
        {
            if( addedAppIdentity ) _hasApplicationIdentity.Cancel();
            _onChange?.Invoke( result );
        }
        return result;
    }

    /// <summary>
    /// Adds multiple identity information at once. See <see cref="Add(string, string)"/> for allowed characters
    /// in key and value strings.
    /// </summary>
    /// <param name="info">Multiple identity informations.</param>
    /// <returns>The event that <see cref="OnChanged"/> has raised or null if nothing changed.</returns>
    public IdentiCardChangedEvent? Add( params (string Key, string Value)[] info ) => Add( (IEnumerable<(string Key, string Value)>)info );

    AddOneResult DoAdd( string key, string value )
    {
        Debug.Assert( Monitor.IsEntered( _card ) );
        if( ActivityMonitorSimpleSenderExtension.IdentityCard.CheckIdentityInformation( key, value, ActivityMonitor.StaticLogger ) )
        {
            return DoAddWithoutChecks( _card, key, value );
        }
        return AddOneResult.None;
    }

    static AddOneResult DoAddWithoutChecks( Dictionary<string, string[]> card, string key, string value )
    {
        if( card.TryGetValue( key, out var exist ) )
        {
            if( exist.Contains( value ) ) return AddOneResult.None;
            Array.Resize( ref exist, exist.Length + 1 );
            exist[^1] = value;
            card[key] = exist;
        }
        else
        {
            card.Add( key, new[] { value } );
            if( key == "AppIdentity" ) return AddOneResult.AddedAppIdentity;
        }
        return AddOneResult.Added;
    }

    /// <summary>
    /// Packs multiple key/values in a single string separated by STX (0x02) characters.
    /// This relies on the fact that STX character doesn't appear in the strings and this is not tested
    /// since this is to be used for identity information that are necessarily valid.
    /// <para>
    /// Use <see cref="TryUnpack(ReadOnlySpan{char})"/> to unpack the resulting string.
    /// </para>
    /// </summary>
    /// <param name="values">The values to pack in one string. Must not be empty.</param>
    /// <returns>The packed string (empty for no values).</returns>
    public static string Pack( IReadOnlyList<(string Key, string Value)> values )
    {
        Debug.Assert( ActivityMonitorSimpleSenderExtension.IdentityCard.KeySeparator == '\u0002' );
        Throw.CheckArgument( values != null && values.Count > 0 );
        if( values.Count == 1 )
        {
            var single = values[0];
            return String.Create( single.Key.Length + 1 + single.Value.Length, single, ( s, v ) =>
            {
                v.Key.CopyTo( s );
                s[v.Key.Length] = '\u0002';
                s = s.Slice( v.Key.Length + 1 );
                v.Value.CopyTo( s );
            } );
        }
        int len = 0;
        foreach( var v in values ) len += v.Key.Length + v.Value.Length + 2;
        return String.Create( len, values, ( s, values ) =>
        {
            foreach( var v in values )
            {
                s[0] = '\u0002';
                s = s.Slice( 1 );
                v.Key.CopyTo( s );
                s[v.Key.Length] = '\u0002';
                s = s.Slice( v.Key.Length + 1 );
                v.Value.CopyTo( s );
                s = s.Slice( v.Value.Length );
            }
        } );
    }

    /// <summary>
    /// Packs <see cref="Identities"/> in a single string separated by SOH (0x01) and STX (0x02) characters.
    /// This relies on the fact that ETX character doesn't appear in the strings and this is not tested
    /// since this is to be used for identity information that are necessarily valid.
    /// <para>
    /// <see cref="TryUnpack(ReadOnlySpan{char})"/> or <see cref="TryCreate(ReadOnlySpan{char})"/> can be used
    /// to unpack the string.
    /// </para>
    /// </summary>
    /// <param name="identities">The identities to pack in one string.</param>
    /// <returns>The packed string (empty for no values).</returns>
    public static string Pack( IReadOnlyDictionary<string, IReadOnlyCollection<string>> identities )
    {
        int len = identities.Count;
        foreach( var kv in identities )
        {
            len += kv.Key.Length + kv.Value.Count;
            foreach( var v in kv.Value )
            {
                len += v.Length;
            }
        }
        return String.Create( len, identities, ( s, identities ) =>
        {
            foreach( var kv in identities )
            {
                s[0] = '\u0001';
                s = s.Slice( 1 );
                kv.Key.CopyTo( s );
                s = s.Slice( kv.Key.Length );
                foreach( var v in kv.Value )
                {
                    s[0] = '\u0002';
                    s = s.Slice( 1 );
                    v.CopyTo( s );
                    s = s.Slice( v.Length );
                }
            }
        } );
    }

    /// <summary>
    /// Gets the identities packed with <see cref="Pack(IReadOnlyDictionary{string, IReadOnlyCollection{string}})"/>.
    /// </summary>
    /// <returns>The identities as a SOH (0x01) and STX (0x02) characters separated string.</returns>
    public override string ToString() => _toString ??= Pack( _exposed );

    /// <summary>
    /// Tries to unpack non empty IReadOnlyList&lt;(string, string)&gt; or a IReadOnlyDictionary&lt;string, IReadOnlyCollection&lt;string&gt;&gt;
    /// previously packed.
    /// </summary>
    /// <param name="s">The packed string. When empty, null is returned.</param>
    /// <returns>A read only list of tuples or dictionary of strings, or null if the string cannot be unpacked.</returns>
    public static object? TryUnpack( ReadOnlySpan<char> s )
    {
        if( s.Length < 3 ) return null;
        char c = s[0];
        if( c == '\u0001' )
        {
            return s.Length < 4 ? null : DoTryUnpackIdentities( s )?.AsIReadOnlyDictionary<string, string[], IReadOnlyCollection<string>>();
        }
        int idx = 0;
        string key;
        if( c == '\u0002' )
        {
            var kv = new List<(string, string)>();
            for(; ; )
            {
                s = s.Slice( idx + 1 );
                idx = s.IndexOf( '\u0002' );
                if( idx <= 0 ) return null;
                key = s.Slice( 0, idx ).ToString();
                s = s.Slice( idx + 1 );
                idx = s.IndexOf( '\u0002' );
                if( idx == 0 ) return null;
                if( idx < 0 )
                {
                    if( s.Length == 0 ) return null;
                    kv.Add( (key, s.ToString()) );
                    return kv;
                }
                kv.Add( (key, s.Slice( 0, idx ).ToString()) );
            }
        }
        idx = s.IndexOf( '\u0002' );
        if( idx <= 0 ) return null;
        key = s.Slice( 0, idx ).ToString();
        s = s.Slice( idx + 1 );
        if( s.Length == 0 ) return null;
        return new[] { (key, s.ToString()) };
    }

    /// <summary>
    /// Tries to create an identity card from its packed identities provided by <see cref="Pack(IReadOnlyDictionary{string, IReadOnlyCollection{string}})"/>
    /// or its <see cref="ToString()"/>.
    /// </summary>
    /// <param name="identities">Packed string to parse.</param>
    /// <returns>The identity card or null.</returns>
    public static IdentityCard? TryCreate( ReadOnlySpan<char> identities )
    {
        if( identities.Length == 0 ) return new IdentityCard();
        if( identities.Length < 4 ) return null;
        if( identities[0] != '\u0001' ) return null;
        var d = DoTryUnpackIdentities( identities );
        return d == null ? null : new IdentityCard( d );
    }

    static Dictionary<string, string[]>? DoTryUnpackIdentities( ReadOnlySpan<char> s )
    {
        var card = new Dictionary<string, string[]>();
        s = s.Slice( 1 );
        int idx = s.IndexOf( '\u0002' );
        if( idx <= 0 ) return null;
        string key;
        for(; ; )
        {
            key = s.Slice( 0, idx ).ToString();
            s = s.Slice( idx + 1 );
            idx = s.IndexOfAny( '\u0001', '\u0002' );
            if( idx <= 0 ) break;
            if( idx == 0 ) return null;
            if( idx < 0 ) break;
            more:
            char next = s[idx];
            DoAddWithoutChecks( card, key, s.Slice( 0, idx ).ToString() );
            s = s.Slice( idx + 1 );
            idx = s.IndexOfAny( '\u0001', '\u0002' );
            if( idx == 0 ) return null;
            if( idx < 0 ) break;
            if( next == '\u0001' ) continue;
            goto more;
        }
        if( idx < 0 )
        {
            DoAddWithoutChecks( card, key, s.ToString() );
            return card;
        }
        return null;
    }
}
