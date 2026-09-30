using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RoboMouse.App.Services;

/// <summary>This computer's IPv4 addresses, for typing into another machine.</summary>
public static class LocalAddresses
{
    /// <summary>Replaces the real adapters, so previews and screenshots never show this machine's addresses.</summary>
    public static Func<IReadOnlyList<string>>? Override { get; set; }

    /// <summary>One entry per address on a connected adapter, as "address  ·  adapter name".</summary>
    public static IReadOnlyList<string> Describe()
    {
        if (Override is not null)
            return Override();
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                                && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    .Select(a => $"{a.Address}  ·  {n.Name}"))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
