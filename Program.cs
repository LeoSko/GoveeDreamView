using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GoveeDreamView
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0 && args[0] == "--calibrate") { CliCalibrate(); return; }

            // Single instance: bail if another copy already holds the mutex.
            using var mutex = new System.Threading.Mutex(true, "GoveeDreamView_SingleInstance", out bool created);
            if (!created) return;

            Application.Run(new TrayApp());
        }

        // Headless calibration (run with the tray instance stopped). Saves the CCM and exits.
        private static void CliCalibrate()
        {
            var cfg = Config.Load();
            if (string.IsNullOrWhiteSpace(cfg.DeviceIp)) { Log.Write("CliCalibrate: set DeviceIp in config.ini"); return; }
            try
            {
                cfg.Ccm = RunWebCalibration(cfg, 10, Log.Write);
                cfg.Save();
                Log.Write("CliCalibrate done: Ccm=" + string.Join(",", Array.ConvertAll(cfg.Ccm, x => x.ToString("0.###"))));
            }
            catch (Exception ex) { Log.Write("CliCalibrate failed: " + ex); }
        }

        // Hosts the phone-camera page, waits for the phone, runs the sweep, returns the CCM.
        internal static double[] RunWebCalibration(Config cfg, int segments, Action<string> say)
        {
            using (var server = new CameraServer())
            {
                int port = server.Start(cfg.CalibPort);
                string url = "https://" + CameraServer.LocalIp() + ":" + port + "/";
                using (var status = new CalibStatusForm(url))
                {
                    status.Show();
                    Application.DoEvents();
                    say("Open on your phone: " + url);
                    server.SetStatus("Waiting for phone... open " + url);
                    if (!server.WaitForPhone(300000, Application.DoEvents))
                        throw new Exception("no phone connected (open " + url + " on your phone)");
                    say("Phone connected - starting colour sweep");

                    CalibResult res;
                    using (var client = new GoveeClient(cfg.DeviceIp, cfg.StretchToFit))
                    {
                        client.Activate(cfg.Brightness);
                        try
                        {
                            var cal = new Calibrator(client,
                                m => { say(m); server.SetStatus(m); status.SetStatus(m); Application.DoEvents(); },
                                () => server.LatestFrame(),
                                () => server.Lock(),
                                png => server.SetMask(png));
                            res = cal.Run(cfg.MonitorIndex, segments);
                        }
                        finally { try { client.Deactivate(); } catch (Exception ex) { Log.Write("calib deactivate: " + ex.Message); } }
                    }

                    // Surface the report: phone page, PC status window, and a file next to the exe.
                    say(res.Report);
                    server.SetStatus(res.Report);
                    status.SetStatus(res.Report);
                    try { File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "calibration-report.txt"), res.Report); }
                    catch (Exception ex) { Log.Write("report write: " + ex.Message); }

                    // Hold the page open ~60s so the result stays readable on the phone before we exit/restart.
                    int hold = Environment.TickCount + 60000;
                    while (Environment.TickCount < hold && !status.IsDisposed) { Application.DoEvents(); Thread.Sleep(50); }
                    return res.Ccm;
                }
            }
        }
    }

    internal static class Log
    {
        private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "govee.log");
        private static readonly object Gate = new object();

        public static void Write(string msg)
        {
            try
            {
                lock (Gate)
                    File.AppendAllText(Path, DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { /* logging must never take the app down */ }
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        private readonly NotifyIcon _tray;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _pauseOnLockItem;
        private readonly Icon _iconOff;   // muted grey: stopped
        private readonly System.Windows.Forms.Timer _animTimer;
        private Icon _animIcon;           // current live-colour frame while streaming
        private volatile int _liveRgb;    // latest dominant streamed colour, packed 0xRRGGBB

        private Config _cfg;
        private GoveeClient _client;
        private ScreenCapture _capture;
        private Thread _worker;
        private volatile bool _running;
        private bool _pausedByLock;    // true when a session lock stopped streaming, so unlock can resume
        private string _lastIp;        // last resolved device IP, so we can power it off after Stop() disposes the client
        private volatile bool _tuningDirty;   // set by the Settings menu so the loop rebuilds gamma/saturation live

        public TrayApp()
        {
            _cfg = Config.Load();

            _statusItem = new ToolStripMenuItem("Stopped") { Enabled = false };
            _toggleItem = new ToolStripMenuItem("Start", null, (s, e) => Toggle());
            _pauseOnLockItem = new ToolStripMenuItem("Pause when locked", null, (s, e) => TogglePauseOnLock())
            {
                Checked = _cfg.PauseOnLock,
                CheckOnClick = true,
            };
            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_toggleItem);
            menu.Items.Add(_pauseOnLockItem);
            menu.Items.Add(BuildSettingsMenu());
            menu.Items.Add("Reload config", null, (s, e) => ReloadConfig());
            menu.Items.Add("Edit config", null, (s, e) => OpenConfig());
            menu.Items.Add("Rediscover device", null, (s, e) => Rediscover());
            menu.Items.Add("Calibrate colours", null, (s, e) => Calibrate());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitApp());

            _iconOff = BuildTrayIcon(false, null);

            _tray = new NotifyIcon
            {
                Icon = _iconOff,   // while streaming, _animTimer swaps in live-colour frames
                Text = "Govee DreamView",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (s, e) => Toggle();

            _animTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _animTimer.Tick += (s, e) => AnimTick();

            SystemEvents.SessionSwitch += OnSessionSwitch;

            Start(); // auto-start on launch
        }

        private void Toggle()
        {
            if (_running) Stop();
            else Start();
        }

        // Tray tickbox: persist the choice. CheckOnClick has already flipped Checked.
        private void TogglePauseOnLock()
        {
            _cfg.PauseOnLock = _pauseOnLockItem.Checked;
            try { _cfg.Save(); } catch (Exception ex) { Log.Write("Save PauseOnLock: " + ex.Message); }
            if (!_cfg.PauseOnLock) _pausedByLock = false;
        }

        // Lock -> stop streaming and power the strip off. Unlock -> power on + resume, if we stopped it.
        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (!_cfg.PauseOnLock) return;
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                Log.Write("SessionLock" + (_running ? " -> stop + power off" : " (not running)"));
                if (_running)
                {
                    _pausedByLock = true;
                    Stop();        // halts the worker first, so no colour frame re-lights the strip after power-off
                    PowerOff();
                }
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                Log.Write("SessionUnlock" + (_pausedByLock ? " -> power on + resume" : " (nothing to resume)"));
                if (_pausedByLock) { _pausedByLock = false; Start(); }   // Activate() powers the device back on
            }
        }

        // Powers the strip off via a one-off client (Stop() has already disposed the streaming client).
        private void PowerOff()
        {
            if (string.IsNullOrEmpty(_lastIp)) return;
            try { using (var c = new GoveeClient(_lastIp, _cfg.StretchToFit)) c.Power(false); }
            catch (Exception ex) { Log.Write("PowerOff: " + ex.Message); }
        }

        // Live colour tuning. Brightness re-sends to the device; gamma/saturation flag the loop to
        // rebuild its LUT (no device round-trip, so no flicker). All three persist to config.ini.
        private ToolStripMenuItem BuildSettingsMenu()
        {
            var settings = new ToolStripMenuItem("Settings");
            settings.DropDownItems.Add(PresetMenu("Brightness",
                new[] { 25.0, 50, 75, 100 }, v => v + "%", _cfg.Brightness,
                v => { _cfg.Brightness = (int)v; SaveCfg(); try { _client?.SetBrightness(_cfg.Brightness); } catch (Exception ex) { Log.Write("SetBrightness: " + ex.Message); } }));
            settings.DropDownItems.Add(PresetMenu("Gamma",
                new[] { 1.8, 2.2, 2.6, 3.2 }, v => v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), _cfg.Gamma,
                v => { _cfg.Gamma = v; SaveCfg(); _tuningDirty = true; }));
            settings.DropDownItems.Add(PresetMenu("Saturation",
                new[] { 1.0, 1.25, 1.5, 1.75 }, v => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), _cfg.Saturation,
                v => { _cfg.Saturation = v; SaveCfg(); _tuningDirty = true; }));
            return settings;
        }

        // Radio-style preset list: the value matching current is ticked; picking one unticks the siblings.
        private static ToolStripMenuItem PresetMenu(string title, double[] values, Func<double, string> label, double current, Action<double> apply)
        {
            var parent = new ToolStripMenuItem(title);
            foreach (var value in values)
            {
                double v = value;
                var item = new ToolStripMenuItem(label(v)) { Checked = Math.Abs(v - current) < 1e-6 };
                item.Click += (s, e) =>
                {
                    foreach (ToolStripMenuItem sib in parent.DropDownItems) sib.Checked = false;
                    item.Checked = true;
                    apply(v);
                };
                parent.DropDownItems.Add(item);
            }
            return parent;
        }

        private void SaveCfg()
        {
            try { _cfg.Save(); } catch (Exception ex) { Log.Write("SaveCfg: " + ex.Message); }
        }

        // ~4 Hz: paint the icon's screen with the current dominant streamed colour, so the tray shows live.
        private void AnimTick()
        {
            if (!_running) return;
            int rgb = _liveRgb;
            var live = Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            var frame = BuildTrayIcon(true, live);
            _tray.Icon = frame;
            _animIcon?.Dispose();   // free the previous frame; the tray already holds the new one
            _animIcon = frame;
        }

        // Draws the tray icon at runtime (no embedded asset -> exe stays tiny): a colour-wheel ring
        // (the RGB ambient glow / segment perimeter) around a dark screen. Vivid = streaming, grey = stopped.
        private static Icon BuildTrayIcon(bool active, Color? screenColor)
        {
            const int S = 64;
            using (var bmp = new Bitmap(S, S))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    float o = S * 0.05f;
                    var ring = new RectangleF(o, o, S - 2 * o, S - 2 * o);
                    for (int a = 0; a < 360; a += 6)
                    {
                        using (var b = new SolidBrush(active ? FromHue(a) : Color.FromArgb(150, 96, 96, 104)))
                            g.FillPie(b, ring.X, ring.Y, ring.Width, ring.Height, a - 90, 6.5f);
                    }

                    float m = S * 0.28f;
                    var screen = new RectangleF(m, m, S - 2 * m, S - 2 * m);
                    using (var path = RoundedRect(screen, S * 0.09f))
                    using (var fill = new SolidBrush(screenColor ?? Color.FromArgb(26, 26, 34)))
                        g.FillPath(fill, path);
                }
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h))
                        return (Icon)tmp.Clone();   // clone owns its own managed data
                }
                finally { DestroyIcon(h); }         // FromHandle doesn't own h, so free it ourselves
            }
        }

        // Full-saturation colour for a hue angle 0-360.
        private static Color FromHue(int h)
        {
            double x = 1 - Math.Abs((h / 60.0) % 2 - 1);
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = 1; g = x; }
            else if (h < 120) { r = x; g = 1; }
            else if (h < 180) { g = 1; b = x; }
            else if (h < 240) { g = x; b = 1; }
            else if (h < 300) { r = x; b = 1; }
            else { r = 1; b = x; }
            return Color.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        private static GraphicsPath RoundedRect(RectangleF r, float rad)
        {
            float d = rad * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private void Start()
        {
            if (_running) return;
            try
            {
                string ip = ResolveDeviceIp();
                if (string.IsNullOrWhiteSpace(ip))
                {
                    Notify("No Govee device found. Set DeviceIp/DeviceMac in config.ini and check LAN Control is on.", ToolTipIcon.Error);
                    return;
                }

                _lastIp = ip;
                _client = new GoveeClient(ip, _cfg.StretchToFit);
                _capture = new ScreenCapture(_cfg.MonitorIndex);
                _client.Activate(_cfg.Brightness);

                _running = true;
                _worker = new Thread(Loop) { IsBackground = true, Name = "govee-stream" };
                _worker.Start();
                _animTimer.Start();

                SetStatus("Streaming -> " + ip);
            }
            catch (Exception ex)
            {
                Log.Write("Start failed: " + ex);
                Notify("Start failed: " + ex.Message, ToolTipIcon.Error);
                Stop();
            }
        }

        // Resolves the strip's current IP. Priority: fixed MAC (DHCP-proof) -> last-known IP -> LAN scan.
        // Whatever works is remembered (IP always; MAC learned once) so later starts survive DHCP changes.
        private string ResolveDeviceIp()
        {
            var mac = DeviceLocator.ParseMac(_cfg.DeviceMac);
            string ip = null;

            if (mac != null)
            {
                Notify("Locating Govee by MAC...", ToolTipIcon.Info);
                ip = DeviceLocator.FindByMac(mac);
                Log.Write(ip != null ? "Located " + _cfg.DeviceMac + " at " + ip
                                     : "MAC " + _cfg.DeviceMac + " not on LAN yet");
            }

            if (ip == null && !string.IsNullOrWhiteSpace(_cfg.DeviceIp))
                ip = _cfg.DeviceIp.Trim();

            if (ip == null)
            {
                Notify("Discovering Govee device...", ToolTipIcon.Info);
                var found = GoveeClient.Discover();
                if (found != null) { ip = found.Value.ip; Log.Write("Discovered " + ip + " sku=" + found.Value.sku); }
            }

            if (ip == null) return null;

            // Remember what worked: keep the IP fresh, and learn the MAC once so future starts find it by MAC.
            bool dirty = false;
            if (_cfg.DeviceIp != ip) { _cfg.DeviceIp = ip; dirty = true; }
            if (mac == null)
            {
                var learned = DeviceLocator.MacOf(ip);
                if (learned != null) { _cfg.DeviceMac = DeviceLocator.FormatMac(learned); dirty = true; Log.Write("Learned MAC " + _cfg.DeviceMac); }
            }
            if (dirty) { try { _cfg.Save(); } catch { } }
            return ip;
        }

        private void Stop()
        {
            _running = false;
            _animTimer.Stop();
            if (_worker != null && _worker.IsAlive) _worker.Join(500);
            _worker = null;

            try { _client?.Deactivate(); } catch (Exception ex) { Log.Write("Deactivate: " + ex.Message); }
            _client?.Dispose();
            _capture?.Dispose();
            _client = null;
            _capture = null;

            SetStatus("Stopped");   // resets _tray.Icon to _iconOff, so the anim frame is safe to free
            _animIcon?.Dispose();
            _animIcon = null;
        }

        // H6609 DreamView segment layout: device order = strip segments 1..10, each mapped to a
        // normalised screen rectangle {x0,y0,x1,y1}. Clockwise perimeter from top-left:
        //   1 2 3 4 (top L->R)  5 (right-mid)  6 7 8 9 (bottom R->L)  10 (left-mid)
        private static readonly double[][] Zones =
        {
            new[] {0.00, 0.00, 0.25, 0.34},  // 1  top-left
            new[] {0.25, 0.00, 0.50, 0.34},  // 2  top-centre-left
            new[] {0.50, 0.00, 0.75, 0.34},  // 3  top-centre-right
            new[] {0.75, 0.00, 1.00, 0.34},  // 4  top-right
            new[] {0.75, 0.34, 1.00, 0.66},  // 5  right-middle
            new[] {0.75, 0.66, 1.00, 1.00},  // 6  bottom-right
            new[] {0.50, 0.66, 0.75, 1.00},  // 7  bottom-centre-right
            new[] {0.25, 0.66, 0.50, 1.00},  // 8  bottom-centre-left
            new[] {0.00, 0.66, 0.25, 1.00},  // 9  bottom-left
            new[] {0.00, 0.34, 0.25, 0.66},  // 10 left-middle
        };

        private void Loop()
        {
            int n = Zones.Length;
            var cur = new byte[n * 3];
            var outRgb = new byte[n * 3];
            var ema = new double[n * 3];
            bool primed = false;

            double s = _cfg.Smoothing;
            var m = _cfg.Ccm;   // 3x3 colour correction, applied after gamma
            int frameMs = Math.Max(1, 1000 / _cfg.Fps);

            // Saturation + perceptual gamma LUT (linear 0-255 -> gamma-corrected). Rebuilt live when
            // the tray Settings menu changes gamma/saturation, so tuning takes effect without a restart.
            double sat = _cfg.Saturation;
            var lut = new byte[256];
            void RebuildTuning()
            {
                sat = _cfg.Saturation;
                double g = _cfg.Gamma;
                for (int i = 0; i < 256; i++)
                    lut[i] = (byte)Math.Round(255.0 * Math.Pow(i / 255.0, g));
            }
            RebuildTuning();

            timeBeginPeriod(1); // sharpen Thread.Sleep so frame pacing holds
            var sw = new System.Diagnostics.Stopwatch();
            int fails = 0;
            try
            {
                while (_running)
                {
                    sw.Restart();
                    if (_tuningDirty) { RebuildTuning(); _tuningDirty = false; }
                    try
                    {
                        _capture.Capture();
                        for (int z = 0; z < n; z++)
                        {
                            var q = Zones[z];
                            _capture.AverageRegion(q[0], q[1], q[2], q[3], cur, z * 3);
                        }

                        for (int i = 0; i < cur.Length; i++)
                            ema[i] = primed ? ema[i] * s + cur[i] * (1.0 - s) : cur[i];
                        primed = true;

                        for (int z = 0; z < n; z++)
                        {
                            int o = z * 3;
                            double r = ema[o], g = ema[o + 1], b = ema[o + 2];
                            if (sat != 1.0)
                            {
                                double gray = 0.299 * r + 0.587 * g + 0.114 * b;
                                r = gray + (r - gray) * sat;
                                g = gray + (g - gray) * sat;
                                b = gray + (b - gray) * sat;
                            }
                            double pr = lut[Clamp(r)], pg = lut[Clamp(g)], pb = lut[Clamp(b)];
                            outRgb[o] = Clamp(m[0] * pr + m[1] * pg + m[2] * pb);
                            outRgb[o + 1] = Clamp(m[3] * pr + m[4] * pg + m[5] * pb);
                            outRgb[o + 2] = Clamp(m[6] * pr + m[7] * pg + m[8] * pb);
                        }

                        long ar = 0, ag = 0, ab = 0;
                        for (int z = 0; z < n; z++) { ar += outRgb[z * 3]; ag += outRgb[z * 3 + 1]; ab += outRgb[z * 3 + 2]; }
                        _liveRgb = (int)((ar / n) << 16 | (ag / n) << 8 | (ab / n));   // for the tray anim

                        _client.SendColors(outRgb, n);
                        fails = 0;
                    }
                    catch (Exception ex)
                    {
                        if (++fails == 1 || fails % 200 == 0) Log.Write("Frame error: " + ex.Message);
                    }

                    int wait = frameMs - (int)sw.ElapsedMilliseconds;
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            finally
            {
                timeEndPeriod(1);
            }
        }

        private static byte Clamp(double v) => v <= 0 ? (byte)0 : (v >= 255 ? (byte)255 : (byte)v);

        private void Calibrate()
        {
            if (string.IsNullOrWhiteSpace(_cfg.DeviceIp))
            {
                Notify("Set DeviceIp in config.ini before calibrating.", ToolTipIcon.Error);
                return;
            }
            bool was = _running;
            Stop();
            try
            {
                _cfg.Ccm = Program.RunWebCalibration(_cfg, Zones.Length, m => { Log.Write(m); SetStatus(m); });
                _cfg.Save();
                Notify("Calibration done - colour matrix saved.", ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Write("Calibrate: " + ex);
                Notify("Calibration failed: " + ex.Message, ToolTipIcon.Error);
            }
            if (was) Start();
        }

        private void ReloadConfig()
        {
            bool was = _running;
            if (was) Stop();
            _cfg = Config.Load();
            if (was) Start();
            Notify("Config reloaded", ToolTipIcon.Info);
        }

        private void OpenConfig()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Config.FilePath) { UseShellExecute = true }); }
            catch (Exception ex) { Notify("Cannot open config: " + ex.Message, ToolTipIcon.Error); }
        }

        private void Rediscover()
        {
            Stop();
            _cfg.DeviceIp = "";
            _cfg.Save();
            Start();
        }

        private void SetStatus(string text)
        {
            void Apply()
            {
                _statusItem.Text = text;
                _toggleItem.Text = _running ? "Stop" : "Start";
                if (!_running) _tray.Icon = _iconOff;   // while running, _animTimer drives the icon
                _tray.Text = ("Govee DreamView - " + text).Substring(0, Math.Min(63, ("Govee DreamView - " + text).Length));
            }
            if (_tray.ContextMenuStrip.InvokeRequired) _tray.ContextMenuStrip.BeginInvoke((Action)Apply);
            else Apply();
        }

        private void Notify(string msg, ToolTipIcon icon)
        {
            Log.Write(msg);
            if (icon == ToolTipIcon.Info || icon == ToolTipIcon.None) return;   // only surface warnings/errors as balloons
            _tray.BalloonTipTitle = "Govee DreamView";
            _tray.BalloonTipText = msg;
            _tray.BalloonTipIcon = icon;
            _tray.ShowBalloonTip(2500);
        }

        private void ExitApp()
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            Stop();
            _animTimer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _animIcon?.Dispose();
            _iconOff?.Dispose();
            ExitThread();
        }
    }
}
