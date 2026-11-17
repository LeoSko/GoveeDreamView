using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GoveeDreamView
{
    // Raises Changed(on) when Windows turns the console display off or back on (power plan idle
    // timeout). Windows sends the current state once at registration, so the first event can repeat
    // the state we already have. Dimmed counts as on.
    internal sealed class DisplayState : NativeWindow, IDisposable
    {
        private const int WM_POWERBROADCAST = 0x0218;
        private const int PBT_POWERSETTINGCHANGE = 0x8013;
        private static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new Guid("6FE69556-704A-47A0-8F24-C28D936FDA47");

        private readonly IntPtr _notify;
        public event Action<bool> Changed;

        public DisplayState()
        {
            CreateHandle(new CreateParams());
            var guid = GUID_CONSOLE_DISPLAY_STATE;
            _notify = RegisterPowerSettingNotification(Handle, ref guid, 0);   // DEVICE_NOTIFY_WINDOW_HANDLE
            if (_notify == IntPtr.Zero) Log.Write("RegisterPowerSettingNotification failed: " + Marshal.GetLastWin32Error());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt32() == PBT_POWERSETTINGCHANGE)
            {
                var ps = (PowerBroadcastSetting)Marshal.PtrToStructure(m.LParam, typeof(PowerBroadcastSetting));
                if (ps.PowerSetting == GUID_CONSOLE_DISPLAY_STATE) Changed?.Invoke(ps.Data != 0);   // 0 off, 1 on, 2 dimmed
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_notify != IntPtr.Zero) UnregisterPowerSettingNotification(_notify);
            DestroyHandle();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerBroadcastSetting
        {
            public Guid PowerSetting;
            public int DataLength;
            public int Data;   // first DWORD of the payload; the display state is one DWORD
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);

        [DllImport("user32.dll")]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    }
}
