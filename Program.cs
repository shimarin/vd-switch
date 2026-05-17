using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using VirtualDesktop;

const string MulticastAddr = "ff12::7664:7377";
const int MulticastPort = 5356;

if (args.Length == 0)
{
    Console.WriteLine($"{Desktop.FromDesktop(Desktop.Current)}/{Desktop.Count}");
    return 0;
}

switch (args[0])
{
    case "--current":
        Console.WriteLine(Desktop.FromDesktop(Desktop.Current));
        return 0;
    case "--count":
        Console.WriteLine(Desktop.Count);
        return 0;
    case "listen":
        return await ListenAsync();
}

if (!int.TryParse(args[0], out int desktopNumber))
{
    Console.Error.WriteLine($"Invalid desktop number: {args[0]}");
    return 1;
}

if (desktopNumber < 0 || desktopNumber >= Desktop.Count)
{
    Console.Error.WriteLine($"Desktop number out of range: {desktopNumber} (0-{Desktop.Count - 1})");
    return 1;
}

Desktop.FromIndex(desktopNumber).MakeVisible();
return 0;

// ── listen mode ──────────────────────────────────────────────────────────────

static (int Index, string Name)? FindLinkLocalInterface()
{
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        var props = ni.GetIPProperties();
        bool hasLinkLocal = props.UnicastAddresses.Any(a =>
            a.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
            a.Address.IsIPv6LinkLocal);
        if (hasLinkLocal)
            return (props.GetIPv6Properties().Index, ni.Name);
    }
    return null;
}

static async Task<int> ListenAsync()
{
    var iface = FindLinkLocalInterface();
    if (iface is null)
    {
        Console.Error.WriteLine("No link-local IPv6 interface found.");
        return 1;
    }

    var mcast = IPAddress.Parse(MulticastAddr);
    using var sock = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
    sock.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
    sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    sock.Bind(new IPEndPoint(IPAddress.IPv6Any, MulticastPort));
    sock.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
        new IPv6MulticastOption(mcast, iface.Value.Index));

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    Console.WriteLine($"Interface: {iface.Value.Name}");
    Console.WriteLine($"Multicast: [{MulticastAddr}%{iface.Value.Name}]:{MulticastPort}");
    Console.WriteLine("  a=desktop 0  b=desktop 1  c=desktop 2");
    Console.WriteLine("Ctrl-C to quit");

    var keyMap = new Dictionary<string, int> { ["a"] = 0, ["b"] = 1, ["c"] = 2 };

    var buf = new byte[16];
    while (!cts.IsCancellationRequested)
    {
        int len;
        try
        {
            len = await sock.ReceiveAsync(buf, cts.Token);
        }
        catch (OperationCanceledException) { break; }

        var key = Encoding.UTF8.GetString(buf, 0, len).Trim();
        if (keyMap.TryGetValue(key, out int target))
        {
            if (target < Desktop.Count)
            {
                Desktop.FromIndex(target).MakeVisible();
                Console.WriteLine($"[{key}] → desktop {target}");
            }
            else
            {
                Console.WriteLine($"[{key}] desktop {target} does not exist (count={Desktop.Count})");
            }
        }
        else
        {
            Console.WriteLine($"[?] unknown key: {key}");
        }
    }

    return 0;
}
