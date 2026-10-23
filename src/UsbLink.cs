// USB 연결: 충전기 HID 인터페이스(VID 1532, PID 902A, 사용 페이지 FF00)에
// 레이저 표준 90바이트 명령을 기능 보고서(보고서 번호 0 + 90바이트)로 주고받는다.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RazerChargerChroma
{
    class EffectState
    {
        public int Effect, Left, Speed, Colors, Color1, Color2;
    }

    class UsbLink : IDisposable
    {
        const ushort Vid = 0x1532, Pid = 0x902A;
        const int ReportLen = 91;
        const byte StatusBusy = 0x01, StatusOk = 0x02;

        readonly object io = new object();
        SafeFileHandle handle;
        public string DevicePath { get; private set; }
        public bool IsOpen { get { return handle != null && !handle.IsInvalid && !handle.IsClosed; } }
        // 충전기 조명이 꺼지면(대기 타이머) 충전기가 잠들어 USB 명령이 '장치 동작 안 함(31)'으로 막힌다.
        // 버튼을 누르거나 폰을 올려 깨우면 그대로 다시 받는다.
        public bool Asleep { get; private set; }

        public static string FindPath()
        {
            Guid hid;
            HidD_GetHidGuid(out hid);
            IntPtr set = SetupDiGetClassDevs(ref hid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return null;
            try
            {
                string want = string.Format("vid_{0:x4}&pid_{1:x4}", Vid, Pid);
                for (uint i = 0; ; i++)
                {
                    var data = new SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref data)) break;
                    uint need;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out need, IntPtr.Zero);
                    if (need == 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal((int)need);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, need, out need, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(new IntPtr(buf.ToInt64() + 4));
                        if (path != null && path.ToLowerInvariant().Contains(want)) return path;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        public bool Open()
        {
            lock (io)
            {
                if (IsOpen) return true;
                string path = FindPath();
                if (path == null) return false;
                var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) return false;
                handle = h;
                DevicePath = path;
                return true;
            }
        }

        public void Close()
        {
            lock (io)
            {
                if (handle != null) { handle.Dispose(); handle = null; }
                Asleep = false;
            }
        }

        public void Dispose() { Close(); }

        // 명령 보내고 응답 받기. 실패하면 null
        public byte[] Request(byte cls, byte cmd, byte size, byte[] args)
        {
            lock (io)
            {
                if (!IsOpen) return null;
                byte[] req = new byte[ReportLen];
                req[2] = 0x1F;            // 순번(아무 값이나 받음)
                req[6] = size;
                req[7] = cls; req[8] = cmd;
                if (args != null) Array.Copy(args, 0, req, 9, Math.Min(args.Length, 80));
                byte crc = 0;
                for (int i = 3; i < 89; i++) crc ^= req[i];
                req[89] = crc;
                if (!HidD_SetFeature(handle, req, ReportLen)) { Fail(); return null; }
                byte[] res = new byte[ReportLen];
                for (int tries = 0; tries < 25; tries++)
                {
                    Thread.Sleep(tries == 0 ? 8 : 12);
                    Array.Clear(res, 0, res.Length);
                    if (!HidD_GetFeature(handle, res, ReportLen)) { Fail(); return null; }
                    if (res[1] != StatusBusy) break;
                }
                Asleep = false;
                byte[] r = new byte[90];
                Array.Copy(res, 1, r, 0, 90);
                return r;
            }
        }

        void Fail()
        {
            int err = Marshal.GetLastWin32Error();
            if (err == 31) { Asleep = true; return; }   // 잠든 충전기: 손잡이는 그대로 두고 깨기를 기다린다
            // 장치가 빠졌으면 닫아서 다시 찾게 한다
            if (err == 1167 || err == 2 || err == 6 || err == 22 || err == 433)
            {
                if (handle != null) { handle.Dispose(); handle = null; }
            }
        }

        static bool Ok(byte[] r) { return r != null && r[0] == StatusOk; }

        public string Firmware()
        {
            var r = Request(0x00, 0x81, 0x02, null);
            return Ok(r) ? string.Format("v{0}.{1:00}", r[8], r[9]) : null;
        }

        public string Serial()
        {
            var r = Request(0x00, 0x82, 0x16, null);
            if (!Ok(r)) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = 8; i < 8 + 22 && r[i] != 0; i++) sb.Append((char)r[i]);
            return sb.ToString();
        }

        public bool SetEffect(int profile, byte[] payload10)
        {
            byte[] a = new byte[12];
            a[0] = (byte)profile; a[1] = Proto.Led;
            Array.Copy(payload10, 0, a, 2, 10);
            return Ok(Request(0x0F, 0x02, 0x0C, a));
        }

        public bool SetBrightness(int profile, byte dev)
        {
            return Ok(Request(0x0F, 0x04, 0x03, new byte[] { (byte)profile, Proto.Led, dev }));
        }

        public EffectState ReadEffect(int profile)
        {
            var r = Request(0x0F, 0x82, 0x0C, new byte[] { (byte)profile, Proto.Led });
            if (!Ok(r)) return null;
            // r[8..] = 프로필, 조명, 효과, 방향, 지속 시간, 색 수, 색1, 색2
            var e = new EffectState();
            e.Effect = r[10]; e.Left = r[11]; e.Speed = r[12]; e.Colors = r[13];
            e.Color1 = (r[14] << 16) | (r[15] << 8) | r[16];
            e.Color2 = (r[17] << 16) | (r[18] << 8) | r[19];
            return e;
        }

        public int ReadBrightness(int profile)
        {
            var r = Request(0x0F, 0x84, 0x03, new byte[] { (byte)profile, Proto.Led, 0 });
            return Ok(r) ? r[10] : -1;
        }

        // ---------- Win32 ----------
        const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int n);
        [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int n);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, uint size, out uint need, IntPtr info);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    }
}
