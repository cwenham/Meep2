using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Meep2.Daemon;

/// <summary>The names and addresses of the machine a daemon runs on, by which tools place it.</summary>
public static class MachineAddresses
{
    /// <summary>
    /// The machine's names: its name as the system gives it, its host name, and that name as DNS knows it in full, if
    /// DNS answers within a second. Without duplicates, shortest first.
    /// </summary>
    public static async Task<IReadOnlyList<string>> NamesAsync(CancellationToken cancellationToken = default)
    {
        var names = new List<string> { Environment.MachineName, Dns.GetHostName() };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            var entry = await Dns.GetHostEntryAsync(Dns.GetHostName(), AddressFamily.Unspecified, timeout.Token).ConfigureAwait(false);
            names.Add(entry.HostName);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ArgumentException)
        {
            // DNS doesn't know it, or didn't answer in time.
        }
        return [.. names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The machine's addresses on the networks it's connected to, IPv4 first: those of interfaces that are up, without
    /// loopback and link-local ones, which other machines can't reach it by.
    /// </summary>
    public static IReadOnlyList<string> Addresses()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal && !IsIPv4LinkLocal(a))
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Select(a => a.ToString())
                .Distinct()];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    // 169.254.0.0/16, which a machine gives itself when nothing else gives it an address.
    private static bool IsIPv4LinkLocal(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, ..];
}
