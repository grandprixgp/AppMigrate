using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TeaChest.Services.Lan;

public sealed record LanNetwork(string Name, string Adapter, bool IsPublic);

/// <summary>A connected network as Windows reports it, before deciding whether it is the LAN.</summary>
public sealed record NetworkConnectionInfo(string NetworkName, int Category, Guid AdapterId);

/// <summary>What we need to know about an adapter to tell a real LAN from a VPN or virtual switch.</summary>
public sealed record AdapterInfo(Guid Id, string Name, bool HasIPv4Gateway, bool HasLanAddress);

/// <summary>
/// Reads the Public/Private category of the networks this PC is on. The sharing firewall rule
/// only applies to Private networks, so a home network left on Public silently blocks peers.
/// </summary>
public static class NetworkProfiles
{
    private const int CategoryPublic = 0;

    /// <summary>The LAN networks this PC is connected to. Empty if Windows will not say.</summary>
    public static IReadOnlyList<LanNetwork> GetLanNetworks()
    {
        try
        {
            return SelectLanNetworks(ReadConnections(), ReadAdapters());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NetworkInformationException)
        {
            return Array.Empty<LanNetwork>();
        }
    }

    /// <summary>
    /// Keeps networks on adapters that look like a real LAN: a private, non-host IPv4 address and a
    /// default gateway. That leaves out VPN tunnels (Cloudflare WARP reports a Public /32 network
    /// with no gateway) and Hyper-V/Sandbox switches, which would otherwise raise false alarms.
    /// </summary>
    internal static IReadOnlyList<LanNetwork> SelectLanNetworks(IEnumerable<NetworkConnectionInfo> connections, IReadOnlyDictionary<Guid, AdapterInfo> adapters)
    {
        var result = new List<LanNetwork>();
        foreach (NetworkConnectionInfo connection in connections)
        {
            if (!adapters.TryGetValue(connection.AdapterId, out AdapterInfo? adapter)) continue;
            if (!adapter.HasIPv4Gateway || !adapter.HasLanAddress) continue;
            if (result.Any(n => n.Name == connection.NetworkName && n.Adapter == adapter.Name)) continue;
            result.Add(new LanNetwork(connection.NetworkName, adapter.Name, connection.Category == CategoryPublic));
        }
        return result;
    }

    private static List<NetworkConnectionInfo> ReadConnections()
    {
        var result = new List<NetworkConnectionInfo>();
        var manager = (INetworkListManager)new NetworkListManagerClass();
        try
        {
            manager.GetNetworks(NlmEnumNetwork.Connected, out IEnumNetworks networks);
            while (networks.Next(1, out INetwork? network, out uint fetched) == 0 && fetched == 1 && network != null)
            {
                network.GetName(out string name);
                network.GetCategory(out int category);
                network.GetNetworkConnections(out IEnumNetworkConnections connections);
                while (connections.Next(1, out INetworkConnection? connection, out uint got) == 0 && got == 1 && connection != null)
                {
                    connection.GetAdapterId(out Guid adapterId);
                    result.Add(new NetworkConnectionInfo(name, category, adapterId));
                    Marshal.ReleaseComObject(connection);
                }
                Marshal.ReleaseComObject(connections);
                Marshal.ReleaseComObject(network);
            }
            Marshal.ReleaseComObject(networks);
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
        return result;
    }

    private static Dictionary<Guid, AdapterInfo> ReadAdapters()
    {
        var result = new Dictionary<Guid, AdapterInfo>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!Guid.TryParse(nic.Id, out Guid id)) continue;
            IPInterfaceProperties properties = nic.GetIPProperties();
            bool gateway = properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            bool lan = properties.UnicastAddresses.Any(u =>
                u.Address.AddressFamily == AddressFamily.InterNetwork
                && u.PrefixLength < 32
                && LanProtocol.IsLocalAddress(u.Address)
                && !IPAddress.IsLoopback(u.Address)
                && !u.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            result[id] = new AdapterInfo(id, nic.Name, gateway, lan);
        }
        return result;
    }

    // ---- Network List Manager COM interop (netlistmgr.h). Only the methods used are declared,
    // ---- in vtable order up to the last one needed.

    private enum NlmEnumNetwork
    {
        Connected = 0x01
    }

    [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManagerClass
    {
    }

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [PreserveSig] int GetNetworks(NlmEnumNetwork flags, out IEnumNetworks networks);
    }

    [ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworks
    {
        [PreserveSig] int GetNewEnum([MarshalAs(UnmanagedType.IUnknown)] out object enumerator);
        [PreserveSig] int Next(uint count, out INetwork? network, out uint fetched);
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.BStr)] out string name);
        [PreserveSig] int SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.BStr)] out string description);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        [PreserveSig] int GetNetworkId(out Guid id);
        [PreserveSig] int GetDomainType(out int domainType);
        [PreserveSig] int GetNetworkConnections(out IEnumNetworkConnections connections);
        [PreserveSig] int GetTimeCreatedAndConnected(out uint lowCreated, out uint highCreated, out uint lowConnected, out uint highConnected);
        [PreserveSig] int GetIsConnectedToInternet(out short connected);
        [PreserveSig] int GetIsConnected(out short connected);
        [PreserveSig] int GetConnectivity(out int connectivity);
        [PreserveSig] int GetCategory(out int category);
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [PreserveSig] int GetNewEnum([MarshalAs(UnmanagedType.IUnknown)] out object enumerator);
        [PreserveSig] int Next(uint count, out INetworkConnection? connection, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [PreserveSig] int GetNetwork(out INetwork network);
        [PreserveSig] int GetIsConnectedToInternet(out short connected);
        [PreserveSig] int GetIsConnected(out short connected);
        [PreserveSig] int GetConnectivity(out int connectivity);
        [PreserveSig] int GetConnectionId(out Guid id);
        [PreserveSig] int GetAdapterId(out Guid id);
    }
}
