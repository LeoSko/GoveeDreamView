using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace GoveeDreamView
{
    // Fullscreen solid-colour window shown on the target monitor during calibration, so the
    // camera sees a known reference colour on-screen at the same time as the lit wall.
    internal sealed class SwatchForm : Form
    {
        public volatile bool CaptureRequested;

        public SwatchForm(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            KeyPreview = true;
            MouseDown += (s, e) => CaptureRequested = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) CaptureRequested = true; };
        }

        public void SetColor(Color c) { BackColor = c; Refresh(); }
        public void ClearCapture() { CaptureRequested = false; }
    }

    // Result of a calibration run: the matrix plus a human-readable report.
    internal sealed class CalibResult
    {
        public double[] Ccm;
        public string Report;
    }

    // A captured camera frame, scaled to RGB bytes for analysis.
    internal sealed class Frame
    {
        public int W, H;
        public byte[] Rgb;   // R,G,B per pixel, row-major
        public int Bright(int i) { int o = i * 3; return Rgb[o] + Rgb[o + 1] + Rgb[o + 2]; }

        // Decodes the JPEG scaled so its long edge is `maxDim`, preserving aspect (the phone camera
        // may be portrait or landscape - forcing a fixed w x h would distort the image and the mask).
        public static Frame FromJpeg(byte[] jpeg, int maxDim)
        {
            using (var ms = new MemoryStream(jpeg))
            using (var src = new Bitmap(ms))
            {
                double scale = (double)maxDim / Math.Max(src.Width, src.Height);
                if (scale > 1) scale = 1;
                int w = Math.Max(1, (int)Math.Round(src.Width * scale));
                int h = Math.Max(1, (int)Math.Round(src.Height * scale));
                using (var bmp = new Bitmap(src, new Size(w, h)))
                    return FromBitmap(bmp);
            }
        }

        private static Frame FromBitmap(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var f = new Frame { W = w, H = h, Rgb = new byte[w * h * 3] };
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var row = new byte[data.Stride];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, data.Stride);
                for (int x = 0; x < w; x++)
                {
                    int o = x * 4, d = (y * w + x) * 3;
                    f.Rgb[d] = row[o + 2]; f.Rgb[d + 1] = row[o + 1]; f.Rgb[d + 2] = row[o];
                }
            }
            bmp.UnlockBits(data);
            return f;
        }
    }

    // Self-calibration:
    //   1. Auto-locates the monitor and the lit wall in the camera frame by differential
    //      lighting (screen-only vs strip-only vs black), so orientation/position don't matter.
    //   2. Sweeps a full-spectrum set of swatches, measuring screen-vs-wall in each frame
    //      (which cancels the camera's auto-exposure / white-balance).
    //   3. Least-squares fits the strip and screen response matrices and derives a 3x3 CCM
    //      so the wall reproduces the on-screen colour.
    internal sealed class Calibrator
    {
        private const byte NONE = 0, SCREEN = 1, WALL = 2;

        private readonly GoveeClient _client;
        private readonly Action<string> _log;
        private readonly Func<Frame> _latestFrame;   // newest frame the phone posted
        private readonly Action _lockCamera;         // tells the phone to freeze exposure + white balance
        private readonly Action<byte[]> _publishMask; // pushes the region-overlay PNG to the phone page

        public Calibrator(GoveeClient client, Action<string> log, Func<Frame> latestFrame, Action lockCamera, Action<byte[]> publishMask)
        {
            _client = client;
            _log = log;
            _latestFrame = latestFrame;
            _lockCamera = lockCamera;
            _publishMask = publishMask;
        }

        private static readonly byte[][] Spectrum =
        {
            new byte[]{255,0,0},   new byte[]{0,255,0},   new byte[]{0,0,255},
            new byte[]{255,255,0}, new byte[]{0,255,255},  new byte[]{255,0,255},
            new byte[]{255,128,0}, new byte[]{128,255,0},  new byte[]{0,255,128},
            new byte[]{0,128,255}, new byte[]{128,0,255},  new byte[]{255,0,128},
            new byte[]{128,128,128}, new byte[]{200,200,200}, new byte[]{255,255,255},
        };

        public CalibResult Run(int monitorIndex, int segments)
        {
            var bounds = MonitorBounds(monitorIndex);
            using (var form = new SwatchForm(bounds))
            {
                form.Show();
                form.Activate();
                form.Focus();
                Application.DoEvents();
                _publishMask?.Invoke(null);   // clear any stale overlay from a previous run

                // --- 0. one-time setup: aim + lock exposure on the brightest scene, then ONE click ---
                _log("SETUP: screen + strip are WHITE. Aim the phone at screen + wall, let auto-exposure settle, then CLICK the PC screen ONCE. Calibration then runs on its own - no more clicks.");
                ShowGrab(form, White, White, segments); // the single user click; frame discarded

                // Freeze the phone's exposure + white balance NOW (on the bright scene, before anything
                // darker), so the camera response stays constant for the whole closed-loop fit.
                _log("Locking camera exposure + white balance...");
                _lockCamera?.Invoke();
                PumpWait(900);

                // --- 1. locate screen vs wall (auto, settle-gated) ---
                _log("Locating screen & wall...");
                var fBlack = GrabSettled(form, Black, Black, segments);
                var fScreen = GrabSettled(form, White, Black, segments);  // monitor lit, strip off
                var fWall = GrabSettled(form, Black, White, segments);    // monitor off, strip lit
                var mask = DetectMask(fBlack, fScreen, fWall, out int nScr, out int nWall);
                _log("Located screen=" + nScr + "px, wall=" + nWall + "px");
                _publishMask?.Invoke(MaskPng(mask, fBlack.W, fBlack.H));   // show regions on the phone
                if (nScr < 50 || nWall < 50)
                    throw new Exception("could not locate screen and wall (screen=" + nScr + ", wall=" + nWall + ") - re-aim and recalibrate");
                SampleSettled(form, Black, Black, segments, mask, out var scrBlack, out var wallBlack);

                // --- 2. warm-start: identity sweep -> first CCM estimate ---
                var t0 = new List<double[]>(); var w0 = new List<double[]>(); var s0 = new List<double[]>();
                for (int i = 0; i < Spectrum.Length; i++)
                {
                    var c = Spectrum[i];
                    _log("Probing colour " + (i + 1) + "/" + Spectrum.Length + "...");
                    var f = GrabSettled(form, c, c, segments);
                    t0.Add(new double[] { c[0], c[1], c[2] });
                    s0.Add(Sub(AvgMask(f, mask, SCREEN), scrBlack));
                    w0.Add(Sub(AvgMask(f, mask, WALL), wallBlack));
                }
                var Sresp = LeastSquares(t0, s0);                          // screen_seen = S * target (fixed)
                var M = MatMul(Invert(LeastSquares(t0, w0)), Sresp);      // warm CCM = G^-1 * S
                NormalizeWhite(M);

                // --- 3. closed-loop refinement: trust region. Accept a step only if it lowers the
                //        error; on rejection halve the step and retry from the same point. Always
                //        keep the best matrix seen, so the result can never be worse than the start. ---
                const int MAXROUND = 18;
                const double TARGET = 3.0, MINDAMP = 0.06;   // TARGET is hue error in degrees
                double damp = 0.7;

                var meas = MeasureSweep(form, M, mask, scrBlack, wallBlack, segments);
                double bestErr = meas.Err;
                var bestM = Clone(M);
                _log("Start hue error " + Deg(bestErr) + " - refining until it converges...");

                int accepted = 0, round;
                for (round = 1; round <= MAXROUND && bestErr > TARGET; round++)
                {
                    var cand = StepMatrix(meas, damp);
                    var measC = MeasureSweep(form, cand, mask, scrBlack, wallBlack, segments);
                    bool better = measC.Err < bestErr - 0.1;   // 0.1 deg = meaningful improvement
                    _log("Round " + round + "/" + MAXROUND + ": hue error " + Deg(measC.Err) + (better ? "" : "  (rejected, step/2)"));
                    if (better)
                    {
                        meas = measC; bestErr = measC.Err; bestM = Clone(cand);
                        damp = Math.Min(0.9, damp * 1.2);
                        accepted++;
                    }
                    else
                    {
                        damp *= 0.5;
                        if (damp < MINDAMP) break;   // step too small to matter -> at the minimum
                    }
                }

                NormalizeWhite(bestM);
                var flat = Flatten(bestM);
                string report =
                    "Calibration done after " + accepted + " accepted step(s).\n" +
                    "regions: screen=" + nScr + "px  wall=" + nWall + "px\n" +
                    "hue error: " + Deg(bestErr) + (bestErr <= TARGET ? "  (converged)" : "  (best reached)") + "\n" +
                    "wall reaches " + Pct(BrightnessRatio(meas)) + " of screen brightness (abs error " + Pct(MeanRelErr(meas.S, meas.W)) + ")\n" +
                    "matrix (drive = M x target):\n" + MatStr(flat);
                _log(report);
                return new CalibResult { Ccm = flat, Report = report };
            }
        }

        // One full spectrum sweep under matrix M: drives the strip with clamp(M*target) for each
        // swatch and records target, drive, screen-seen and wall-seen, plus the overall error.
        private sealed class Sweep
        {
            public List<double[]> Ts = new List<double[]>();    // target colours
            public List<double[]> Sent = new List<double[]>();  // drive colours actually sent (clamped)
            public List<double[]> S = new List<double[]>();      // screen seen (minus black)
            public List<double[]> W = new List<double[]>();      // wall seen (minus black)
            public double Err;
        }

        private Sweep MeasureSweep(SwatchForm form, double[][] M, byte[] mask, double[] scrBlack, double[] wallBlack, int segments)
        {
            var sw = new Sweep();
            for (int i = 0; i < Spectrum.Length; i++)
            {
                var c = Spectrum[i];
                var tgt = new double[] { c[0], c[1], c[2] };
                var snt = ClampVec(MatVec(M, tgt));
                SampleSettled(form, c, ToBytes(snt), segments, mask, out var sAvg, out var wAvg);
                sw.Ts.Add(tgt); sw.Sent.Add(snt);
                sw.S.Add(Sub(sAvg, scrBlack));
                sw.W.Add(Sub(wAvg, wallBlack));
            }
            sw.Err = MeanChromaErr(sw.S, sw.W);
            return sw;
        }

        // Gauss-Newton step: d(wall) ~= G * d(sent), so move each drive by damp * G^-1 * (screen - wall),
        // then refit the matrix to the corrected drives. No white-normalise here - that would perturb
        // the trust-region search; the caller normalises only the final accepted matrix.
        private static double[][] StepMatrix(Sweep m, double damp)
        {
            var Ginv = Invert(LeastSquares(m.Sent, m.W));
            var newSent = new List<double[]>();
            for (int i = 0; i < m.Ts.Count; i++)
            {
                // Chroma residual: aim the wall at the screen's *hue* scaled to the wall's own
                // brightness, so we correct colour without chasing brightness the wall can't reach.
                double ns = Norm(m.S[i]), nw = Norm(m.W[i]);
                double sc = ns > 1e-6 ? nw / ns : 1.0;
                var resid = new[] { m.S[i][0] * sc - m.W[i][0], m.S[i][1] * sc - m.W[i][1], m.S[i][2] * sc - m.W[i][2] };
                var step = MatVec(Ginv, resid);
                newSent.Add(ClampVec(new[] { m.Sent[i][0] + damp * step[0], m.Sent[i][1] + damp * step[1], m.Sent[i][2] + damp * step[2] }));
            }
            return LeastSquares(m.Ts, newSent);
        }

        private static double[][] Clone(double[][] m) =>
            new[] { (double[])m[0].Clone(), (double[])m[1].Clone(), (double[])m[2].Clone() };

        // Settles on a colour, then averages the masked screen/wall values over several *distinct*
        // camera frames to beat down sensor + JPEG + flicker noise (single frames jitter ~3%).
        private const int AvgFrames = 7;
        private void SampleSettled(SwatchForm form, byte[] screenColor, byte[] driveColor, int segments,
                                   byte[] mask, out double[] sAvg, out double[] wAvg)
        {
            var f = GrabSettled(form, screenColor, driveColor, segments);
            var flat = new byte[segments * 3];
            for (int s = 0; s < segments; s++) { flat[s * 3] = driveColor[0]; flat[s * 3 + 1] = driveColor[1]; flat[s * 3 + 2] = driveColor[2]; }

            var sAcc = AvgMask(f, mask, SCREEN);
            var wAcc = AvgMask(f, mask, WALL);
            double[] lastS = sAcc, lastW = wAcc;
            int k = 1;
            int until = Environment.TickCount + 1300;
            while (k < AvgFrames && Environment.TickCount < until)
            {
                _client.SendColors(flat, segments);
                Application.DoEvents();
                Thread.Sleep(70);
                var g = _latestFrame();
                if (g == null) continue;
                var s = AvgMask(g, mask, SCREEN);
                var w = AvgMask(g, mask, WALL);
                // LatestFrame re-decodes the same JPEG each call; identical values => same frame, no new info.
                if (s[0] == lastS[0] && s[1] == lastS[1] && s[2] == lastS[2] &&
                    w[0] == lastW[0] && w[1] == lastW[1] && w[2] == lastW[2]) continue;
                lastS = s; lastW = w;
                for (int j = 0; j < 3; j++) { sAcc[j] += s[j]; wAcc[j] += w[j]; }
                k++;
            }
            for (int j = 0; j < 3; j++) { sAcc[j] /= k; wAcc[j] /= k; }
            sAvg = sAcc; wAvg = wAcc;
        }

        // Drives the strip + screen to fixed colours, then waits (no click) until the camera frame
        // stops changing (strip settled + camera auto-exposure adapted), and returns that frame.
        private Frame GrabSettled(SwatchForm form, byte[] screenColor, byte[] driveColor, int segments)
        {
            form.SetColor(Color.FromArgb(screenColor[0], screenColor[1], screenColor[2]));
            Application.DoEvents();
            var flat = new byte[segments * 3];
            for (int s = 0; s < segments; s++) { flat[s * 3] = driveColor[0]; flat[s * 3 + 1] = driveColor[1]; flat[s * 3 + 2] = driveColor[2]; }

            const int maxMs = 2600, window = 450;
            const double eps = 1.6;   // avg per-channel frame delta below which we call it settled
            double[] prev = null;
            int start = Environment.TickCount, stableStart = -1;
            Frame last = null;
            while (Environment.TickCount - start < maxMs)
            {
                _client.SendColors(flat, segments);
                Application.DoEvents();
                Thread.Sleep(70);
                var f = _latestFrame();
                if (f == null) continue;
                last = f;
                var a = AvgFrame(f);
                if (prev != null)
                {
                    double d = Math.Abs(a[0] - prev[0]) + Math.Abs(a[1] - prev[1]) + Math.Abs(a[2] - prev[2]);
                    if (d < eps)
                    {
                        if (stableStart < 0) stableStart = Environment.TickCount;
                        else if (Environment.TickCount - stableStart >= window) return f;
                    }
                    else stableStart = -1;
                }
                prev = a;
            }
            if (last == null) throw new Exception("no camera frame received during calibration");
            return last;
        }

        private static void PumpWait(int ms)
        {
            int deadline = Environment.TickCount + ms;
            while (Environment.TickCount < deadline) { Application.DoEvents(); Thread.Sleep(30); }
        }

        private static double[] AvgFrame(Frame f)
        {
            long r = 0, g = 0, b = 0; int n = f.W * f.H;
            for (int i = 0; i < n; i++) { int o = i * 3; r += f.Rgb[o]; g += f.Rgb[o + 1]; b += f.Rgb[o + 2]; }
            return new[] { (double)r / n, (double)g / n, (double)b / n };
        }

        // Mean ||a-b|| / ||a|| over all samples: relative colour error between two measured sets.
        private static double MeanRelErr(List<double[]> a, List<double[]> b)
        {
            double num = 0, den = 0;
            for (int k = 0; k < a.Count; k++)
            {
                double dx = a[k][0] - b[k][0], dy = a[k][1] - b[k][1], dz = a[k][2] - b[k][2];
                num += Math.Sqrt(dx * dx + dy * dy + dz * dz);
                den += Math.Sqrt(a[k][0] * a[k][0] + a[k][1] * a[k][1] + a[k][2] * a[k][2]);
            }
            return den > 1e-6 ? num / den : 0;
        }

        private static double Norm(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

        // Mean angle (degrees) between matched colours treated as directions: pure hue/saturation
        // error, independent of brightness (the wall is dimmer than the screen, which a CCM can't fix).
        private static double MeanChromaErr(List<double[]> a, List<double[]> b)
        {
            double sum = 0; int n = 0;
            for (int k = 0; k < a.Count; k++)
            {
                double na = Norm(a[k]), nb = Norm(b[k]);
                if (na < 1e-6 || nb < 1e-6) continue;
                double dot = (a[k][0] * b[k][0] + a[k][1] * b[k][1] + a[k][2] * b[k][2]) / (na * nb);
                dot = dot > 1 ? 1 : (dot < -1 ? -1 : dot);
                sum += Math.Acos(dot) * 180.0 / Math.PI;
                n++;
            }
            return n > 0 ? sum / n : 0;
        }

        // Mean of |wall| / |screen|: how much of the screen's brightness the wall actually reaches.
        private static double BrightnessRatio(Sweep m)
        {
            double sum = 0; int n = 0;
            for (int k = 0; k < m.S.Count; k++)
            {
                double ns = Norm(m.S[k]);
                if (ns < 1e-6) continue;
                sum += Norm(m.W[k]) / ns; n++;
            }
            return n > 0 ? sum / n : 0;
        }

        private static string Deg(double x) =>
            x.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "°";

        private static double[] MatVec(double[][] m, double[] v) => new[]
        {
            m[0][0] * v[0] + m[0][1] * v[1] + m[0][2] * v[2],
            m[1][0] * v[0] + m[1][1] * v[1] + m[1][2] * v[2],
            m[2][0] * v[0] + m[2][1] * v[1] + m[2][2] * v[2],
        };

        private static double C255(double x) => x < 0 ? 0 : (x > 255 ? 255 : x);
        private static double[] ClampVec(double[] v) => new[] { C255(v[0]), C255(v[1]), C255(v[2]) };
        private static byte[] ToBytes(double[] v) =>
            new[] { (byte)Math.Round(C255(v[0])), (byte)Math.Round(C255(v[1])), (byte)Math.Round(C255(v[2])) };

        private static string Pct(double x) =>
            (x * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";

        private static string MatStr(double[] m)
        {
            string F(double x) => x.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture).PadLeft(6);
            return "[" + F(m[0]) + F(m[1]) + F(m[2]) + " ]\n" +
                   "[" + F(m[3]) + F(m[4]) + F(m[5]) + " ]\n" +
                   "[" + F(m[6]) + F(m[7]) + F(m[8]) + " ]";
        }

        private Frame ShowGrab(SwatchForm form, byte[] screenColor, byte[] driveColor, int segments)
        {
            form.SetColor(Color.FromArgb(screenColor[0], screenColor[1], screenColor[2]));
            Application.DoEvents();
            var flat = new byte[segments * 3];
            for (int s = 0; s < segments; s++) { flat[s * 3] = driveColor[0]; flat[s * 3 + 1] = driveColor[1]; flat[s * 3 + 2] = driveColor[2]; }

            // Keep the strip lit and the UI pumping until the user clicks the PC screen / presses SPACE.
            form.ClearCapture();
            int deadline = Environment.TickCount + 180000;
            while (Environment.TickCount < deadline)
            {
                _client.SendColors(flat, segments);
                Application.DoEvents();
                if (form.CaptureRequested)
                {
                    var f = _latestFrame();
                    if (f != null) { form.ClearCapture(); return f; }
                }
                Thread.Sleep(40);
            }
            throw new Exception("timed out waiting for a Capture click on the PC screen");
        }

        // Classify every pixel as SCREEN, WALL or NONE by which source it brightened under.
        private static byte[] DetectMask(Frame blk, Frame scr, Frame wal, out int nScr, out int nWall)
        {
            int n = blk.W * blk.H;
            var ds = new int[n];
            var dw = new int[n];
            int peak = 1;
            for (int i = 0; i < n; i++)
            {
                int b = blk.Bright(i);
                ds[i] = Math.Max(0, scr.Bright(i) - b);
                dw[i] = Math.Max(0, wal.Bright(i) - b);
                if (ds[i] > peak) peak = ds[i];
                if (dw[i] > peak) peak = dw[i];
            }
            int thr = (int)(peak * 0.30); // clear cutoff: must brighten >=30% of the strongest response
            var mask = new byte[n];
            for (int i = 0; i < n; i++)
            {
                if (Math.Max(ds[i], dw[i]) < thr) mask[i] = NONE;
                else if (ds[i] >= dw[i]) mask[i] = SCREEN;
                else mask[i] = WALL;
            }

            // "Wall" so far is just everything the strip lit - which includes a table or objects in
            // the frame. Refine each region to its uniform surface: keep only pixels whose colour in
            // the lit reference is near that region's median, dropping furniture (different material =
            // different colour under the same light) and rainbow moire outliers on the screen.
            RefineUniform(mask, scr, SCREEN, 0.30);
            RefineUniform(mask, wal, WALL, 0.30);

            nScr = 0; nWall = 0;
            for (int i = 0; i < n; i++) { if (mask[i] == SCREEN) nScr++; else if (mask[i] == WALL) nWall++; }
            return mask;
        }

        // Keeps pixels of `which` whose colour in `f` is within `tol` (relative to the region's median
        // magnitude) of the region median; the rest become NONE.
        private static void RefineUniform(byte[] mask, Frame f, byte which, double tol)
        {
            var rs = new List<byte>(); var gs = new List<byte>(); var bs = new List<byte>();
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != which) continue;
                int o = i * 3; rs.Add(f.Rgb[o]); gs.Add(f.Rgb[o + 1]); bs.Add(f.Rgb[o + 2]);
            }
            if (rs.Count < 50) return;   // too few to estimate a reliable median
            double mr = Median(rs), mg = Median(gs), mb = Median(bs);
            double lim = tol * Math.Sqrt(mr * mr + mg * mg + mb * mb);
            if (lim < 1e-6) return;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != which) continue;
                int o = i * 3;
                double dr = f.Rgb[o] - mr, dg = f.Rgb[o + 1] - mg, db = f.Rgb[o + 2] - mb;
                if (Math.Sqrt(dr * dr + dg * dg + db * db) > lim) mask[i] = NONE;
            }
        }

        private static double Median(List<byte> v) { v.Sort(); return v[v.Count / 2]; }

        // Renders the region mask as a transparent overlay PNG: just the region OUTLINES (screen=blue,
        // wall=green) plus a centred text label per region, so it marks the regions without hiding them.
        private static byte[] MaskPng(byte[] mask, int w, int h)
        {
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                var row = new byte[data.Stride];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int o = x * 4;
                        byte cls = mask[y * w + x];
                        byte a = 0, r = 0, g = 0, b = 0;
                        if (cls != NONE && IsEdge(mask, w, h, x, y))
                        {
                            a = 255;
                            if (cls == SCREEN) { r = 60; g = 140; b = 255; } else { r = 50; g = 230; b = 90; }
                        }
                        row[o] = b; row[o + 1] = g; row[o + 2] = r; row[o + 3] = a;   // BGRA byte order
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), data.Stride);
                }
                bmp.UnlockBits(data);

                using (var gfx = Graphics.FromImage(bmp))
                {
                    gfx.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var font = new Font(FontFamily.GenericSansSerif, Math.Max(10f, h * 0.05f), FontStyle.Bold, GraphicsUnit.Pixel))
                    {
                        DrawLabel(gfx, font, mask, w, h, SCREEN, "SCREEN", Color.FromArgb(120, 180, 255));
                        DrawLabel(gfx, font, mask, w, h, WALL, "WALL", Color.FromArgb(90, 240, 120));
                    }
                }
                using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
            }
        }

        // A region pixel is an outline pixel if it touches the image edge or a pixel of a different class.
        private static bool IsEdge(byte[] m, int w, int h, int x, int y)
        {
            if (x == 0 || y == 0 || x == w - 1 || y == h - 1) return true;
            byte c = m[y * w + x];
            return m[y * w + x - 1] != c || m[y * w + x + 1] != c || m[(y - 1) * w + x] != c || m[(y + 1) * w + x] != c;
        }

        private static void DrawLabel(Graphics gfx, Font font, byte[] mask, int w, int h, byte which, string text, Color col)
        {
            long sx = 0, sy = 0, n = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (mask[y * w + x] == which) { sx += x; sy += y; n++; }
            if (n < 50) return;
            var size = gfx.MeasureString(text, font);
            float tx = (float)sx / n - size.Width / 2, ty = (float)sy / n - size.Height / 2;
            using (var halo = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                for (int dx = -2; dx <= 2; dx += 2)
                    for (int dy = -2; dy <= 2; dy += 2)
                        gfx.DrawString(text, font, halo, tx + dx, ty + dy);   // dark halo for legibility
            using (var br = new SolidBrush(col))
                gfx.DrawString(text, font, br, tx, ty);
        }

        private static double[] AvgMask(Frame f, byte[] mask, byte which)
        {
            long r = 0, g = 0, b = 0; int count = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != which) continue;
                int o = i * 3;
                r += f.Rgb[o]; g += f.Rgb[o + 1]; b += f.Rgb[o + 2]; count++;
            }
            if (count == 0) count = 1;
            return new double[] { (double)r / count, (double)g / count, (double)b / count };
        }

        // ---- linear algebra ----

        // Least-squares 3x3 M with measured ≈ M * sent, over all samples.
        private static double[][] LeastSquares(List<double[]> sent, List<double[]> meas)
        {
            var a = Zero3(); // Σ meas ⊗ sent
            var b = Zero3(); // Σ sent ⊗ sent
            for (int k = 0; k < sent.Count; k++)
            {
                var x = sent[k]; var y = meas[k];
                for (int r = 0; r < 3; r++)
                    for (int c = 0; c < 3; c++)
                    {
                        a[r][c] += y[r] * x[c];
                        b[r][c] += x[r] * x[c];
                    }
            }
            return MatMul(a, Invert(b));
        }

        private static double[] Sub(double[] a, double[] b) =>
            new[] { Math.Max(0, a[0] - b[0]), Math.Max(0, a[1] - b[1]), Math.Max(0, a[2] - b[2]) };

        private static double[][] Zero3() => new[] { new double[3], new double[3], new double[3] };

        private static double[][] MatMul(double[][] a, double[][] b)
        {
            var m = Zero3();
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    m[r][c] = a[r][0] * b[0][c] + a[r][1] * b[1][c] + a[r][2] * b[2][c];
            return m;
        }

        private static double[][] Invert(double[][] m)
        {
            double a = m[0][0], b = m[0][1], c = m[0][2];
            double d = m[1][0], e = m[1][1], f = m[1][2];
            double g = m[2][0], h = m[2][1], i = m[2][2];
            double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
            if (Math.Abs(det) < 1e-9) throw new Exception("calibration singular (camera saw no colour separation - check exposure/clipping)");
            double inv = 1.0 / det;
            return new[]
            {
                new[] { (e * i - f * h) * inv, (c * h - b * i) * inv, (b * f - c * e) * inv },
                new[] { (f * g - d * i) * inv, (a * i - c * g) * inv, (c * d - a * f) * inv },
                new[] { (d * h - e * g) * inv, (b * g - a * h) * inv, (a * e - b * d) * inv },
            };
        }

        private static void NormalizeWhite(double[][] m)
        {
            double mx = 0;
            for (int r = 0; r < 3; r++)
            {
                double sum = m[r][0] + m[r][1] + m[r][2];
                if (sum > mx) mx = sum;
            }
            if (mx <= 1e-6) return;
            double k = 1.0 / mx;
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) m[r][c] *= k;
        }

        private static double[] Flatten(double[][] m) =>
            new[] { m[0][0], m[0][1], m[0][2], m[1][0], m[1][1], m[1][2], m[2][0], m[2][1], m[2][2] };

        private static readonly byte[] Black = { 0, 0, 0 };
        private static readonly byte[] White = { 255, 255, 255 };

        private static Rectangle MonitorBounds(int index)
        {
            var screens = Screen.AllScreens;
            if (index < 0 || index >= screens.Length) index = 0;
            return screens[index].Bounds;
        }
    }
}
