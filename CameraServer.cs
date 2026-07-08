using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace GoveeDreamView
{
    // Tiny HTTPS server (TcpListener + SslStream, so no netsh/urlacl/admin needed). Serves a page
    // that opens the phone camera in-browser (getUserMedia needs a secure context -> self-signed
    // TLS) and POSTs JPEG frames back. The calibrator reads the latest frame on demand.
    internal sealed class CameraServer : IDisposable
    {
        // Self-signed cert (CN=GoveeDreamView), password "govee". Browser shows a one-time warning.
        private const string PfxB64 =
            "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";

        private const int CapMax = 480;   // frames decoded with long edge = CapMax, aspect preserved
        private const int MaxBody = 4 * 1024 * 1024;

        private readonly X509Certificate2 _cert;
        private TcpListener _listener;
        private volatile bool _run;
        private volatile byte[] _frame;
        private long _seq;
        private volatile byte[] _maskPng;   // PC-computed region overlay (PNG), shown on the phone
        private volatile bool _lock;   // true => tell the phone to freeze exposure + white balance
        private volatile string _status = "Waiting for phone camera...";

        public CameraServer()
        {
            _cert = new X509Certificate2(Convert.FromBase64String(PfxB64), "govee", X509KeyStorageFlags.Exportable);
        }

        public int Start(int port)
        {
            _run = true;
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            var t = new Thread(AcceptLoop) { IsBackground = true, Name = "cam-http" };
            t.Start();
            return port;
        }

        public void SetStatus(string s) { _status = s; }

        // Signals the phone page (via /ctl) to lock exposure + white balance at their current values.
        public void Lock() { _lock = true; }
        public void SetMask(byte[] png) { _maskPng = png; }

        // Blocks until the phone has posted at least one frame.
        public bool WaitForPhone(int timeoutMs, Action tick)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (Interlocked.Read(ref _seq) > 0) return true;
                tick?.Invoke();
                Thread.Sleep(50);
            }
            return false;
        }

        // Decodes the most recent frame the phone posted (null if none yet).
        public Frame LatestFrame()
        {
            var data = _frame;
            return data == null ? null : Frame.FromJpeg(data, CapMax);
        }

        private void AcceptLoop()
        {
            while (_run)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { break; }
                var t = new Thread(() => Handle(c)) { IsBackground = true };
                t.Start();
            }
        }

        private void Handle(TcpClient tc)
        {
            try
            {
                using (tc)
                using (var ssl = new SslStream(tc.GetStream(), false))
                {
                    ssl.AuthenticateAsServer(_cert, false, SslProtocols.Tls12, false);
                    while (_run)
                    {
                        if (!ReadRequest(ssl, out string method, out string path, out byte[] body)) break;
                        Route(ssl, method, path, body);
                    }
                }
            }
            catch { /* per-connection errors (browser closes, TLS aborts) are expected */ }
        }

        private void Route(SslStream ssl, string method, string path, byte[] body)
        {
            if (method == "POST" && path == "/frame")
            {
                if (body != null && body.Length > 0) { _frame = body; Interlocked.Increment(ref _seq); }
                Send(ssl, 200, "OK", "text/plain", Encoding.ASCII.GetBytes("ok"));
            }
            else if (path == "/status")
            {
                Send(ssl, 200, "OK", "text/plain", Encoding.UTF8.GetBytes(_status));
            }
            else if (path == "/ctl")
            {
                Send(ssl, 200, "OK", "text/plain", Encoding.ASCII.GetBytes(_lock ? "lock" : "free"));
            }
            else if (path == "/frame.jpg")
            {
                var data = _frame;   // latest JPEG the phone posted, for live diagnostics
                if (data == null) Send(ssl, 404, "Not Found", "text/plain", Encoding.ASCII.GetBytes("no frame"));
                else Send(ssl, 200, "OK", "image/jpeg", data);
            }
            else if (path.StartsWith("/mask.png"))   // query string carries a cache-buster
            {
                var data = _maskPng;
                if (data == null) Send(ssl, 404, "Not Found", "text/plain", Encoding.ASCII.GetBytes("no mask"));
                else Send(ssl, 200, "OK", "image/png", data);
            }
            else if (path == "/" || path == "/index.html")
            {
                Send(ssl, 200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Page));
            }
            else
            {
                Send(ssl, 404, "Not Found", "text/plain", Encoding.ASCII.GetBytes("not found"));
            }
        }

        private static bool ReadRequest(SslStream ssl, out string method, out string path, out byte[] body)
        {
            method = path = null; body = null;
            var buf = new byte[8192];
            var acc = new List<byte>();
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = ssl.Read(buf, 0, buf.Length);
                if (n <= 0) return false;
                for (int i = 0; i < n; i++) acc.Add(buf[i]);
                headerEnd = FindHeaderEnd(acc);
                if (acc.Count > 64 * 1024 && headerEnd < 0) return false;
            }

            string header = Encoding.ASCII.GetString(acc.ToArray(), 0, headerEnd);
            var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var req = lines[0].Split(' ');
            if (req.Length < 2) return false;
            method = req[0]; path = req[1];

            int contentLength = 0;
            foreach (var ln in lines)
            {
                int colon = ln.IndexOf(':');
                if (colon > 0 && ln.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(ln.Substring(colon + 1).Trim(), out contentLength);
            }
            if (contentLength < 0 || contentLength > MaxBody) return false;

            int bodyStart = headerEnd + 4;
            var bodyBytes = new List<byte>();
            for (int i = bodyStart; i < acc.Count; i++) bodyBytes.Add(acc[i]);
            while (bodyBytes.Count < contentLength)
            {
                int n = ssl.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                for (int i = 0; i < n; i++) bodyBytes.Add(buf[i]);
            }
            body = bodyBytes.ToArray();
            return true;
        }

        private static int FindHeaderEnd(List<byte> b)
        {
            for (int i = 3; i < b.Count; i++)
                if (b[i - 3] == 13 && b[i - 2] == 10 && b[i - 1] == 13 && b[i] == 10) return i - 3;
            return -1;
        }

        private static void Send(SslStream ssl, int code, string status, string ctype, byte[] body)
        {
            string head = "HTTP/1.1 " + code + " " + status + "\r\n"
                + "Content-Type: " + ctype + "\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + "Cache-Control: no-store\r\n"
                + "Connection: keep-alive\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            ssl.Write(hb, 0, hb.Length);
            if (body.Length > 0) ssl.Write(body, 0, body.Length);
            ssl.Flush();
        }

        public static string LocalIp()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 1);   // no packet sent; just resolves the outbound interface
                    return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
                }
            }
            catch { return "127.0.0.1"; }
        }

        public void Dispose()
        {
            _run = false;
            try { _listener?.Stop(); } catch { }
            try { _cert?.Dispose(); } catch { }
        }

        private const string Page =
