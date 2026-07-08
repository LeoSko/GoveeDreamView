using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace GoveeDreamView
{
    // Talks the Govee LAN "razer"/DreamView protocol (reverse-engineered, see README).
    //   control/stream  -> UDP :4003
    //   discovery scan   -> multicast 239.255.255.250:4003-in, replies on :4002
    internal sealed class GoveeClient : IDisposable
    {
        private const int ControlPort = 4003;
        private const int ScanReplyPort = 4002;
        private const int ScanSendPort = 4001;
        private const string Multicast = "239.255.255.250";

        private readonly Socket _send;       // unicast control/stream socket
        private readonly IPEndPoint _device;
        private readonly byte _stretchByte;

        public string Ip { get; }

        public GoveeClient(string ip, bool stretchToFit)
        {
            Ip = ip;
            _device = new IPEndPoint(IPAddress.Parse(ip), ControlPort);
            _stretchByte = (byte)(stretchToFit ? 0x01 : 0x00);
            _send = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        }

        // Discovers the first Govee device that answers the LAN scan. Returns (ip, sku) or null.
        public static (string ip, string sku)? Discover(int timeoutMs = 2500)
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
            try { sock.Bind(new IPEndPoint(IPAddress.Any, ScanReplyPort)); }
            catch { sock.Bind(new IPEndPoint(IPAddress.Any, 0)); }
            sock.ReceiveTimeout = timeoutMs;

            var scan = Encoding.UTF8.GetBytes("{\"msg\":{\"cmd\":\"scan\",\"data\":{\"account_topic\":\"reserve\"}}}");
            sock.SendTo(scan, new IPEndPoint(IPAddress.Parse(Multicast), ScanSendPort));
            sock.SendTo(scan, new IPEndPoint(IPAddress.Broadcast, ScanSendPort));

            var buf = new byte[8192];
            var deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                try
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = sock.ReceiveFrom(buf, ref from);
                    var json = Encoding.UTF8.GetString(buf, 0, n);
                    var ip = Match(json, "ip");
                    if (ip == null) continue;
                    return (ip, Match(json, "sku") ?? "");
                }
                catch (SocketException) { break; }
            }
            return null;
        }

        // Wakes the device into stream mode: power on, brightness, then enable razer/DreamView.
        public void Activate(int brightness)
        {
            Power(true);
            System.Threading.Thread.Sleep(100);
            SetBrightness(brightness);
            System.Threading.Thread.Sleep(100);
            SendJson("{\"msg\":{\"cmd\":\"razer\",\"data\":{\"pt\":\"uwABsQEK\"}}}"); // BB 00 01 B1 01 0A
        }

        // Sets the strip's master brightness (0-100). Also usable live while streaming.
        public void SetBrightness(int value) =>
            SendJson("{\"msg\":{\"cmd\":\"brightness\",\"data\":{\"value\":" + value + "}}}");

        // Powers the whole device on/off (not the same as stream mode: Deactivate only exits DreamView,
        // which leaves the strip lit on its last scene). Used to actually darken the strip on lock.
        public void Power(bool on) =>
            SendJson("{\"msg\":{\"cmd\":\"turn\",\"data\":{\"value\":" + (on ? 1 : 0) + "}}}");

        public void Deactivate()
        {
            try { SendJson("{\"msg\":{\"cmd\":\"razer\",\"data\":{\"pt\":\"uwABsQAL\"}}}"); } // BB 00 01 B1 00 0B
            catch { /* best effort on shutdown */ }
        }

        // rgb = N*3 bytes (R,G,B per zone). Builds the razer colour packet, base64s it, wraps in JSON.
        public void SendColors(byte[] rgb, int segments)
        {
            // header: BB 00 <len> B0 <stretch> <segments> + RGB... + XOR checksum
            // <len> is the payload length = stretch(1) + segments(1) + RGB(3N) = 2 + rgb.Length.
            // The device validates this; a wrong value makes it silently drop the packet.
            var packet = new byte[6 + rgb.Length + 1];
            packet[0] = 0xBB;
            packet[1] = 0x00;
            packet[2] = (byte)(2 + rgb.Length);
            packet[3] = 0xB0;
            packet[4] = _stretchByte;
            packet[5] = (byte)segments;
            Buffer.BlockCopy(rgb, 0, packet, 6, rgb.Length);
            byte chk = 0;
            for (int i = 0; i < packet.Length - 1; i++) chk ^= packet[i];
            packet[packet.Length - 1] = chk;

            var pt = Convert.ToBase64String(packet);
            SendJson("{\"msg\":{\"cmd\":\"razer\",\"data\":{\"pt\":\"" + pt + "\"}}}");
        }

        private void SendJson(string json)
        {
            var data = Encoding.UTF8.GetBytes(json);
            _send.SendTo(data, _device);
        }

        private static string Match(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        public void Dispose() => _send.Dispose();
    }
}
