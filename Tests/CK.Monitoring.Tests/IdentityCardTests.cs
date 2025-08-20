using CK.Core;
using Shouldly;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CK.Monitoring.Tests;

[TestFixture]
public class IdentityCardTests
{
    [Test]
    public void OnChange_event_is_triggered()
    {
        var id = new IdentityCard();
        int callCount = 0;
        id.OnChanged += ev =>
        {
            ++callCount;
            ev.IdentityCard.ShouldBeSameAs( id );
            ev.AddedInfo.ShouldBe( new[] { ("a", "b") } );
        };
        id.OnChanged += ev => ++callCount;

        var e = id.Add( "a", "b" );
        Debug.Assert( e != null );
        e.AddedInfo.Count.ShouldBe( 1 );
        e.Identities["a"].ShouldBe( new[] { "b" } );

        callCount.ShouldBe( 2 );
    }

    [Test]
    public void duplicated_added_infos_are_ignored()
    {
        var id = new IdentityCard();
        int callCount = 0;
        id.OnChanged += ev => ++callCount;

        id.Add( "a", "b" ).ShouldNotBeNull();
        id.Add( "a", "b" ).ShouldBeNull();
        id.Add( ("a", "b") ).ShouldBeNull();
        id.Add( ("a", "b"), ("a", "b"), ("a", "b"), ("a", "b") ).ShouldBeNull();
        callCount.ShouldBe( 1 );

        id.Add( "a", "c" ).ShouldNotBeNull();
        var c = id.Add( ("a", "d"), ("A", "b"), ("A", "c") );
        Debug.Assert( c != null );
        c.AddedInfo.ShouldBe( new[] { ("a", "d"), ("A", "b"), ("A", "c") } );

        callCount.ShouldBe( 3 );

        c = id.Add( ("a", "d"), ("B", "1"), ("B", "2"), ("A", "c"), ("B", "3") );
        Debug.Assert( c != null );
        c.AddedInfo.ShouldBe( new[] { ("B", "1"), ("B", "2"), ("B", "3") } );
        callCount.ShouldBe( 4 );

        c = id.Add( ("C", "0"), ("B", "1"), ("B", "2"), ("A", "c"), ("B", "3") );
        Debug.Assert( c != null );
        c.AddedInfo.ShouldBe( new[] { ("C", "0") } );
        callCount.ShouldBe( 5 );

        id.Identities.Count.ShouldBe( 4 );
        id.Identities.Keys.ShouldBe( new[] { "a", "A", "B", "C" } );
    }

    [Test]
    public void key_and_value_cannot_contain_the_9_first_chracters_and_keys_cannot_contain_newlines()
    {
        var id = new IdentityCard();
        id.Add( null!, "valid" ).ShouldBeNull();
        id.Add( "", "valid" ).ShouldBeNull();
        id.Add( "A\u0000A", "valid" ).ShouldBeNull();
        id.Add( "A\u0001A", "valid" ).ShouldBeNull();
        id.Add( "A\u0002A", "valid" ).ShouldBeNull();
        id.Add( "A\u0003A", "valid" ).ShouldBeNull();
        id.Add( "A\u0004A", "valid" ).ShouldBeNull();
        id.Add( "A\u0005A", "valid" ).ShouldBeNull();
        id.Add( "A\u0006A", "valid" ).ShouldBeNull();
        id.Add( "A\u0007A", "valid" ).ShouldBeNull();
        id.Add( "A\u0008A", "valid" ).ShouldBeNull();

        id.Add( "A\u0009A", "valid" ).ShouldNotBeNull();

        id.Add( "valid", null! ).ShouldBeNull();
        id.Add( "valid", "" ).ShouldBeNull();
        id.Add( "valid", "A\u0000A" ).ShouldBeNull();
        id.Add( "valid", "A\u0001A" ).ShouldBeNull();
        id.Add( "valid", "A\u0002A" ).ShouldBeNull();
        id.Add( "valid", "A\u0003A" ).ShouldBeNull();
        id.Add( "valid", "A\u0004A" ).ShouldBeNull();
        id.Add( "valid", "A\u0005A" ).ShouldBeNull();
        id.Add( "valid", "A\u0006A" ).ShouldBeNull();
        id.Add( "valid", "A\u0007A" ).ShouldBeNull();
        id.Add( "valid", "A\u0008A" ).ShouldBeNull();

        id.Add( "valid", "A\u0009A" ).ShouldNotBeNull();

        id.Add( "A\rA", "valid" ).ShouldBeNull();
        id.Add( "A\nA", "valid" ).ShouldBeNull();
    }

