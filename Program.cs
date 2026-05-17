using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using VirtualDesktop;

static class Program
{
    const string MulticastAddr = "ff12::7664:7377";
    const int MulticastPort = 5356;

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            Environment.Exit(RunCli(args));
            return;
        }
        RunTray();
    }

    // ── tray mode ────────────────────────────────────────────────────────────

    static void RunTray()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var cts = new CancellationTokenSource();

        using var trayIcon = BuildTrayIcon(cts);
        trayIcon.Visible = true;

        _ = Task.Run(() => ListenLoopAsync(trayIcon, cts.Token));

        Application.Run();
        cts.Cancel();
    }

    static NotifyIcon BuildTrayIcon(CancellationTokenSource cts)
    {
        var menu = new ContextMenuStrip();
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            menu.Items.Add($"Desktop {i + 1}", null, (_, _) => SwitchDesktop(idx));
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { cts.Cancel(); Application.Exit(); });

        return new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "vd-switch",
            ContextMenuStrip = menu,
        };
    }

    static Icon LoadIcon()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = Array.Find(asm.GetManifestResourceNames(), n => n.EndsWith(".ico"));
        if (name is null) return SystemIcons.Application;
        var stream = asm.GetManifestResourceStream(name)!;
        return new Icon(stream);
    }

    static async Task ListenLoopAsync(NotifyIcon trayIcon, CancellationToken ct)
    {
        var iface = FindLinkLocalInterface();
        if (iface is null)
        {
            trayIcon.ShowBalloonTip(4000, "vd-switch",
                "No link-local IPv6 interface found.", ToolTipIcon.Error);
            return;
        }

        using var sock = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        sock.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
        sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        sock.Bind(new IPEndPoint(IPAddress.IPv6Any, MulticastPort));
        sock.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership,
            new IPv6MulticastOption(IPAddress.Parse(MulticastAddr), iface.Value.Index));

        var buf = new byte[16];
        while (!ct.IsCancellationRequested)
        {
            int len;
            try { len = await sock.ReceiveAsync(buf, ct); }
            catch (OperationCanceledException) { break; }

            var key = Encoding.UTF8.GetString(buf, 0, len).Trim();
            int target = key switch { "a" => 0, "b" => 1, "c" => 2, _ => -1 };
            if (target >= 0)
                SwitchDesktop(target);
        }
    }

    static void SwitchDesktop(int index)
    {
        try
        {
            if (index < Desktop.Count)
                Desktop.FromIndex(index).MakeVisible();
        }
        catch { }
    }

    // ── CLI mode (引数あり) ──────────────────────────────────────────────────

    static int RunCli(string[] args)
    {
        switch (args[0])
        {
            case "--current":
                Console.WriteLine(Desktop.FromDesktop(Desktop.Current));
                return 0;
            case "--count":
                Console.WriteLine(Desktop.Count);
                return 0;
        }

        if (!int.TryParse(args[0], out int n))
        {
            Console.Error.WriteLine($"Invalid desktop number: {args[0]}");
            return 1;
        }
        if (n < 0 || n >= Desktop.Count)
        {
            Console.Error.WriteLine($"Desktop number out of range: {n} (0-{Desktop.Count - 1})");
            return 1;
        }
        Desktop.FromIndex(n).MakeVisible();
        return 0;
    }

    // ── shared ──────────────────────────────────────────────────────────────

    static (int Index, string Name)? FindLinkLocalInterface()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            var props = ni.GetIPProperties();
            if (props.UnicastAddresses.Any(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                    a.Address.IsIPv6LinkLocal))
                return (props.GetIPv6Properties().Index, ni.Name);
        }
        return null;
    }
}
