// Razer 무선 충전기 크로마 — 윈도우 프로그램
// 화면은 원래 안드로이드 앱(Razer Chroma Configurator) 디자인을 HTML로 옮긴 것(ui 폴더),
// 장치와는 USB(HID) 또는 블루투스 LE로 통신한다.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace RazerChargerChroma
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, "RazerChargerChroma.Single", out first))
            {
                if (!first)
                {
                    MessageBox.Show("Razer Chroma 충전기 프로그램이 이미 실행 중입니다.", "Razer Chroma", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { SetProcessDPIAware(); } catch { }   // 고해상도 화면에서 흐려지지 않게
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string missing = MissingFiles();
                if (missing != null)
                {
                    MessageBox.Show("프로그램 실행에 필요한 파일이 없습니다: " + missing +
                        "\n\n압축 파일 안에서 바로 실행하면 이렇게 됩니다.\n압축을 먼저 풀고, 풀린 폴더 안의 RazerChargerChroma.exe를 실행해 주세요.",
                        "Razer Chroma", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                Run();
            }
        }

        // 공유받은 사람이 EXE만 꺼내 실행했을 때 알 수 없는 오류 대신 안내를 띄운다
        static string MissingFiles()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var f in new[] { "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll", @"ui\index.html" })
                if (!File.Exists(Path.Combine(dir, f))) return f;
            return null;
        }

        // WebView2 DLL은 이 함수가 처음 불릴 때 읽힌다 — 위에서 파일을 확인한 뒤에
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Run() { Application.Run(new MainForm()); }

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    }

    class MainForm : Form
    {
        readonly WebView2 web = new WebView2();
        readonly UsbLink usb = new UsbLink();
        readonly BleLink ble = new BleLink();
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer();
        readonly string dataDir, uiDir, hostFile, stateFile, logFile;
        Dictionary<string, object> host = new Dictionary<string, object>();
        bool ready, wantBle, usbChecking;
        string usbFirmware, usbSerial;
        DateTime lastBleTry = DateTime.MinValue;
        // 충전기가 잠든 사이에 바꾼 설정: 깨어나면 바로 보낸다 (프로필 → 효과 데이터, 밝기(-1 = 안 보냄))
        readonly Dictionary<int, KeyValuePair<byte[], int>> pendingEffect = new Dictionary<int, KeyValuePair<byte[], int>>();
        readonly Dictionary<int, byte> pendingBright = new Dictionary<int, byte>();

        public MainForm()
        {
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RazerChargerChroma");
            Directory.CreateDirectory(dataDir);
            uiDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui");
            hostFile = Path.Combine(dataDir, "host.json");
            stateFile = Path.Combine(dataDir, "settings.json");
            logFile = Path.Combine(dataDir, "log.txt");
            try { if (File.Exists(hostFile)) host = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(hostFile, Encoding.UTF8)) ?? host; } catch { }

            Text = "Razer Chroma";
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;
            // 폰 화면 비율의 창. 화면 배율에 맞추되 작업 영역을 넘지 않게
            float scale = 1f;
            try { using (var g = CreateGraphics()) scale = g.DpiX / 96f; } catch { }
            var work = Screen.PrimaryScreen.WorkingArea;
            int chrome = (int)(40 * scale);
            ClientSize = new Size(Math.Min((int)(420 * scale), work.Width - chrome), Math.Min((int)(860 * scale), work.Height - chrome));
            MinimumSize = new Size((int)(360 * scale), Math.Min((int)(600 * scale), work.Height - chrome));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Color.Black;
            Controls.Add(web);

            ble.StateChanged += (s, t) => BeginInvoke((Action)(() => { if (s == "connected") { host["bleAddress"] = ble.Address.ToString(); host["bleAuto"] = true; SaveHost(); } SendConn(); }));
            ble.Log += t => BeginInvoke((Action)(() => Log(t)));
            watch.Interval = 2000;
            watch.Tick += (s, e) => CheckDevices();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1;
            try { DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { }
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDir, "WebView2"));
                await web.EnsureCoreWebView2Async(env);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                MessageBox.Show("화면 표시에 필요한 Microsoft Edge WebView2 런타임이 없습니다.\nMicrosoft 사이트에서 'WebView2 런타임'을 설치한 뒤 다시 실행해 주세요.", "Razer Chroma", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            var core = web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("RAZER_CHROMA_DEVTOOLS") == "1";
            core.SetVirtualHostNameToFolderMapping("charger.app", uiDir, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (s, a) => { if (!a.IsSuccess) Log("화면 불러오기 실패: " + a.WebErrorStatus); };
            core.Navigate("https://charger.app/index.html");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            watch.Stop();
            try { ble.Disconnect(); } catch { }
            usb.Dispose();
            base.OnFormClosing(e);
        }

        // ---------- 화면과 주고받기 ----------
        void Post(object o)
        {
            if (!ready || web.CoreWebView2 == null) return;
            try { web.CoreWebView2.PostWebMessageAsJson(json.Serialize(o)); } catch { }
        }

        void Log(string text)
        {
            Post(new Dictionary<string, object> { { "type", "log" }, { "text", text } });
            try
            {
                var fi = new FileInfo(logFile);
                if (fi.Exists && fi.Length > 1000000) fi.Delete();
                File.AppendAllText(logFile, DateTime.Now.ToString("MM-dd HH:mm:ss ") + text + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        void Toast(string text) { Post(new Dictionary<string, object> { { "type", "toast" }, { "text", text } }); }

        void SendConn()
        {
            Post(new Dictionary<string, object> {
                { "type", "conn" },
                { "usb", new Dictionary<string, object> { { "on", usb.IsOpen }, { "asleep", usb.IsOpen && usb.Asleep }, { "pending", pendingEffect.Count + pendingBright.Count }, { "firmware", usbFirmware ?? "" }, { "serial", usbSerial ?? "" } } },
                { "ble", new Dictionary<string, object> { { "state", ble.State }, { "text", ble.Text ?? "" }, { "address", ble.Address == 0 ? "" : ble.Address.ToString("X12") } } }
            });
        }

        void SaveHost()
        {
            try { File.WriteAllText(hostFile, json.Serialize(host), Encoding.UTF8); } catch { }
        }

        static int I(Dictionary<string, object> d, string k, int def)
        {
            object v;
            if (d == null || !d.TryGetValue(k, out v) || v == null) return def;
            try { return Convert.ToInt32(v); } catch { return def; }
        }

        static bool B(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v is bool && (bool)v;
        }

        async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try { m = json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); } catch { return; }
            string cmd = m.ContainsKey("cmd") ? m["cmd"] as string : null;
            switch (cmd)
            {
                case "ready":
                    ready = true;
                    string saved = null;
                    try { if (File.Exists(stateFile)) saved = File.ReadAllText(stateFile, Encoding.UTF8); } catch { }
                    Post(new Dictionary<string, object> { { "type", "init" }, { "settings", saved == null ? null : json.DeserializeObject(saved) } });
                    SendConn();
                    await Task.Run(() => OpenUsb());
                    AfterUsbCheck(true);
                    watch.Start();
                    break;
                case "save":
                    try { File.WriteAllText(stateFile, json.Serialize(m["state"]), Encoding.UTF8); } catch { }
                    break;
                case "effect":
                    await ApplyEffect(I(m, "profile", 2), I(m, "effect", 1), I(m, "color1", 0), I(m, "color2", 0), B(m, "left"), I(m, "speed", 0), I(m, "brightness", 187), B(m, "withBrightness"));
                    break;
                case "brightness":
                    await ApplyBrightness(I(m, "profile", 2), I(m, "value", 187));
                    break;
                case "status":
                    if (RequireBle()) await ble.StatusLight(B(m, "on"));
                    break;
                case "timers":
                    if (RequireBle()) await ble.Timers(I(m, "standby", 0), I(m, "charging", 0));
                    break;
                case "reset":
                    if (RequireBle()) { if (await ble.Reset()) Toast("충전기 설정을 초기화했습니다"); }
                    break;
                case "connectBle":
                    wantBle = true;
                    await ble.Connect(SavedAddress());
                    break;
                case "disconnectBle":
                    wantBle = false;
                    host["bleAuto"] = false; SaveHost();
                    ble.Disconnect();
                    break;
                case "refresh":
                    if (usb.IsOpen) await ReadUsbState();
                    break;
            }
        }

        bool RequireBle()
        {
            if (ble.Connected) return true;
            Toast("이 설정은 블루투스로 연결했을 때만 바꿀 수 있습니다");
            return false;
        }

        ulong SavedAddress()
        {
            object v;
            ulong a;
            if (host.TryGetValue("bleAddress", out v) && v != null && ulong.TryParse(v.ToString(), out a)) return a;
            return 0;
        }

        // ---------- 조명 명령: USB가 있으면 USB, 없으면 블루투스 ----------
        async Task ApplyEffect(int profile, int effect, int c1, int c2, bool left, int speed, int brightness, bool withBrightness)
        {
            byte[] payload = Proto.EffectPayload(effect, c1, c2, left, speed);
            byte dev = Proto.DeviceBrightness(brightness);
            if (usb.IsOpen)
            {
                bool ok = await Task.Run(() => usb.SetEffect(profile, payload) && (effect == 0 || !withBrightness || usb.SetBrightness(profile, dev)));
                Log("USB  " + (profile == 2 ? "충전 효과" : "대기 효과") + " " + Proto.Hex(payload, 0, 10) + (ok ? "" : usb.Asleep ? "  → 충전기 절전 중" : "  → 실패"));
                if (ok) { pendingEffect.Remove(profile); return; }
                if (usb.Asleep && !ble.Connected)
                {
                    pendingEffect[profile] = new KeyValuePair<byte[], int>(payload, effect == 0 || !withBrightness ? -1 : dev);
                    SendConn();
                    Toast("충전기가 절전 중입니다 · 충전기 버튼을 누르거나 폰을 올리면 바로 적용됩니다");
                    return;
                }
                CheckUsbGone();
            }
            if (ble.Connected)
            {
                if (await ble.Effect(profile, payload) && effect != 0 && withBrightness) await ble.Brightness(profile, dev);
                return;
            }
            Toast("충전기가 연결되어 있지 않습니다");
        }

        async Task ApplyBrightness(int profile, int value)
        {
            byte dev = Proto.DeviceBrightness(value);
            if (usb.IsOpen)
            {
                bool ok = await Task.Run(() => usb.SetBrightness(profile, dev));
                if (ok) { pendingBright.Remove(profile); return; }
                if (usb.Asleep && !ble.Connected)
                {
                    pendingBright[profile] = dev;
                    SendConn();
                    Toast("충전기가 절전 중입니다 · 깨어나면 바로 적용됩니다");
                    return;
                }
                CheckUsbGone();
            }
            if (ble.Connected) { await ble.Brightness(profile, dev); return; }
            Toast("충전기가 연결되어 있지 않습니다");
        }

        // ---------- USB 연결 감시 ----------
        bool OpenUsb()
        {
            if (usb.IsOpen) return true;
            if (!usb.Open()) return false;
            usbFirmware = usb.Firmware();
            usbSerial = usb.Serial();
            return true;
        }

        void AfterUsbCheck(bool startup)
        {
            SendConn();
            if (usb.IsOpen)
            {
                Log("USB로 연결됨 " + (usbFirmware ?? "") + " " + (usbSerial ?? ""));
                var t = ReadUsbState();
                if (B(host, "bleAuto") && ble.State == "off") { wantBle = true; var x = ble.Connect(SavedAddress()); }
            }
            else if (startup)
            {
                wantBle = B(host, "bleAuto");
                var x = ble.Connect(SavedAddress());
            }
        }

        async Task ReadUsbState()
        {
            var r = await Task.Run(() =>
            {
                var d = new Dictionary<string, object>();
                foreach (int p in new[] { Proto.ProfileStandby, Proto.ProfileCharging })
                {
                    var e = usb.ReadEffect(p);
                    int b = usb.ReadBrightness(p);
                    if (e == null) continue;
                    d[p.ToString()] = new Dictionary<string, object> {
                        { "effect", e.Effect }, { "left", e.Left }, { "speed", e.Speed }, { "colors", e.Colors },
                        { "color1", e.Color1 }, { "color2", e.Color2 }, { "brightness", b < 0 ? -1 : Proto.AppBrightness(b) }
                    };
                }
                return d;
            });
            if (r.Count > 0) Post(new Dictionary<string, object> { { "type", "device" }, { "profiles", r } });
        }

        async Task FlushPending()
        {
            int n = pendingEffect.Count + pendingBright.Count;
            foreach (var kv in new List<KeyValuePair<int, KeyValuePair<byte[], int>>>(pendingEffect))
            {
                int profile = kv.Key; byte[] payload = kv.Value.Key; int dev = kv.Value.Value;
                bool ok = await Task.Run(() => usb.SetEffect(profile, payload) && (dev < 0 || usb.SetBrightness(profile, (byte)dev)));
                if (ok) pendingEffect.Remove(profile);
            }
            foreach (var kv in new List<KeyValuePair<int, byte>>(pendingBright))
            {
                int profile = kv.Key; byte dev = kv.Value;
                if (await Task.Run(() => usb.SetBrightness(profile, dev))) pendingBright.Remove(profile);
            }
            Toast(n > 0 && pendingEffect.Count + pendingBright.Count == 0 ? "충전기가 깨어나 설정을 적용했습니다" : "충전기가 깨어났습니다");
        }

        void CheckUsbGone()
        {
            if (usb.IsOpen && UsbLink.FindPath() == null) usb.Close();
            if (!usb.IsOpen) { usbFirmware = usbSerial = null; SendConn(); }
        }

        async void CheckDevices()
        {
            if (usbChecking) return;
            usbChecking = true;
            try
            {
                bool was = usb.IsOpen, wasAsleep = usb.IsOpen && usb.Asleep;
                bool now = await Task.Run(() =>
                {
                    if (usb.IsOpen)
                    {
                        if (UsbLink.FindPath() == null) usb.Close();
                        else if (usb.Asleep) { string fw = usb.Firmware(); if (fw != null) { usbFirmware = fw; if (usbSerial == null) usbSerial = usb.Serial(); } }
                        return usb.IsOpen;
                    }
                    return OpenUsb();
                });
                if (now != was)
                {
                    if (now) { Toast(usb.Asleep ? "USB로 연결되었습니다 · 충전기가 절전 중입니다" : "USB로 연결되었습니다"); AfterUsbCheck(false); }
                    else { usbFirmware = usbSerial = null; pendingEffect.Clear(); pendingBright.Clear(); Toast("USB 연결이 끊겼습니다"); SendConn(); if (!ble.Connected) wantBle = B(host, "bleAuto"); }
                }
                else if (now && wasAsleep && !usb.Asleep)
                {
                    Log("충전기가 깨어남");
                    await FlushPending();
                    await ReadUsbState();
                    SendConn();
                }
                else if (now && !wasAsleep && usb.Asleep) SendConn();
                // 블루투스 자동 재연결
                if (wantBle && !usb.IsOpen && !ble.Connected && ble.State != "scanning" && ble.State != "connecting" && ble.State != "needpair"
                    && (DateTime.Now - lastBleTry).TotalSeconds > 20)
                {
                    lastBleTry = DateTime.Now;
                    var x = ble.Connect(SavedAddress());
                }
            }
            finally { usbChecking = false; }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
