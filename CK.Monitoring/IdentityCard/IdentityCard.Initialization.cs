using CK.Core;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace CK.Monitoring;

public sealed partial class IdentityCard
{
    internal void LocalInitialize( IActivityMonitor monitor, bool isDefaultGrandOutput )
    {
        static IEnumerable<(string Key, string Value)> GetAppIdentityInfos()
        {
            var coreId = CoreApplicationIdentity.Instance;
            var id = coreId.DomainName;
            if( coreId.EnvironmentName.Length > 0 ) id += '/' + coreId.EnvironmentName;
            id += '/' + coreId.PartyName;
            var v = new[] { ("AppIdentity", id),
                            ("AppIdentity/InstanceId", CoreApplicationIdentity.InstanceId),
                            ("AppIdentity/ContextualId", coreId.ContextualId) };
            return coreId.ContextDescriptor.Length > 0
                    ? v.Append( ("AppIdentity/ContextDescriptor", coreId.ContextDescriptor) )
                    : v;
        }

        bool appIdentityInitialized = CoreApplicationIdentity.IsInitialized;
        try
        {
            var infos = new List<(string, string)>( 128 )
            {
                ("IdentityCardVersion", CurrentVersion.ToString( CultureInfo.InvariantCulture ))
            };
            // If the Core identity is ready, do it now.
            if( appIdentityInitialized )
            {
                infos.AddRange( GetAppIdentityInfos() );
            }

            AddEnvironment( infos );
            AddTimeZone( monitor, infos );
            // Track loaded assemblies versions only for the default GrandOutput.
            if( isDefaultGrandOutput )
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                foreach( var a in AppDomain.CurrentDomain.GetAssemblies() )
                {
                    infos.AddRange( GetAssemblyInfos( a ) );
                }
            }

            Add( infos );
        }
        catch( Exception ex )
        {
            monitor.Fatal( "Unable to build identity card.", ex );
            // If an error occurred during initialization, ensures
            // that the CoreApplicationIdentity will be registered.
            // The fact that IdentityCardVersion may not be registered is "normal":
            // this version applies to the content of the identity card and if an error
            // occurred, the content is not the one expected.
            appIdentityInitialized = false;
        }
        if( !appIdentityInitialized )
        {
            CoreApplicationIdentity.OnInitialized( () => Add( GetAppIdentityInfos() ) );
        }
    }

    static void AddEnvironment( List<(string Key, string Value)> infos )
    {
        infos.Add( ("MachineName", Environment.MachineName) );
        infos.Add( ("UserName", Environment.UserName) );
        infos.Add( ("OSVersion", Environment.OSVersion.ToString()) );

        infos.Add( ("RuntimeInformation/ProcessArchitecture", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()) );
        infos.Add( ("RuntimeInformation/OSArchitecture", System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString()) );

        if( OperatingSystem.IsWindows() ) infos.Add( ("OS", "Windows") );
        else if( OperatingSystem.IsLinux() ) infos.Add( ("OS", "Linux") );
        else if( OperatingSystem.IsMacOS() ) infos.Add( ("OS", "MacOS") );
        else if( OperatingSystem.IsBrowser() ) infos.Add( ("OS", "Browser") );
        else if( OperatingSystem.IsAndroid() ) infos.Add( ("OS", "Android") );
        else if( OperatingSystem.IsFreeBSD() ) infos.Add( ("OS", "FreeBSD") );
        else if( OperatingSystem.IsIOS() ) infos.Add( ("OS", "IOS") );
        else if( OperatingSystem.IsMacOS() ) infos.Add( ("OS", "MacOS") );
        else if( OperatingSystem.IsMacCatalyst() ) infos.Add( ("OS", "MacCatalyst") );
        else if( OperatingSystem.IsTvOS() ) infos.Add( ("OS", "TvOS") );
        else if( OperatingSystem.IsWatchOS() ) infos.Add( ("OS", "WatchOS") );
        else infos.Add( ("OS", "Other") );
    }

    static void AddTimeZone( IActivityMonitor monitor, List<(string Key, string Value)> infos )
    {
        var tz = TimeZoneInfo.Local;
        infos.Add( ("TimeZone", tz.ToSerializedString()) );
        if( tz.HasIanaId )
        {
            infos.Add( ("TimeZone/Id", tz.Id) );
            if( TimeZoneInfo.TryConvertIanaIdToWindowsId( tz.Id, out string? winId ) )
            {
                infos.Add( ("TimeZone/Id/Windows", winId) );
            }
            else
            {
                // Don't warn and even trace whenever we miss a Windows id.
            }
        }
        else
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById( tz.Id );
                infos.Add( ("TimeZone/Id/Windows", tz.Id) );
            }
            catch( Exception ex )
            {
                monitor.Warn( $"Unable to find the time zone '{tz.Id}' among existing system time zones.", ex );
                infos.Add( ("TimeZone/Id/Custom", tz.Id) );
            }
            if( TimeZoneInfo.TryConvertWindowsIdToIanaId( tz.Id, out string? ianaId ) )
            {
                infos.Add( ("TimeZone/Id", ianaId) );
            }
            else
            {
                // Don't warn and even trace whenever we miss a IANA id. Windows "Romance Standard Time" is ambiguous:
                // (Europe/Paris, Europe/Brussels, Europe/Copenhagen, Europe/Madrid or Africa/Ceuta) and this is noise
                // in the logs. Moreover, the TimeZone/Id/Windows is captured above.
            }
        }
    }

    internal void LocalUninitialize( bool isDefaultGrandOutput )
    {
        if( isDefaultGrandOutput )
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        }
    }

    void OnAssemblyLoad( object? sender, AssemblyLoadEventArgs args ) => Add( GetAssemblyInfos( args.LoadedAssembly ) );

    IEnumerable<(string Key, string Value)> GetAssemblyInfos( Assembly assembly )
    {
        var name = assembly.GetName();
        var info = (AssemblyInformationalVersionAttribute?)Attribute.GetCustomAttribute( assembly, typeof( AssemblyInformationalVersionAttribute ) );
        string prefix = $"Assembly/{name.Name}";

        var metas = Attribute.GetCustomAttributes( assembly, typeof( AssemblyMetadataAttribute ) ).Cast<AssemblyMetadataAttribute>();
        // Skips any Serviceable assemblies (provided by Microsoft/.NetFoundation).
        if( metas.Any( m => m.Key == "Serviceable" ) ) return Enumerable.Empty<(string Key, string Value)>();

        string vInfo = info?.InformationalVersion
                       ?? name.Version?.ToString()
                       ?? "(null)";
        string metaPrefix = prefix + "/Meta/";
        return metas.Cast<AssemblyMetadataAttribute>().Where( m => !string.IsNullOrWhiteSpace( m.Value ) )
                                                      .Select( m => (metaPrefix + m.Key, m.Value!) )
                                                      .Prepend( (prefix, vInfo) );
    }

}
