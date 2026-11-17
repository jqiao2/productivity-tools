// BatteryBar: shows Bluetooth peripheral battery levels on the Windows taskbar.
// Source: the battery % Windows itself caches per Bluetooth device (same data as Settings > Bluetooth).
// Build: build.cmd (uses the C# compiler that ships with Windows, no installs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

class Dev { public string Name; public int Pct; public int Kind; }

// AirPods don't report battery to Windows; read it from Apple's "proximity pairing" BLE adverts
// (manufacturer 0x004C, type 0x07) that the buds broadcast every few seconds.
static class AirPods
{
    public static int Model = -1;   // e.g. 0x2014, from the connected AirPods' PnP PID
    public static Action Changed;
    static int left = -1, right = -1, box = -1;
    static DateTime seen = DateTime.MinValue;
    static BluetoothLEAdvertisementWatcher watcher;

    public static void Start()
    {
        watcher = new BluetoothLEAdvertisementWatcher();
        watcher.Received += OnAd;
        watcher.Start();
    }

    static void OnAd(BluetoothLEAdvertisementWatcher w, BluetoothLEAdvertisementReceivedEventArgs a)
    {
        if (a.RawSignalStrengthInDBm < -70) return; // ponytail: model + RSSI filter only; a neighbour's identical model at close range could win
        foreach (var md in a.Advertisement.GetManufacturerDataByCompanyId(0x004C))
        {
            var b = new byte[md.Data.Length];
            DataReader.FromBuffer(md.Data).ReadBytes(b);
            if (Parse(b, Model)) { seen = DateTime.Now; if (Changed != null) Changed(); }
        }
    }

    // b[3..4] model (LE), b[6] = two bud nibbles, b[7] low nibble = case. 0-10 => x10%, 15 = unknown.
    public static bool Parse(byte[] b, int model)
    {
        if (b.Length < 8 || b[0] != 0x07 || (b[3] | b[4] << 8) != model) return false;
        int l = Nib(b[6] >> 4), r = Nib(b[6] & 0xF), c = Nib(b[7] & 0xF);
        bool changed = l != left || r != right || c != box;
        left = l; right = r; box = c;
        return changed;
    }
    static int Nib(int n) { return n <= 10 ? n * 10 : -1; }

    public static int Pct()
    {
        if ((DateTime.Now - seen).TotalMinutes > 3) return -1;
        if (left >= 0 && right >= 0) return Math.Min(left, right);
        return Math.Max(left, right);
    }
    public static string Detail()
    {
        return " (buds " + Fmt(left) + " / " + Fmt(right) + ", case " + Fmt(box) + ")";
    }
    static string Fmt(int v) { return v < 0 ? "--" : v + "%"; }
}

