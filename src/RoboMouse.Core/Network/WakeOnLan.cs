using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RoboMouse.Core.Logging;

namespace RoboMouse.Core.Network;

/// <summary>
/// Wake-on-LAN: finds the MAC address a peer should remember us by, and sends the magic packet that
/// wakes a sleeping peer. The target needs Wake-on-LAN enabled in its firmware and adapter settings,
/// and generally a wired connection on the same network.
/// </summary>
public static class WakeOnLan
{
    /// <summary>
    /// The MAC address (12 hex digits) of the adapter that owns <paramref name="localAddress"/>, which
    /// is the adapter a peer reaches us through. Empty when it cannot be determined.
    /// </summary>
    public static string GetMacAddressFor(IPAddress? localAddress)
    {
        if (localAddress == null)
            return string.Empty;
        if (localAddress.IsIPv4MappedToIPv6)
            localAddress = localAddress.MapToIPv4();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                if (!nic.GetIPProperties().UnicastAddresses.Any(a => a.Address.Equals(localAddress)))
                    continue;
                var mac = Normalize(nic.GetPhysicalAddress().ToString());
                if (mac.Length == 12)
                    return mac;
            }
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Wake", $"Could not read the local MAC address: {ex.Message}");
        }
        return string.Empty;
    }

    /// <summary>Strips separators and upper-cases; returns an empty string unless 12 hex digits remain.</summary>
    public static string Normalize(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
            return string.Empty;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 12 && hex != "000000000000" ? hex : string.Empty;
    }

    /// <summary>"AABBCCDDEEFF" as "AA:BB:CC:DD:EE:FF", for display.</summary>
    public static string Format(string? mac)
    {
        var hex = Normalize(mac);
        return hex.Length == 0 ? string.Empty : string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    /// <summary>The magic packet: six 0xFF bytes followed by the MAC address sixteen times.</summary>
    public static byte[] BuildMagicPacket(string mac)
    {
        var hex = Normalize(mac);
        if (hex.Length == 0)
            throw new ArgumentException("Not a MAC address.", nameof(mac));

        var address = Convert.FromHexString(hex);
        var packet = new byte[6 + 16 * 6];
        packet.AsSpan(0, 6).Fill(0xFF);
        for (var i = 0; i < 16; i++)
            address.CopyTo(packet, 6 + i * 6);
        return packet;
    }

    /// <summary>
    /// Broadcasts the magic packet out of every active IPv4 adapter (limited and subnet-directed
    /// broadcast, ports 9 and 7). Returns the number of datagrams sent; zero means it could not be sent.
    /// </summary>
    public static int Send(string mac)
    {
        var packet = BuildMagicPacket(mac);
        var sent = 0;

        foreach (var (local, broadcast) in GetBroadcastTargets())
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                foreach (var target in new[] { IPAddress.Broadcast, broadcast }.Distinct())
                {
                    foreach (var port in new[] { 9, 7 })
                    {
                        udp.Send(packet, packet.Length, new IPEndPoint(target, port));
                        sent++;
                    }
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Log("Wake", $"Could not send from {local}: {ex.Message}");
            }
        }

        SimpleLogger.Log("Wake", $"Sent wake packet for {Format(mac)} ({sent} datagrams)");
        return sent;
    }

    private static List<(IPAddress Local, IPAddress Broadcast)> GetBroadcastTargets()
    {
        var targets = new List<(IPAddress, IPAddress)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    targets.Add((unicast.Address, GetDirectedBroadcast(unicast.Address, unicast.IPv4Mask)));
                }
            }
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Wake", $"Could not list network adapters: {ex.Message}");
        }
        return targets;
    }

    /// <summary>The subnet's broadcast address: every host bit set.</summary>
    public static IPAddress GetDirectedBroadcast(IPAddress address, IPAddress? mask)
    {
        if (mask == null || mask.AddressFamily != AddressFamily.InterNetwork)
            return IPAddress.Broadcast;
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        for (var i = 0; i < 4; i++)
            a[i] |= (byte)~m[i];
        return new IPAddress(a);
    }
}