    [Test]
    public void packing_and_unpacking_added_infos()
    {
        Check( new[] { ("A", "B"), ("C", "D") } );
        Check( new[] { ("A", "B"), ("C", "D"), ("E", "F") } );

        static void Check( (string, string)[] a )
        {
            var s = IdentityCard.Pack( a );
            var r = (IReadOnlyList<(string, string)>?)IdentityCard.TryUnpack( s );
            r.ShouldBe( a );
        }
    }

    [Test]
    public void packing_and_unpacking_identities()
    {
        var id = new IdentityCard();
        Check( id );
        id.Add( "A", "B" );
        Check( id );
        id.Add( "A", "C" );
        Check( id );
        id.Add( "A", "D" );
        Check( id );
        id.Add( "A", "E" );
        Check( id );
        id.Add( "X", "Y" );
        Check( id );
        id.Add( "U", "T" );
        Check( id );
        id.Add( "M", "N" );
        Check( id );
        id.Add( "M", "O" );
        Check( id );
        id.Add( "M", "P" );
        Check( id );
        id.Add( "ANOTHER", "Piece" );
        Check( id );
        id.Add( "OF", "DATA" );
        Check( id );
        id.Add( "ANOTHER", "another piece" );
        Check( id );
        id.Add( "OF", "another DATA" );
        Check( id );

        static void Check( IdentityCard c )
        {
            var s = c.ToString();
            var r = IdentityCard.TryCreate( s );
            r.ShouldNotBeNull().Identities.Count.ShouldBe( c.Identities.Count );
            r.Identities.ShouldAll( e => e.Value.ShouldBeEquivalentTo( c.Identities[e.Key] ) );

            if( s.Length > 0 )
            {
                var i = (IReadOnlyDictionary<string, IReadOnlyCollection<string>>?)IdentityCard.TryUnpack( s );
                i.ShouldNotBeNull().Count.ShouldBe( c.Identities.Count );
                i.ShouldAll( e => e.Value.ShouldBeEquivalentTo( c.Identities[e.Key] ) );
            }
        }
    }

    [Test]
    public async Task CoreApplicationIdentity_initialize_injects_CoreApplicationIdentity_values_Async()
    {
        await using var g = new GrandOutput( new GrandOutputConfiguration() );
        CoreApplicationIdentity.Initialize();
        g.IdentityCard.Identities["AppIdentity"].ShouldNotBeEmpty();
        g.IdentityCard.Identities["AppIdentity/InstanceId"].Single().ShouldBe( CoreApplicationIdentity.InstanceId );
        g.IdentityCard.Identities["AppIdentity/ContextualId"].Single().ShouldBe( CoreApplicationIdentity.Instance.ContextualId );
        // The empty ContextDescriptor cannot be a valid value.
        if( CoreApplicationIdentity.Instance.ContextDescriptor.Length > 0 )
        {
            g.IdentityCard.Identities["AppIdentity/ContextDescriptor"].Single().ShouldBe( CoreApplicationIdentity.Instance.ContextDescriptor );
        }
    }

    [Test]
    public async Task ActivityMonitorSimpleSenderExtension_AddIdentityInformation_updates_the_identity_card_Async()
    {
        string folder = TestHelper.PrepareLogFolder( "AddIdentityInformation" );
        var textConf = new Handlers.TextFileConfiguration() { Path = "AddIdentityInformation", AutoFlushRate = 1 };

        IdentityCard.ReadOnly identityCard;
        await using( var g = new GrandOutput( new GrandOutputConfiguration() { TimerDuration = TimeSpan.FromMilliseconds( 15 ), Handlers = { textConf } } ) )
        {
            identityCard = g.IdentityCard;
            var m = new ActivityMonitor( ActivityMonitorOptions.SkipAutoConfiguration );
            g.EnsureGrandOutputClient( m );
            m.AddIdentityInformation( "Hello", "World!" );
            m.AddIdentityInformation( "Hello", "World2" );
            m.AddIdentityInformation( "Hello", "World!" );
        }

        identityCard.Identities["Hello"].ShouldNotBeEmpty( "The identity card has been updated." );
        identityCard.Identities["Hello"].ShouldBe( ["World!", "World2"], ignoreOrder: true );

        string tempFile = Directory.EnumerateFiles( folder ).Single();
        var lines = TestHelper.FileReadAllText( tempFile ).Split( Environment.NewLine, StringSplitOptions.RemoveEmptyEntries );
        var helloUpdateLines = lines.Where( l => l.Contains( $"Hello{ActivityMonitorSimpleSenderExtension.IdentityCard.KeySeparator}World!" ) );
        helloUpdateLines.Count().ShouldBe( 1, "The Hello line has been added only once." );

        var hello2UpdateLines = lines.Where( l => l.Contains( $"Hello{ActivityMonitorSimpleSenderExtension.IdentityCard.KeySeparator}World2" ) );
        hello2UpdateLines.Count().ShouldBe( 1, "The Hello World2 line has been added only once." );
    }
}
