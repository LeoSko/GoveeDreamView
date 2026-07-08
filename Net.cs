using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace GoveeDreamView
{
    // Locates a device's current IPv4 by its fixed hardware MAC, so a DHCP-assigned IP change
    // can't lose it. Some Govee firmware never answers the LAN scan (the 4002 reply path is dead),
    // so we can't ask the device its IP; instead we populate the OS ARP cache with a UDP sweep
    // and read MAC->IP straight out of it.
    internal static class DeviceLocator
    {
        public static byte[] ParseMac(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var parts = s.Split(':', '-');
            if (parts.Length != 6) return null;
            var b = new byte[6];
            for (int i = 0; i < 6; i++)
                if (!byte.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.HexNumber,
                                   System.Globalization.CultureInfo.InvariantCulture, out b[i]))
                    return null;
            return b;
        }

        public static string FormatMac(byte[] m) =>
            string.Join(":", Array.ConvertAll(m, x => x.ToString("x2")));

        // Sweeps the local /24 to refresh the ARP cache, then returns the IP holding `mac`, or null.
        public static string FindByMac(byte[] mac)
        {
            var local = LocalIPv4();
            if (local == null) return null;
            SweepArp(local);
            return ReadArpTable(mac);
        }

        // Resolves one IP's MAC (used to learn+remember a device we reached by other means).
        public static byte[] MacOf(string ip)
        {
            try
            {
                int dst = BitConverter.ToInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);
                var mac = new byte[6];
                int len = mac.Length;
                if (SendARP(dst, 0, mac, ref len) == 0 && len == 6) return mac;
            }
            catch { }
            return null;
        }

        private static IPAddress LocalIPv4()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect("8.8.8.8", 1);   // no packet sent; just resolves the outbound interface
                    return ((IPEndPoint)s.LocalEndPoint).Address;
                }
            }
            catch { return null; }
        }

        // One harmless UDP byte to every host in the /24 so the OS resolves their MACs into the cache.
        private static void SweepArp(IPAddress local)
        {
            var b = local.GetAddressBytes();
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                for (int i = 1; i <= 254; i++)
                {
                    b[3] = (byte)i;
                    try { s.SendTo(new byte[] { 0 }, new IPEndPoint(new IPAddress(b), 9)); } catch { }
                }
            }
            Thread.Sleep(1200);   // let ARP replies land
        }

        private static string ReadArpTable(byte[] mac)
        {
            int size = 0;
            GetIpNetTable(IntPtr.Zero, ref size, false);   // ask for required buffer size
            if (size <= 0) return null;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetIpNetTable(buf, ref size, false) != 0) return null;
                int n = Marshal.ReadInt32(buf);
                int rowSize = Marshal.SizeOf(typeof(IpNetRow));
                long p = (long)buf + 4;
                for (int i = 0; i < n; i++)
                {
                    var row = (IpNetRow)Marshal.PtrToStructure((IntPtr)p, typeof(IpNetRow));
                    p += rowSize;
                    if (row.PhysAddrLen == 6 &&
                        row.M0 == mac[0] && row.M1 == mac[1] && row.M2 == mac[2] &&
                        row.M3 == mac[3] && row.M4 == mac[4] && row.M5 == mac[5])
                        return new IPAddress(BitConverter.GetBytes(row.Addr)).ToString();
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return null;
        }

        [DllImport("iphlpapi.dll")]
        private static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(int dest, int src, byte[] mac, ref int macLen);

        // MIB_IPNETROW: bPhysAddr is MAXLEN_PHYSADDR (8) bytes, dwAddr is the IPv4 in network order.
        [StructLayout(LayoutKind.Sequential)]
        private struct IpNetRow
        {
            public int Index;
            public int PhysAddrLen;
            public byte M0, M1, M2, M3, M4, M5, M6, M7;
            public int Addr;
            public int Type;
        }
    }
}
