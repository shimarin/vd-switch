using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Runtime.InteropServices;
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

    static readonly Control _invoker = new();
    static Form? _launcherForm;

    static string QuickLaunchPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch");

    // ── Shell icon extraction (256×256 jumbo) ────────────────────────────────

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    static Bitmap? ExtractJumboIcon(string path, int displaySize)
    {
        const uint SHGFI_SYSICONINDEX = 0x4000;
        const int  SHIL_JUMBO        = 0x4;
        const int  ILD_TRANSPARENT   = 0x1;
        var iid  = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
        var shfi = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf(shfi), SHGFI_SYSICONINDEX) == IntPtr.Zero)
            return null;
        if (SHGetImageList(SHIL_JUMBO, ref iid, out var list) != 0) return null;
        if (list.GetIcon(shfi.iIcon, ILD_TRANSPARENT, out var hIcon) != 0) return null;
        try
        {
            using var raw     = Icon.FromHandle(hIcon).ToBitmap();
            using var trimmed = TrimTransparent(raw);
            float scale  = Math.Min((float)displaySize / trimmed.Width, (float)displaySize / trimmed.Height);
            int   drawW  = (int)(trimmed.Width  * scale);
            int   drawH  = (int)(trimmed.Height * scale);
            int   offsetX = (displaySize - drawW) / 2;
            int   offsetY = (displaySize - drawH) / 2;
            var dst = new Bitmap(displaySize, displaySize);
            using var g = Graphics.FromImage(dst);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(trimmed, offsetX, offsetY, drawW, drawH);
            return dst;
        }
        finally { DestroyIcon(hIcon); }
    }

    static Bitmap TrimTransparent(Bitmap src)
    {
        int minX = src.Width, minY = src.Height, maxX = 0, maxY = 0;
        var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                byte alpha = Marshal.ReadByte(data.Scan0, y * data.Stride + x * 4 + 3);
                if (alpha == 0) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        finally { src.UnlockBits(data); }
        if (minX > maxX) return src; // fully transparent — return as-is
        return src.Clone(Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1), src.PixelFormat);
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            Environment.Exit(RunCli(args));
            return;
        }
        using var mutex = new Mutex(true, @"Local\vd-switch", out bool createdNew);
        if (!createdNew) return;
        RunTray();
    }

    // ── tray mode ────────────────────────────────────────────────────────────

    static void RunTray()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        _invoker.CreateControl();

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
                case "KEY_B": ShowLauncher(); break;
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

    static void ShowLauncher() => _invoker.BeginInvoke(ShowLauncherUI);

    static void ShowLauncherUI()
    {
        if (_launcherForm is { IsDisposed: false })
        {
            _launcherForm.Close();
            return;
        }

        var entries = Directory.Exists(QuickLaunchPath)
            ? Directory.GetFiles(QuickLaunchPath)
                .Where(f => (File.GetAttributes(f) &
                             (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .OrderBy(Path.GetFileNameWithoutExtension)
                .ToArray()
            : [];

        if (entries.Length == 0) return;

        const int iconSize = 128, tileW = 180, tileH = 180;
        var screen = Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position);
        int screenW = screen.WorkingArea.Width;
        int maxCols = Math.Max(1, (screenW - 32) / tileW);
        int rows = (int)Math.Ceiling((double)entries.Length / maxCols);
        int cols = (int)Math.Ceiling((double)entries.Length / rows);

        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Width  = cols * (tileW + 6) + 8,
            Height = rows * (tileH + 6) + 8,
            Padding = new Padding(4),
        };

        foreach (var path in entries)
        {
            var p = path;
            var name = Path.GetFileNameWithoutExtension(path);
            Bitmap? img = null;
            try { img = ExtractJumboIcon(path, iconSize); } catch { }

            var btn = new Button
            {
                Text = name,
                Image = img,
                ImageAlign = ContentAlignment.TopCenter,
                TextAlign = ContentAlignment.BottomCenter,
                TextImageRelation = TextImageRelation.ImageAboveText,
                Width = tileW,
                Height = tileH,
                Padding = new Padding(4),
            };
            btn.Click += (_, _) =>
            {
                _launcherForm?.Close();
                try { Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true }); }
                catch { }
            };
            panel.Controls.Add(btn);
        }

        _launcherForm = new Form
        {
            Text = "Quick Launch",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            TopMost = true,
            KeyPreview = true,
        };
        _launcherForm.Controls.Add(panel);
        _launcherForm.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) _launcherForm?.Close(); };
        _launcherForm.Deactivate += (_, _) => _launcherForm?.Close();
        _launcherForm.Show();
        var wa = screen.WorkingArea;
        _launcherForm.Location = new Point(
            wa.X + (wa.Width  - _launcherForm.Width)  / 2,
            wa.Y + (wa.Height - _launcherForm.Height) / 2);
        _launcherForm.Activate();
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