@"<!doctype html><html><head><meta charset=utf-8>
<meta name=viewport content='width=device-width,initial-scale=1'>
<title>Govee Calibration</title>
<style>body{margin:0;background:#111;color:#eee;font-family:system-ui,sans-serif;text-align:center}
#v{width:100%;max-height:52vh;background:#000;object-fit:contain}
#s{padding:12px;font-size:15px;line-height:1.4;white-space:pre-wrap;text-align:left;font-family:ui-monospace,Menlo,Consolas,monospace}
.ctl{font-size:13px;color:#9ad;padding:3px}
.sl{width:88%;height:34px}
label{color:#cde;font-size:14px}
#wrap{position:relative}
#o{position:absolute;left:0;top:0;width:100%;height:100%;object-fit:contain;pointer-events:none;opacity:.9;display:none}</style></head>
<body>
<div id=s>Starting camera...</div>
<div id=wrap><video id=v autoplay playsinline muted></video><canvas id=o width=480 height=270></canvas></div>
<div class=ctl id=leg style=display:none><b style=color:#48f>&#9633; SCREEN</b> &nbsp; <b style=color:#3d6>&#9633; WALL</b></div>
<div class=ctl><label><input id=ae type=checkbox checked> auto exposure</label> <span id=expL></span></div>
<input id=ex class=sl type=range min=0 max=100 value=35 style=display:none>
<div class=ctl><label><input id=aw type=checkbox checked> auto white balance</label> <span id=wbL></span></div>
<input id=wb class=sl type=range min=0 max=100 value=50 style=display:none>
<canvas id=c style=display:none></canvas>
<script>
function q(i){return document.getElementById(i);}
var v=q('v'),c=q('c'),s=q('s'),ex=q('ex'),wb=q('wb'),ae=q('ae'),aw=q('aw'),expL=q('expL'),wbL=q('wbL');
var track,expCap=null,expKey=null,wbCap=null,expVal=0,lastExpVal=0,lastExpApply=0,locked=false;
var expState={},wbState={};
function fmt(x){return Math.round(x*1000)/1000;}
function clampExp(x){var lo=Math.max(expCap.min,1e-4),hi=expCap.max;return Math.min(hi,Math.max(lo,x));}
function wbActual(){var st=track.getSettings?track.getSettings():{};return (st.whiteBalanceMode||'?')+' '+(st.colorTemperature||'?')+'K';}
// Apply exposure + white-balance together in ONE constraint set, so a periodic exposure
// update never clobbers the white-balance setting (and vice versa).
function applyAll(cb){
 if(!track)return;
 var o={},k;
 for(k in expState)o[k]=expState[k];
 for(k in wbState)o[k]=wbState[k];
 if(!Object.keys(o).length)return;
 track.applyConstraints({advanced:[o]}).then(function(){if(cb)cb();}).catch(function(e){if(cb)cb(e);});
}
// Freeze exposure + white balance at their current (auto-converged) values for the whole fit.
function freeze(){
 if(locked||!track)return;locked=true;
 var st=track.getSettings?track.getSettings():{};
 if(expCap&&expKey=='exposureTime'){expVal=st.exposureTime||expVal;expState={exposureMode:'manual'};expState[expKey]=expVal;}
 else{expState={exposureMode:'manual'};}
 if(wbCap){var k=st.colorTemperature||Math.round(wbCap.min+(wbCap.max-wbCap.min)*wb.value/100);wbState={whiteBalanceMode:'manual',colorTemperature:k};wbL.textContent='LOCKED '+k+'K';}
 expL.textContent='LOCKED'+(expState[expKey]?(' '+fmt(expState[expKey])):'');
 ae.disabled=aw.disabled=ex.disabled=wb.disabled=true;
 applyAll();
}
function manualExp(){
 if(locked||!track||!expCap)return;
 var t;
 if(expKey=='exposureTime'){var lo=Math.max(expCap.min,1e-4),hi=Math.max(lo*1.001,expCap.max);t=lo*Math.pow(hi/lo,ex.value/100);}
 else{t=expCap.min+(expCap.max-expCap.min)*(ex.value/100);}
 expVal=t;lastExpVal=t;expState={exposureMode:'manual'};expState[expKey]=t;
 applyAll(function(e){expL.textContent=e?('err '+e):('manual '+fmt(t));});
}
// Highlight-priority auto-exposure: lower exposureTime until bright pixels stop clipping at 255.
function autoExpStep(ctx){
 if(locked||!track||!expCap||expKey!='exposureTime'||!ae.checked)return;
 var d=ctx.getImageData(0,0,c.width,c.height).data,n=0,clip=0;
 for(var i=0;i<d.length;i+=80){n++;var m=Math.max(d[i],d[i+1],d[i+2]);if(m>=250)clip++;}
 var frac=n?clip/n:0;
 if(frac>0.004)expVal=clampExp(expVal*0.8);
 else if(frac<0.0004)expVal=clampExp(expVal*1.12);
 var now=Date.now();
 if(now-lastExpApply>250&&Math.abs(expVal-lastExpVal)>lastExpVal*0.02){
  lastExpApply=now;lastExpVal=expVal;expState={exposureMode:'manual'};expState[expKey]=expVal;
  applyAll();expL.textContent='auto '+fmt(expVal)+'  clip '+(Math.round(frac*1000)/10)+'%';
 }
}
function setExpMode(){
 if(!expCap)return;
 if(ae.checked){ex.style.display='none';
  if(expKey!='exposureTime'){expState={exposureMode:'continuous'};applyAll();expL.textContent='camera auto';}
  else{lastExpVal=0;} // force the next auto step to re-apply
 }else{ex.style.display='inline-block';manualExp();}
}
function applyWb(){
 if(locked||!track||!wbCap)return;
 if(aw.checked){wb.style.display='none';wbState={whiteBalanceMode:'continuous'};applyAll(function(){wbL.textContent='auto ('+wbActual()+')';});}
 else{wb.style.display='inline-block';var k=Math.round(wbCap.min+(wbCap.max-wbCap.min)*wb.value/100);
  wbState={whiteBalanceMode:'manual',colorTemperature:k};
  applyAll(function(e){wbL.textContent=e?('err '+e):('set '+k+'K -> got '+wbActual());});}
}
ex.oninput=function(){if(!ae.checked)manualExp();};
ae.onchange=setExpMode;
wb.oninput=function(){if(!aw.checked)applyWb();};
aw.onchange=applyWb;
async function setup(){
 try{
  var caps=track.getCapabilities?track.getCapabilities():{};
  if(caps.focusMode&&caps.focusMode.indexOf('continuous')>=0) await track.applyConstraints({advanced:[{focusMode:'continuous'}]});
  if(caps.exposureTime){expCap=caps.exposureTime;expKey='exposureTime';var lo=Math.max(caps.exposureTime.min,1e-4),hi=Math.max(caps.exposureTime.max,lo*1.001);expVal=lo*Math.pow(hi/lo,0.35);}
  else if(caps.exposureCompensation){expCap=caps.exposureCompensation;expKey='exposureCompensation';}
  if(expCap)setExpMode(); else expL.textContent='no exposure control';
  if(caps.colorTemperature){wbCap=caps.colorTemperature;applyWb();} else wbL.textContent='no WB control';
 }catch(e){expL.textContent='setup err '+e;}
}
async function start(){
 try{
  var st=await navigator.mediaDevices.getUserMedia({video:{facingMode:{ideal:'environment'},width:{ideal:1280},height:{ideal:720}},audio:false});
  v.srcObject=st;track=st.getVideoTracks()[0];await v.play();
  await setup();sendLoop();statusLoop();ctlLoop();maskLoop();
 }catch(e){s.textContent='Camera blocked: '+e+'. Allow camera + reload.';}
}
function ctlLoop(){
 setInterval(async function(){try{var r=await fetch('/ctl');if((await r.text())=='lock')freeze();}catch(e){}},400);
}
// Overlay the PC-computed region mask (screen/wall/other) on the live camera once calibration has it.
function maskLoop(){
 var o=q('o'),octx=o.getContext('2d'),img=new Image();
 setInterval(function(){
  fetch('/mask.png?'+Date.now()).then(function(r){return r.ok?r.blob():null;}).then(function(b){
   if(!b){o.style.display='none';q('leg').style.display='none';return;}
   var u=URL.createObjectURL(b);
   img.onload=function(){if(o.width!=img.naturalWidth||o.height!=img.naturalHeight){o.width=img.naturalWidth;o.height=img.naturalHeight;}octx.clearRect(0,0,o.width,o.height);octx.drawImage(img,0,0,o.width,o.height);URL.revokeObjectURL(u);o.style.display='block';q('leg').style.display='block';};
   img.src=u;
  }).catch(function(){});
 },800);
}
function sendLoop(){
 var ctx=c.getContext('2d',{willReadFrequently:true});
 setInterval(function(){
  if(!v.videoWidth)return;
  c.width=640;c.height=Math.round(640*v.videoHeight/v.videoWidth);
  ctx.drawImage(v,0,0,c.width,c.height);
  if(ae.checked)autoExpStep(ctx);
  c.toBlob(function(b){if(b)fetch('/frame',{method:'POST',body:b}).catch(function(){});},'image/jpeg',0.85);
 },200);
}
function statusLoop(){
 setInterval(async function(){try{var r=await fetch('/status');s.textContent=await r.text();}catch(e){}},600);
}
start();
</script></body></html>";
    }

    // Small always-on-top window that shows the URL to type on the phone + live status.
    internal sealed class CalibStatusForm : Form
    {
        private readonly Label _status;

        public CalibStatusForm(string url)
        {
            Text = "Govee Calibration";
            Width = 600; Height = 320;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; TopMost = true;

            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Waiting for phone...", Font = new Font(FontFamily.GenericMonospace, 10) };
            var urlLabel = new Label { Dock = DockStyle.Top, Height = 46, TextAlign = ContentAlignment.MiddleCenter, Text = url, Font = new Font(FontFamily.GenericSansSerif, 15, FontStyle.Bold) };
            var hint = new Label { Dock = DockStyle.Top, Height = 50, TextAlign = ContentAlignment.MiddleCenter, Text = "On your phone (same Wi-Fi), open this URL, accept the\r\nsecurity warning, and allow the camera:" };
            Controls.Add(_status);
            Controls.Add(urlLabel);
            Controls.Add(hint);
        }

        public void SetStatus(string s)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke((Action)(() => _status.Text = s)); } catch { } }
            else _status.Text = s;
        }
    }
}
