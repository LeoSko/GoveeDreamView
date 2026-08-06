using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GoveeDreamView
{
    // Flat key=value config saved next to the exe. Kept dependency-free (no JSON lib)
    // so the binary stays tiny and runs on a stock net48 install.
    internal sealed class Config
    {
        public string DeviceIp = "";        // last-known IPv4 (auto-updated); empty => discover
        public string DeviceMac = "";       // fixed hardware MAC; resolved to current IP so DHCP changes don't break us
        public int Fps = 30;                // colour frames sent per second
        public int Brightness = 100;        // 0-100, sent once when streaming starts
        public bool StretchToFit = false;   // true stretches the N colours across all LEDs (some devices only)
        public double Smoothing = 0.45;     // temporal EMA 0..1 (0 = none, higher = steadier/laggier)
        public double Saturation = 1.25;    // >1 boosts colour vividness
        public double Gamma = 2.2;          // >1 darkens dark/mid tones so dark areas stay dark
        public int MonitorIndex = 0;        // which display to capture (0 = primary)
        public bool StreamEvenIfGoveeDesktop = false;  // false = don't auto-stream while Govee's own desktop app runs (they fight over the strip); true = stream anyway
        public bool KillGoveeDesktop = false;          // true = kill GoveeDesktop.exe on every start instead of stepping aside

        // --- colour correction (filled in by tray -> Calibrate colours) ---
        public double[] Ccm = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };   // 3x3 row-major, applied after gamma
        public int CalibPort = 8443;        // HTTPS port for the phone calibration page

        public static string FilePath =>
            Path.Combine(AppContext.BaseDirectory, "config.ini");

        public static Config Load()
        {
            var c = new Config();
            if (!File.Exists(FilePath))
            {
                c.Save();
                return c;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(FilePath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            c.DeviceIp = Get(map, "DeviceIp", c.DeviceIp);
            c.DeviceMac = Get(map, "DeviceMac", c.DeviceMac);
            c.Fps = Clamp(GetInt(map, "Fps", c.Fps), 1, 120);
            c.Brightness = Clamp(GetInt(map, "Brightness", c.Brightness), 1, 100);
            c.StretchToFit = GetBool(map, "StretchToFit", c.StretchToFit);
            c.Smoothing = ClampD(GetDouble(map, "Smoothing", c.Smoothing), 0.0, 0.95);
            c.Saturation = ClampD(GetDouble(map, "Saturation", c.Saturation), 0.0, 3.0);
            c.Gamma = ClampD(GetDouble(map, "Gamma", c.Gamma), 0.1, 5.0);
            c.MonitorIndex = Math.Max(0, GetInt(map, "MonitorIndex", c.MonitorIndex));
            c.StreamEvenIfGoveeDesktop = GetBool(map, "StreamEvenIfGoveeDesktop", c.StreamEvenIfGoveeDesktop);
            c.KillGoveeDesktop = GetBool(map, "KillGoveeDesktop", c.KillGoveeDesktop);
            c.Ccm = GetDoubles(map, "Ccm", c.Ccm, 9);
            c.CalibPort = Clamp(GetInt(map, "CalibPort", c.CalibPort), 1024, 65535);
            return c;
        }

        public void Save()
        {
            var text =
                "# GoveeDreamView config. Edit, then use the tray menu 'Reload config'.\n" +
                "# DeviceIp: last-known IP (auto-updated). DeviceMac: fixed device MAC, resolved to the\n" +
                "# current IP each start so DHCP changes can't lose the device. Set either, or leave both blank to scan.\n" +
                "DeviceIp=" + DeviceIp + "\n" +
                "DeviceMac=" + DeviceMac + "\n" +
                "Fps=" + Fps + "\n" +
                "Brightness=" + Brightness + "\n" +
                "# StretchToFit: true stretches the colours across all LEDs (only some devices support it).\n" +
                "StretchToFit=" + (StretchToFit ? "true" : "false") + "\n" +
                "# Smoothing: 0 = instant/jittery, 0.45 = balanced, 0.8 = very smooth but laggy.\n" +
                "Smoothing=" + Smoothing.ToString(CultureInfo.InvariantCulture) + "\n" +
                "Saturation=" + Saturation.ToString(CultureInfo.InvariantCulture) + "\n" +
                "# Gamma: >1 darkens dark/mid tones (perceptual). 2.2 typical; raise (e.g. 2.8) for darker blacks.\n" +
                "Gamma=" + Gamma.ToString(CultureInfo.InvariantCulture) + "\n" +
                "MonitorIndex=" + MonitorIndex + "\n" +
                "# StreamEvenIfGoveeDesktop: false = don't auto-stream while Govee's own desktop app is\n" +
                "# running (both drive the strip and it blinks). true = stream anyway. Manual start always works.\n" +
                "StreamEvenIfGoveeDesktop=" + (StreamEvenIfGoveeDesktop ? "true" : "false") + "\n" +
                "# KillGoveeDesktop: true kills GoveeDesktop.exe on every start instead of stepping aside.\n" +
                "KillGoveeDesktop=" + (KillGoveeDesktop ? "true" : "false") + "\n" +
                "# Ccm: 3x3 colour-correction matrix (row-major), set by tray -> Calibrate colours.\n" +
                "Ccm=" + Join(Ccm) + "\n" +
                "# CalibPort: HTTPS port for the phone calibration page.\n" +
                "CalibPort=" + CalibPort + "\n";
            File.WriteAllText(FilePath, text);
        }

        private static string Get(Dictionary<string, string> m, string k, string d) =>
            m.TryGetValue(k, out var v) && v.Length > 0 ? v : d;

        private static int GetInt(Dictionary<string, string> m, string k, int d) =>
            m.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : d;

        private static double GetDouble(Dictionary<string, string> m, string k, double d) =>
            m.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : d;

        private static bool GetBool(Dictionary<string, string> m, string k, bool d) =>
            m.TryGetValue(k, out var v) ? (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) : d;

        private static string Join(double[] a)
        {
            var parts = new string[a.Length];
            for (int i = 0; i < a.Length; i++) parts[i] = a[i].ToString(CultureInfo.InvariantCulture);
            return string.Join(",", parts);
        }

        private static double[] GetDoubles(Dictionary<string, string> m, string k, double[] d, int n)
        {
            if (!m.TryGetValue(k, out var v)) return d;
            var parts = v.Split(',');
            if (parts.Length != n) return d;
            var r = new double[n];
            for (int i = 0; i < n; i++)
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out r[i])) return d;
            return r;
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        private static double ClampD(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }
}
