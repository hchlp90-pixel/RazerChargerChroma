// 블루투스 LE 연결: 윈도우 기본 블루투스 기능(WinRT)으로 충전기의 크로마 서비스에 명령을 쓴다.
// 앱과 같은 순서: 알림 특성 읽기 → 제조사 읽기 → 알림 켜기, 10초 넘게 쉬면 깨우기 패킷 먼저.
using System;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace RazerChargerChroma
{
    static class WinRt
    {
        public static Task<T> Wait<T>(IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>();
            op.Completed = (o, s) =>
            {
                if (s == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                else if (s == AsyncStatus.Error) tcs.TrySetException(o.ErrorCode);
                else tcs.TrySetCanceled();
            };
            return tcs.Task;
        }

        public static IBuffer ToBuffer(byte[] b)
        {
            var w = new DataWriter();
            w.WriteBytes(b);
            return w.DetachBuffer();
        }

        public static byte[] FromBuffer(IBuffer buf)
        {
            byte[] a = new byte[buf.Length];
            using (var r = DataReader.FromBuffer(buf)) r.ReadBytes(a);
            return a;
        }
    }

    class BleLink
    {
        public event Action<string, string> StateChanged;   // 상태, 설명
        public event Action<string> Log;

        BluetoothLEDevice device;
        GattDeviceService service;
        GattCharacteristic writeChar, notifyChar;
        GattSession session;
        readonly BleEncoder enc = new BleEncoder();
        DateTime lastWrite = DateTime.MinValue;
        bool busy;

        public string State { get; private set; }
        public string Text { get; private set; }
        public ulong Address { get; private set; }
        public bool Connected { get { return State == "connected"; } }

        public BleLink() { State = "off"; }

        void Set(string state, string text)
        {
            State = state;
            Text = text ?? "";
            var h = StateChanged;
            if (h != null) h(state, text ?? "");
        }

        void Say(string s) { var h = Log; if (h != null) h(s); }

        static async Task<ulong> Scan(int ms)
        {
            var tcs = new TaskCompletionSource<ulong>();
            var w = new BluetoothLEAdvertisementWatcher();
            w.ScanningMode = BluetoothLEScanningMode.Active;
            w.Received += (s, e) =>
            {
                if (e.Advertisement.LocalName == Proto.BleName) tcs.TrySetResult(e.BluetoothAddress);
            };
            w.Stopped += (s, e) => tcs.TrySetResult(0);
            w.Start();
            var done = await Task.WhenAny(tcs.Task, Task.Delay(ms));
            try { w.Stop(); } catch { }
            return done == tcs.Task ? tcs.Task.Result : 0;
        }

        public async Task<bool> Connect(ulong knownAddress)
        {
            if (busy) return false;
            busy = true;
            try
            {
                Drop();
                ulong addr = 0;
                Set("scanning", "충전기를 찾는 중…");
                addr = await Scan(knownAddress != 0 ? 6000 : 15000);
                if (addr == 0) addr = knownAddress;
                if (addr == 0) { Set("notfound", "블루투스로 충전기를 찾지 못했습니다"); return false; }

                Set("connecting", "연결 중…");
                device = await WinRt.Wait(BluetoothLEDevice.FromBluetoothAddressAsync(addr));
                if (device == null) { Set("error", "블루투스 장치를 열 수 없습니다"); return false; }
                Address = addr;
                device.ConnectionStatusChanged += OnConnectionStatus;

                var svc = await WinRt.Wait(device.GetGattServicesForUuidAsync(Proto.Service, BluetoothCacheMode.Uncached));
                if (svc.Status != GattCommunicationStatus.Success || svc.Services.Count == 0)
                {
                    Say("서비스 열기 실패(" + svc.Status + "), 페어링 시도");
                    await Pair();
                    svc = await WinRt.Wait(device.GetGattServicesForUuidAsync(Proto.Service, BluetoothCacheMode.Uncached));
                }
                if (svc.Status != GattCommunicationStatus.Success || svc.Services.Count == 0)
                {
                    // 충전기는 마지막으로 페어링한 기기 하나만 받는다 → 페어링 모드가 필요
                    if (svc.Status == GattCommunicationStatus.Unreachable)
                        Set("needpair", "충전기가 다른 기기(폰)와 페어링되어 있습니다. 충전기 버튼을 5~6초 길게 눌러 페어링 모드로 만든 뒤 다시 연결하세요.");
                    else
                        Set("error", "크로마 서비스에 접근하지 못했습니다 (" + svc.Status + ")");
                    Drop();
                    return false;
                }
                service = svc.Services[0];
                try
                {
                    session = await WinRt.Wait(GattSession.FromDeviceIdAsync(device.BluetoothDeviceId));
                    session.MaintainConnection = true;
                }
                catch { }

                var wr = await WinRt.Wait(service.GetCharacteristicsForUuidAsync(Proto.WriteChar, BluetoothCacheMode.Uncached));
                var nt = await WinRt.Wait(service.GetCharacteristicsForUuidAsync(Proto.NotifyChar, BluetoothCacheMode.Uncached));
                if (wr.Status != GattCommunicationStatus.Success || wr.Characteristics.Count == 0)
                {
                    Set("error", "명령 특성을 찾지 못했습니다");
                    Drop();
                    return false;
                }
                writeChar = wr.Characteristics[0];
                if (nt.Status == GattCommunicationStatus.Success && nt.Characteristics.Count > 0)
                {
                    notifyChar = nt.Characteristics[0];
                    try { await WinRt.Wait(notifyChar.ReadValueAsync(BluetoothCacheMode.Uncached)); } catch { }
                    try
                    {
                        notifyChar.ValueChanged += OnNotify;
                        var st = await WinRt.Wait(notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify));
                        if (st != GattCommunicationStatus.Success) Say("알림 켜기: " + st);
                    }
                    catch (Exception ex) { Say("알림 켜기 실패: " + ex.Message); }
                }
                lastWrite = DateTime.MinValue;
                Set("connected", device.Name);
                return true;
            }
            catch (Exception ex)
            {
                Set("error", "블루투스 연결 실패: " + ex.Message);
                Drop();
                return false;
            }
            finally { busy = false; }
        }

        async Task Pair()
        {
            try
            {
                var p = device.DeviceInformation.Pairing;
                if (p.IsPaired || !p.CanPair) return;
                Set("connecting", "페어링 중…");
                p.Custom.PairingRequested += (s, a) => a.Accept();
                var r = await WinRt.Wait(p.Custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ConfirmPinMatch));
                Say("페어링: " + r.Status);
            }
            catch (Exception ex) { Say("페어링 실패: " + ex.Message); }
        }

        void OnNotify(GattCharacteristic c, GattValueChangedEventArgs e)
        {
            try { byte[] b = WinRt.FromBuffer(e.CharacteristicValue); Say("받음  " + Proto.Hex(b, 0, b.Length)); } catch { }
        }

        void OnConnectionStatus(BluetoothLEDevice d, object o)
        {
            if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected && State == "connected")
                Set("lost", "블루투스 연결이 끊겼습니다");
            else if (d.ConnectionStatus == BluetoothConnectionStatus.Connected && State == "lost")
                Set("connected", d.Name);
        }

        public async Task<bool> Write(string label, byte[] packet)
        {
            if (writeChar == null) return false;
            try
            {
                if ((DateTime.Now - lastWrite).TotalMilliseconds > 10000)
                {
                    Say("보냄  " + Proto.Hex(Proto.Wakeup, 0, 8) + "  (깨우기)");
                    await WinRt.Wait(writeChar.WriteValueWithResultAsync(WinRt.ToBuffer(Proto.Wakeup), GattWriteOption.WriteWithResponse));
                }
                Say("보냄  " + Proto.Hex(packet, 0, packet.Length) + "  (" + label + ")");
                var r = await WinRt.Wait(writeChar.WriteValueWithResultAsync(WinRt.ToBuffer(packet), GattWriteOption.WriteWithResponse));
                lastWrite = DateTime.Now;
                if (r.Status != GattCommunicationStatus.Success) { Say("블루투스 쓰기 실패: " + r.Status); return false; }
                if (State == "lost") Set("connected", device != null ? device.Name : "");
                return true;
            }
            catch (Exception ex) { Say("블루투스 쓰기 오류: " + ex.Message); return false; }
        }

        public Task<bool> Effect(int profile, byte[] payload) { return Write(profile == 2 ? "충전 효과" : "대기 효과", enc.Effect(profile, payload)); }
        public Task<bool> Brightness(int profile, byte dev) { return Write("밝기", enc.Brightness(profile, dev)); }
        public Task<bool> StatusLight(bool on) { return Write("상태 표시기", enc.StatusLight(on)); }
        public Task<bool> Timers(int standby, int charging) { return Write("타이머", enc.Timers(standby, charging)); }
        public Task<bool> Reset() { return Write("초기화", enc.Reset()); }

        void Drop()
        {
            try { if (notifyChar != null) notifyChar.ValueChanged -= OnNotify; } catch { }
            try { if (session != null) session.Dispose(); } catch { }
            try { if (service != null) service.Dispose(); } catch { }
            try { if (device != null) { device.ConnectionStatusChanged -= OnConnectionStatus; device.Dispose(); } } catch { }
            notifyChar = writeChar = null; service = null; device = null; session = null;
        }

        public void Disconnect()
        {
            Drop();
            Set("off", "");
        }
    }
}
