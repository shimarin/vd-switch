using System.Diagnostics;
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

        var buf = new byte[32];
        while (!ct.IsCancellationRequested)
        {
            int len;
            try { len = await sock.ReceiveAsync(buf, ct); }
            catch (OperationCanceledException) { break; }

            // Protocol: "KEY_X DOWN" or "KEY_X UP" (UDP, UP may be lost — receiver's responsibility)
            var parts = Encoding.UTF8.GetString(buf, 0, len).Trim().Split(' ');
            if (parts.Length != 2 || parts[1] != "DOWN") continue;
            switch (parts[0])
            {
                case "KEY_A": SwitchRelative(-1); break;
                case "KEY_B": OpenTaskView();     break;
                case "KEY_C": SwitchRelative(+1); break;
            }
        }
    }

    static void SwitchDesktop(int index)
    {
        try
        {
            if (index >= 0 && index < Desktop.Count)
                Desktop.FromIndex(index).MakeVisible();
        }
        catch { }
    }

    static void SwitchRelative(int delta)
    {
        try
        {
            int next = Desktop.FromDesktop(Desktop.Current) + delta;
            if (next >= 0 && next < Desktop.Count)
                Desktop.FromIndex(next).MakeVisible();
        }
        catch { }
    }

    static void OpenTaskView()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shell:::{3080F90E-D7AD-11D9-BD98-0000947B0257}",
                UseShellExecute = true,
            });
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