static class Bt
{
    [StructLayout(LayoutKind.Sequential)] struct DevInfo { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct PropKey { public Guid fmtid; public int pid; public PropKey(string g, int p) { fmtid = new Guid(g); pid = p; } }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevsW(IntPtr cls, string enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiEnumDeviceInfo(IntPtr set, int i, ref DevInfo d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DevInfo d, StringBuilder sb, int size, out int req);
    [DllImport("setupapi.dll")] static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DevInfo d, ref PropKey k, out int type, byte[] buf, int size, out int req, int flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    static PropKey Battery = new PropKey("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    static PropKey Connected = new PropKey("83DA6326-97A6-4088-9453-A1923F573B29", 15);
    static PropKey FriendlyName = new PropKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
    static Regex MacAtEnd = new Regex(@"([0-9A-F]{12})(_C[0-9A-F]+)?$", RegexOptions.IgnoreCase);
    static Regex AppleModel = new Regex(@"VID&0001004C_PID&([0-9A-F]{4})", RegexOptions.IgnoreCase);
    static Regex Parent =new Regex(@"^BTH(ENUM|LE)\\DEV_", RegexOptions.IgnoreCase);

    static byte[] Prop(IntPtr set, ref DevInfo d, PropKey k)
    {
        var buf = new byte[512]; int type, req;
        return SetupDiGetDevicePropertyW(set, ref d, ref k, out type, buf, buf.Length, out req, 0) ? buf : null;
    }

    // Battery lives on the device node (BLE) or on a profile child node (classic BT, e.g. Hands-Free AG).
    // Both instance IDs end in the device MAC, so join on that and keep only connected devices.
    public static List<Dev> Scan()
    {
        var names = new Dictionary<string, string>();
        var batt = new Dictionary<string, int>();
        var models = new Dictionary<string, int>();
        foreach (var en in new[] { "BTHENUM", "BTHLE" })
        {
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, en, IntPtr.Zero, 0x2 | 0x4); // PRESENT | ALLCLASSES
            if (set == new IntPtr(-1)) continue;
            var d = new DevInfo(); d.cbSize = Marshal.SizeOf(d);
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
            {
                var sb = new StringBuilder(512); int req;
                if (!SetupDiGetDeviceInstanceIdW(set, ref d, sb, sb.Capacity, out req)) continue;
                string id = sb.ToString();
                var m = MacAtEnd.Match(id);
                if (!m.Success) continue;
                string mac = m.Groups[1].Value.ToUpperInvariant();
                if (Parent.IsMatch(id))
                {
                    var c = Prop(set, ref d, Connected);
                    var n = Prop(set, ref d, FriendlyName);
                    if (c != null && c[0] != 0 && n != null)
                        names[mac] = Encoding.Unicode.GetString(n).Split('\0')[0];
                }
                var b = Prop(set, ref d, Battery);
                if (b != null && b[0] <= 100) batt[mac] = b[0];
                var apple = AppleModel.Match(id);
                if (apple.Success) models[mac] = Convert.ToInt32(apple.Groups[1].Value, 16);
            }
            SetupDiDestroyDeviceInfoList(set);
        }
        var list = new List<Dev>();
        foreach (var kv in names)
        {
            if (batt.ContainsKey(kv.Key))
                list.Add(new Dev { Name = kv.Value, Pct = batt[kv.Key], Kind = KindOf(kv.Value) });
            else if (models.ContainsKey(kv.Key))
            {
                AirPods.Model = models[kv.Key];
                if (AirPods.Pct() >= 0)
                    list.Add(new Dev { Name = kv.Value + AirPods.Detail(), Pct = AirPods.Pct(), Kind = KindOf(kv.Value) });
            }
        }
        list.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.Compare(a.Name, b.Name));
        return list;
    }

    // Order = display priority. Glyphs are Segoe Fluent Icons / MDL2 code points.
    public static readonly string[] Glyphs = { "", "", "", "", "", "", "" };
    static readonly string[] Patterns = {
        "mouse|master|anywhere|mx ergo|trackball|g pro|g30|g70|g90",       // mouse
        "key|keyboard|k380|k780|craft",                                     // keyboard
        "airpods|buds|earbud|openrun|shokz|wf-|earfun|beats fit|powerbeats", // earbuds
        "headphone|qc|bose|wh-|xm\\d|headset|beats|sennheiser|momentum|arctis", // headphones
        "controller|xbox|dualsense|dualshock|gamepad|joy-con|8bitdo",       // gamepad
        "phone|iphone|galaxy|pixel",                                        // phone
    };
    static int KindOf(string name)
    {
        for (int i = 0; i < Patterns.Length; i++)
            if (Regex.IsMatch(name, Patterns[i], RegexOptions.IgnoreCase)) return i;
        return Patterns.Length; // generic bluetooth
    }
}

class Bar : Form
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, int flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref Point pos, ref Size size, IntPtr src, ref Point srcPos, int key, ref Blend b, int flags);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Op, Flags, Alpha, Format; }

    const string RegKey = @"Software\BatteryBar";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    List<Dev> devs = new List<Dev>();
    Font iconFont, textFont;
    float scale;
    int offset; // px from taskbar's right edge to our right edge; 0 = auto (left of tray)
    int dragX = -1;
    ToolTip tip = new ToolTip();
    string tipText = "";

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x8 | 0x08000000; // LAYERED | TOOLWINDOW | TOPMOST | NOACTIVATE
            return cp;
        }
    }

    Bar()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
        iconFont = new Font("Segoe Fluent Icons", 14 * scale, GraphicsUnit.Pixel);
        if (iconFont.Name != "Segoe Fluent Icons") iconFont = new Font("Segoe MDL2 Assets", 14 * scale, GraphicsUnit.Pixel);
        textFont = new Font("Segoe UI", 12 * scale, GraphicsUnit.Pixel);
        using (var k = Registry.CurrentUser.OpenSubKey(RegKey)) if (k != null) offset = (int)k.GetValue("Offset", 0);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh", null, delegate { Refresh2(); });
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupEnabled() };
        startup.Click += delegate { SetStartup(!startup.Checked); startup.Checked = StartupEnabled(); };
        menu.Items.Add(startup);
        menu.Items.Add("Reset position", null, delegate { offset = 0; SaveOffset(); Render(); });
        menu.Items.Add("Exit", null, delegate { Application.Exit(); });
        ContextMenuStrip = menu;

        int downX = 0; bool dragged = false;
        MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { dragX = downX = Cursor.Position.X; dragged = false; } };
        MouseMove += (s, e) =>
        {
            if (dragX < 0) return;
            if (!dragged && Math.Abs(Cursor.Position.X - downX) < 4 * scale) return; // still a click
            dragged = true;
            if (offset == 0) offset = Taskbar().R - Right;
            offset -= Cursor.Position.X - dragX; dragX = Cursor.Position.X;
            Render();
        };
        MouseUp += (s, e) =>
        {
            if (dragX < 0) return;
            dragX = -1;
            if (dragged) SaveOffset();
            // No public per-device Settings URI exists; this page lists each device with its battery.
            else System.Diagnostics.Process.Start("ms-settings:bluetooth");
        };

        var scan = new Timer { Interval = 30000 }; scan.Tick += delegate { Refresh2(); }; scan.Start();
        // Taskbar is topmost too; re-assert z-order often, and get out of the way of fullscreen apps.
        var z = new Timer { Interval = 300 }; z.Tick += delegate { KeepOnTop(); }; z.Start();
        Load += delegate { Refresh2(); };
        AirPods.Changed = delegate { if (IsHandleCreated) BeginInvoke(new Action(Refresh2)); };
        try { AirPods.Start(); } catch (Exception e) { Log("BLE watcher failed: " + e.Message); }
    }

    void Refresh2() { devs = Bt.Scan(); Render(); }

    RECT Taskbar() { RECT r; GetWindowRect(FindWindow("Shell_TrayWnd", null), out r); return r; }

    void Render()
    {
        bool light = false;
        using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            if (k != null) light = (int)k.GetValue("SystemUsesLightTheme", 0) == 1;
        Color fg = light ? Color.FromArgb(26, 26, 26) : Color.White;
        Color low = Color.FromArgb(255, 90, 79);

        var tb = Taskbar();
        int h = tb.B - tb.T;
        if (h <= 0 || h > 200 * scale) h = (int)(48 * scale); // vertical/hidden taskbar fallback
        var items = devs.Count > 0 ? devs : new List<Dev> { new Dev { Name = "No connected devices with battery info", Pct = -1, Kind = Bt.Glyphs.Length - 1 } };

        int pad = (int)(8 * scale), gap = (int)(3 * scale), sep = (int)(12 * scale);
        var sbTip = new StringBuilder();
        var widths = new List<int[]>();
        int w = pad;
        using (var tmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(tmp))
            foreach (var d in items)
            {
                int iw = (int)Math.Ceiling(g.MeasureString(Bt.Glyphs[d.Kind], iconFont, PointF.Empty, StringFormat.GenericTypographic).Width);
                int tw = (int)Math.Ceiling(g.MeasureString(Label(d), textFont, PointF.Empty, StringFormat.GenericTypographic).Width);
                widths.Add(new[] { iw, tw });
                w += iw + gap + tw + sep;
                sbTip.AppendLine(d.Pct < 0 ? d.Name : d.Name + ": " + d.Pct + "%");
            }
        w += pad - sep;

        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(1, 0, 0, 0)); // alpha 1 so the whole strip is clickable
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float x = pad;
                for (int i = 0; i < items.Count; i++)
                {
                    var d = items[i];
                    using (var br = new SolidBrush(d.Pct >= 0 && d.Pct <= 20 ? low : fg))
                    {
                        g.DrawString(Bt.Glyphs[d.Kind], iconFont, br, x, (h - iconFont.Height) / 2f + scale, StringFormat.GenericTypographic);
                        x += widths[i][0] + gap;
                        g.DrawString(Label(d), textFont, br, x, (h - textFont.Height) / 2f, StringFormat.GenericTypographic);
                        x += widths[i][1] + sep;
                    }
                }
            }

            int right;
            if (offset != 0) right = tb.R - offset;
            else
            {
                RECT tray; IntPtr t = FindWindowEx(FindWindow("Shell_TrayWnd", null), IntPtr.Zero, "TrayNotifyWnd", null);
                right = t != IntPtr.Zero && GetWindowRect(t, out tray) && tray.L > tb.L ? tray.L - (int)(4 * scale) : tb.R - (int)(300 * scale);
            }
            right = Math.Max(tb.L + w, Math.Min(tb.R, right));
            Present(bmp, new Point(right - w, tb.T + (tb.B - tb.T - h) / 2));
        }

        string text = sbTip.ToString().TrimEnd();
        if (text != tipText) { tipText = text; tip.SetToolTip(this, text); }
    }

    static string Label(Dev d) { return d.Pct < 0 ? "--" : d.Pct + "%"; }

    void Present(Bitmap bmp, Point pos)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
        var size = bmp.Size; var src = Point.Empty;
        var blend = new Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
        if (!UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2)) // ULW_ALPHA
            Log("UpdateLayeredWindow failed, error " + Marshal.GetLastWin32Error());
        SelectObject(mem, old); DeleteObject(hbmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
    }

    void KeepOnTop()
    {
        // Hide only when something actually covers the taskbar (fullscreen game/video). Rect or
        // SHQueryUserNotificationState checks misfire on frameless apps that report full-monitor rects.
        var tb = Taskbar();
        IntPtr hit = GetAncestor(WindowFromPoint(new Point(tb.L + 2, tb.B - 2)), 2); // GA_ROOT
        bool fullscreen = hit != FindWindow("Shell_TrayWnd", null) && hit != Handle;
        if (fullscreen) { if (Visible) Hide(); return; }
        if (!Visible) { Show(); Render(); }
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10); // TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
    }

    void SaveOffset() { using (var k = Registry.CurrentUser.CreateSubKey(RegKey)) k.SetValue("Offset", offset); }
    static bool StartupEnabled() { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("BatteryBar") != null; }
    static void SetStartup(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            if (on) k.SetValue("BatteryBar", "\"" + Application.ExecutablePath + "\""); else k.DeleteValue("BatteryBar", false);
    }

    static void Log(string s)
    {
        try { System.IO.File.AppendAllText(Application.ExecutablePath + ".log", DateTime.Now + " " + s + Environment.NewLine); } catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--test")
        {
            // Self-check of the advert parser: buds 8/7 (x10%), case 5, model 0x2014.
            var ad = new byte[] { 0x07, 0x19, 0x01, 0x14, 0x20, 0x2B, 0x87, 0x05 };
            if (!AirPods.Parse(ad, 0x2014) || AirPods.Parse(ad, 0x200E)) throw new Exception("model match");
            if (AirPods.Detail() != " (buds 80% / 70%, case 50%)") throw new Exception("nibbles: " + AirPods.Detail());
            Console.WriteLine("ok");
            return;
        }
        if (args.Length > 0 && args[0] == "--dump")
        {
            Bt.Scan(); // learns the AirPods model
            AirPods.Start();
            System.Threading.Thread.Sleep(8000);
            foreach (var d in Bt.Scan()) Console.WriteLine(d.Name + " | " + d.Pct + "% | kind " + d.Kind);
            return;
        }
        Application.ThreadException += (s, e) => Log(e.Exception.ToString());
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject.ToString());
        bool first;
        using (new System.Threading.Mutex(true, "BatteryBar.SingleInstance", out first))
        {
            if (!first) return;
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.Run(new Bar());
        }
    }
}
