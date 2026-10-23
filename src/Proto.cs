// Razer Wireless Charger (코드명 Audrey) 명령 형식
// 블루투스: Razer Chroma Configurator 2.5.199 (com.razer.chromaconfigurator) 디컴파일
// USB: 충전기 HID(사용 페이지 FF00)의 91바이트 기능 보고서 — 레이저 표준 90바이트 명령으로 확인
using System;

namespace RazerChargerChroma
{
    static class Proto
    {
        public const string BleName = "Razer Wireless Charger";
        public static readonly Guid Service = new Guid("52401523-f97c-7f90-0e7f-6c6f4e36db1c");
        public static readonly Guid WriteChar = new Guid("52401524-f97c-7f90-0e7f-6c6f4e36db1c");
        public static readonly Guid NotifyChar = new Guid("52401525-f97c-7f90-0e7f-6c6f4e36db1c");
        public static readonly Guid DeviceInfo = new Guid("0000180a-0000-1000-8000-00805f9b34fb");
        public static readonly Guid Manufacturer = new Guid("00002a29-0000-1000-8000-00805f9b34fb");

        // 프로필: 충전 효과 = 2, 대기 효과 = 1
        public const int ProfileCharging = 2;
        public const int ProfileStandby = 1;
        // 조명 번호 (블루투스 머리 [7], USB 인자 [1])
        public const byte Led = 0x0A;

        // 10초 넘게 조용하면 블루투스 명령 앞에 보내는 깨우기 패킷
        public static readonly byte[] Wakeup = { 0x44, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00 };

        // 효과 데이터 10바이트: 효과, 방향(왼쪽 2/오른쪽 1), 지속 시간+1, 색 수, 색1 RGB, 색2 RGB
        public static byte[] EffectPayload(int effect, int color1, int color2, bool left, int speed)
        {
            bool on = effect != 0;
            int c1 = on ? color1 : 0, c2 = on ? color2 : 0;
            byte[] p = new byte[10];
            p[0] = (byte)effect;
            p[1] = (byte)(left ? 2 : 1);
            p[2] = (byte)(Math.Max(0, Math.Min(2, speed)) + 1);
            p[3] = (byte)(c2 != 0 ? 2 : 1);
            p[4] = (byte)(c1 >> 16); p[5] = (byte)(c1 >> 8); p[6] = (byte)c1;
            p[7] = (byte)(c2 >> 16); p[8] = (byte)(c2 >> 8); p[9] = (byte)c2;
            return p;
        }

        // 앱 밝기 0~255 → 장치 값 56~255 (앱과 같은 float 계산)
        public static byte DeviceBrightness(int app)
        {
            int v = Math.Max(0, Math.Min(255, app));
            return (byte)((int)(199f * (v / 255f)) + 56);
        }

        public static int AppBrightness(int dev)
        {
            if (dev <= 56) return 0;
            return Math.Max(0, Math.Min(255, (int)Math.Round((dev - 56) / 199.0 * 255.0)));
        }

        public static string Hex(byte[] b, int start, int count)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = start; i < start + count && i < b.Length; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(b[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }

    // 블루투스 패킷: 8바이트 머리 + 데이터
    class BleEncoder
    {
        byte seq;

        public byte[] Packet(byte group, byte cmd, byte b6, byte b7, byte[] payload)
        {
            int n = payload == null ? 0 : payload.Length;
            byte[] o = new byte[8 + n];
            o[0] = seq;
            seq = (byte)(seq + 1);
            if (seq == 0) seq = 1;
            o[1] = (byte)n; o[2] = (byte)(n >> 8); o[3] = (byte)(n >> 16);
            o[4] = group; o[5] = cmd; o[6] = b6; o[7] = b7;
            if (n > 0) Array.Copy(payload, 0, o, 8, n);
            return o;
        }

        public byte[] Effect(int profile, byte[] payload) { return Packet(0x10, 0x03, (byte)profile, Proto.Led, payload); }
        public byte[] Brightness(int profile, byte dev) { return Packet(0x10, 0x05, (byte)profile, Proto.Led, new[] { dev }); }
        public byte[] StatusLight(bool on) { return Packet(0x11, 0x04, 0, 0, new byte[] { (byte)(on ? 2 : 0) }); }
        public byte[] Reset() { return Packet(0x01, 0x01, 0, 0x01, null); }

        public byte[] Timers(int standbySec, int chargingSec)
        {
            int s = Math.Max(0, Math.Min(0xffff, standbySec)), c = Math.Max(0, Math.Min(0xffff, chargingSec));
            return Packet(0x01, 0x0b, 0, 0, new byte[] { (byte)s, (byte)(s >> 8), (byte)c, (byte)(c >> 8), (byte)s, (byte)(s >> 8) });
        }
    }
}
