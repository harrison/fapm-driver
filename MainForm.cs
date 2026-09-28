using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Fleck;
using SimConnect.NET;
using FSUIPC;
using LibreHardwareMonitor.Hardware;
using System.Management;
using System.Text.RegularExpressions;

namespace FapmDriver
{
    public class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) { computer.Traverse(this); }
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (IHardware subHardware in hardware.SubHardware) subHardware.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    public class AppConfig
    {
        public string SimBriefUser { get; set; } = "";
        public string VatsimCid { get; set; } = "";
        public bool VatsimAlarmEnabled { get; set; } = true;
        public int ScreenBrightness { get; set; } = 80;
        public int LedBrightness { get; set; } = 100;
        public int BuzzerVolume { get; set; } = 50;
    }

    public class AircraftProfile
    {
        public string Name { get; set; } = "Default Profile";
        public string MatchTitle { get; set; } = "";
        public bool UseSimVarsOnly { get; set; } = false;
        public string AP1_LVar { get; set; } = "";
        public string AP2_LVar { get; set; } = "";
        public string ATHR_LVar { get; set; } = "";
        public string MasterWarning_LVar { get; set; } = "";
        public string MasterCaution_LVar { get; set; } = "";
    }

    public class FlightDataPayload
    {
        public string type { get; set; } = "flight_data";
        public VatsimData vatsim { get; set; } = new();
        public SimBriefData simbrief { get; set; } = new();
        public TelemetryData telemetry { get; set; } = new();
        public PcPerfData pc_perf { get; set; } = new();
    }

    public class VatsimData { public bool online { get; set; } = false; public string callsign { get; set; } = ""; }

    public class SimBriefData
    {
        public string callsign { get; set; } = "N/A";
        public string origin { get; set; } = "----";
        public string destination { get; set; } = "----";
        public AirportWeather orig_wx { get; set; } = new();
        public AirportWeather dest_wx { get; set; } = new();
    }

    public class AirportWeather
    {
        public string metar { get; set; } = "No Data";
        public string metar_zh { get; set; } = "暂无数据";
    }

    public class PcPerfData
    {
        public int cpu_load { get; set; }
        public int cpu_temp { get; set; }
        public int cpu_freq { get; set; }
        public int gpu_load { get; set; }
        public int gpu_temp { get; set; }
        public int gpu_freq { get; set; }
        public float ram_used { get; set; }
        public float ram_total { get; set; }
        public string fan_rpm { get; set; } = "--";
    }

    public class TelemetryData
    {
        public int ias { get; set; }
        public double mach { get; set; }
        public int gs { get; set; }
        public int alt_baro { get; set; }
        public int alt_radio { get; set; }
        public int vs { get; set; }
        public bool ap1 { get; set; }
        public bool ap2 { get; set; }
        public bool athr { get; set; }
        public bool gear_down { get; set; }
        public int fuel_kg { get; set; }
        public double com1 { get; set; }
        public bool gs_overspeed { get; set; }
        public string flight_phase { get; set; } = "PREP";
        public string active_alarm { get; set; } = "NONE";
        public string sim_time { get; set; } = "00:00:00";
    }

    public class MainForm : Form
    {
        private AppConfig _config = new AppConfig();
        private readonly string _configFile = "config.json";
        private List<AircraftProfile> _profiles = new();
        private readonly string _profilesFile = "profiles.json";

        private TextBox txtSimBrief = null!, txtVatsim = null!;
        private CheckBox chkVatsimAlarm = null!;
        private TrackBar tbScreen = null!, tbLed = null!, tbBuzzer = null!;
        private Label lblScreenVal = null!, lblLedVal = null!, lblBuzzerVal = null!;
        private Label lblSimStatus = null!, lblNetStatus = null!, lblWsStatus = null!;
        private WebSocketServer _wsServer = null!;

        private readonly List<IWebSocketConnection> _clients = new();
        private readonly FlightDataPayload _globalData = new();
        private readonly HttpClient _httpClient = new();

        private bool _wasApConnected = false;
        private bool _apDiscLatch = false;
        private bool _wasAthrConnected = false;
        private bool _athrDiscLatch = false;
        private int _vatsimOfflineCounter = 0;
        private bool _hasVatsimConnected = false;
        private bool _vatsimDroppedLatch = false;
        private string _lastHighestAlarm = "NONE";
        private bool _isAlarmMuted = false;
        private bool _forceApiFetch = false;

        private DateTime _lastWarningTime = DateTime.MinValue;
        private DateTime _lastCautionTime = DateTime.MinValue;

        public MainForm()
        {
            SetupUI();
            LoadConfig();
            LoadProfiles();

            this.Load += (s, e) =>
            {
                StartWebSocketServer();
                Task.Run(UdpBroadcastLoop);
                Task.Run(HardwareMonitorLoop);
                Task.Run(SimConnectLoop);
                Task.Run(ExternalApiLoop);
            };
        }

        private string GetLocalIPv4()
        {
            List<string> ipList = new List<string>();
            try
            {
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.OperationalStatus == OperationalStatus.Up &&
                        iface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        string desc = iface.Description.ToLower();
                        if (desc.Contains("virtual") || desc.Contains("vmware") || desc.Contains("hyper") || desc.Contains("wsl")) continue;

                        foreach (var ip in iface.GetIPProperties().UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == AddressFamily.InterNetwork) ipList.Add(ip.Address.ToString());
                        }
                    }
                }
            }
            catch { }
            return ipList.Count > 0 ? string.Join(", ", ipList) : "未知 IP";
        }

        private Panel CreateCard(int y, int height, string title)
        {
            Panel pnl = new Panel { Bounds = new Rectangle(20, y, 420, height), BackColor = Color.FromArgb(45, 45, 48) };
            Label lblTitle = new Label { Text = title, Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold), Location = new Point(15, 12), AutoSize = true, ForeColor = Color.FromArgb(0, 255, 255) };
            Label separator = new Label { Bounds = new Rectangle(15, 40, 390, 1), BackColor = Color.FromArgb(62, 62, 66) };
            pnl.Controls.Add(lblTitle); pnl.Controls.Add(separator);
            return pnl;
        }

        private void SetupUI()
        {
            this.Text = "FAPM-Display 服务端";
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            this.Size = new Size(480, 560);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(30, 30, 30);
            this.Font = new Font("Microsoft YaHei", 9F);

            Panel pnlAccount = CreateCard(20, 150, "网络账号配置");
            pnlAccount.Controls.Add(new Label { Text = "SimBrief ID:", Bounds = new Rectangle(15, 55, 90, 25), ForeColor = Color.LightGray });
            txtSimBrief = new TextBox { Bounds = new Rectangle(110, 52, 290, 25), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(62, 62, 66), ForeColor = Color.White };
            pnlAccount.Controls.Add(txtSimBrief);

            pnlAccount.Controls.Add(new Label { Text = "VATSIM CID:", Bounds = new Rectangle(15, 85, 90, 25), ForeColor = Color.LightGray });
            txtVatsim = new TextBox { Bounds = new Rectangle(110, 82, 290, 25), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(62, 62, 66), ForeColor = Color.White };
            pnlAccount.Controls.Add(txtVatsim);

            chkVatsimAlarm = new CheckBox { Bounds = new Rectangle(110, 112, 180, 25), Text = "开启 VATSIM 离线报警", ForeColor = Color.LightGray };
            chkVatsimAlarm.CheckedChanged += (s, e) => { _config.VatsimAlarmEnabled = chkVatsimAlarm.Checked; };
            pnlAccount.Controls.Add(chkVatsimAlarm);

            Button btnSave = new Button { Text = "保存设置", Bounds = new Rectangle(300, 110, 100, 28), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White, Cursor = Cursors.Hand };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += (s, e) => SaveConfig();
            pnlAccount.Controls.Add(btnSave);
            this.Controls.Add(pnlAccount);

            Panel pnlHw = CreateCard(185, 170, "硬件参数调节");
            pnlHw.Controls.Add(new Label { Text = "屏幕背光:", Bounds = new Rectangle(15, 55, 80, 25), ForeColor = Color.LightGray });
            tbScreen = new TrackBar { Bounds = new Rectangle(90, 50, 270, 30), Maximum = 100, TickStyle = TickStyle.None, BackColor = Color.FromArgb(45, 45, 48) };
            lblScreenVal = new Label { Text = "80%", Bounds = new Rectangle(365, 55, 40, 25), ForeColor = Color.Cyan, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold) };
            pnlHw.Controls.Add(tbScreen); pnlHw.Controls.Add(lblScreenVal);

            pnlHw.Controls.Add(new Label { Text = "警告灯亮度:", Bounds = new Rectangle(15, 95, 80, 25), ForeColor = Color.LightGray });
            tbLed = new TrackBar { Bounds = new Rectangle(90, 90, 270, 30), Maximum = 100, TickStyle = TickStyle.None, BackColor = Color.FromArgb(45, 45, 48) };
            lblLedVal = new Label { Text = "100%", Bounds = new Rectangle(365, 95, 40, 25), ForeColor = Color.Cyan, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold) };
            pnlHw.Controls.Add(tbLed); pnlHw.Controls.Add(lblLedVal);

            pnlHw.Controls.Add(new Label { Text = "蜂鸣器音量:", Bounds = new Rectangle(15, 135, 80, 25), ForeColor = Color.LightGray });
            tbBuzzer = new TrackBar { Bounds = new Rectangle(90, 130, 270, 30), Maximum = 100, TickStyle = TickStyle.None, BackColor = Color.FromArgb(45, 45, 48) };
            lblBuzzerVal = new Label { Text = "50%", Bounds = new Rectangle(365, 135, 40, 25), ForeColor = Color.Cyan, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold) };
            pnlHw.Controls.Add(tbBuzzer); pnlHw.Controls.Add(lblBuzzerVal);

            EventHandler onSliderChanged = (s, e) => { lblScreenVal.Text = tbScreen.Value + "%"; lblLedVal.Text = tbLed.Value + "%"; lblBuzzerVal.Text = tbBuzzer.Value + "%"; SendHardwareConfig(); };
            tbScreen.Scroll += onSliderChanged; tbLed.Scroll += onSliderChanged; tbBuzzer.Scroll += onSliderChanged;

            EventHandler onSliderReleased = (s, e) => { _config.ScreenBrightness = tbScreen.Value; _config.LedBrightness = tbLed.Value; _config.BuzzerVolume = tbBuzzer.Value; File.WriteAllText(_configFile, JsonSerializer.Serialize(_config)); };
            tbScreen.MouseCaptureChanged += onSliderReleased; tbLed.MouseCaptureChanged += onSliderReleased; tbBuzzer.MouseCaptureChanged += onSliderReleased;
            this.Controls.Add(pnlHw);

            Panel pnlStatus = CreateCard(370, 140, "系统连接状态");
            lblSimStatus = new Label { Text = "正在等待游戏连接...", Bounds = new Rectangle(15, 55, 390, 20), ForeColor = Color.Gray };
            lblNetStatus = new Label { Text = "云端待机中...", Bounds = new Rectangle(15, 80, 390, 20), ForeColor = Color.Gray };
            lblWsStatus = new Label { Text = $"UDP 局域网广播中 ({GetLocalIPv4()})...", Bounds = new Rectangle(15, 105, 390, 20), ForeColor = Color.Gray };
            pnlStatus.Controls.Add(lblSimStatus); pnlStatus.Controls.Add(lblNetStatus); pnlStatus.Controls.Add(lblWsStatus);
            this.Controls.Add(pnlStatus);
        }

        private void LoadConfig()
        {
            if (File.Exists(_configFile)) { try { string json = File.ReadAllText(_configFile); _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig(); } catch { } }
            txtSimBrief.Text = _config.SimBriefUser; txtVatsim.Text = _config.VatsimCid; chkVatsimAlarm.Checked = _config.VatsimAlarmEnabled;
            tbScreen.Value = _config.ScreenBrightness; tbLed.Value = _config.LedBrightness; tbBuzzer.Value = _config.BuzzerVolume;
            lblScreenVal.Text = _config.ScreenBrightness + "%"; lblLedVal.Text = _config.LedBrightness + "%"; lblBuzzerVal.Text = _config.BuzzerVolume + "%";
        }

        private void SaveConfig()
        {
            _config.SimBriefUser = txtSimBrief.Text.Trim(); _config.VatsimCid = txtVatsim.Text.Trim(); _config.VatsimAlarmEnabled = chkVatsimAlarm.Checked;
            File.WriteAllText(_configFile, JsonSerializer.Serialize(_config));
            lblNetStatus.Text = "☁️ 正在请求云端数据更新..."; lblNetStatus.ForeColor = Color.Orange; _forceApiFetch = true;
        }

        private void LoadProfiles()
        {
            if (File.Exists(_profilesFile)) { try { _profiles = JsonSerializer.Deserialize<List<AircraftProfile>>(File.ReadAllText(_profilesFile)) ?? new List<AircraftProfile>(); } catch { } }
            if (_profiles.Count == 0)
            {
                _profiles.Add(new AircraftProfile { Name = "Fenix A320", MatchTitle = "FenixA320", AP1_LVar = "I_FCU_AP1", AP2_LVar = "I_FCU_AP2", ATHR_LVar = "I_FCU_ATHR", MasterWarning_LVar = "I_MIP_MASTER_WARNING_CAPT", MasterCaution_LVar = "I_MIP_MASTER_CAUTION_CAPT" });
                _profiles.Add(new AircraftProfile { Name = "iniBuilds A350", MatchTitle = "A350", AP1_LVar = "INI_ap1_on", AP2_LVar = "INI_ap2_on", ATHR_LVar = "INI_ATHR_LIGHT", MasterWarning_LVar = "INI_MASTER_WARNING", MasterCaution_LVar = "INI_MASTER_CAUTION" });
                _profiles.Add(new AircraftProfile { Name = "Default / General Aviation", MatchTitle = "", UseSimVarsOnly = true });
                File.WriteAllText(_profilesFile, JsonSerializer.Serialize(_profiles, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        private void SendHardwareConfig()
        {
            string json = JsonSerializer.Serialize(new { type = "hw_config", screen_brightness = tbScreen.Value, warn_led_brightness = tbLed.Value, buzzer_volume = tbBuzzer.Value });
            lock (_clients) { foreach (var client in _clients) if (client.IsAvailable) client.Send(json); }
        }

        private async Task HardwareMonitorLoop()
        {
            Computer computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true, IsMotherboardEnabled = true };
            try { computer.Open(); computer.Accept(new UpdateVisitor()); } catch { }

            while (true)
            {
                try
                {
                    computer.Accept(new UpdateVisitor());
                    int cLoad = 0, cTemp = 0, cFreq = 0, gLoad = 0, gTemp = 0, gFreq = 0;
                    float rUsed = 0, rTotal = 0;
                    int maxChassisFan = -1;

                    foreach (var hw in computer.Hardware)
                    {
                        if (hw.HardwareType == HardwareType.Cpu)
                        {
                            foreach (var s in hw.Sensors)
                            {
                                if (s.SensorType == SensorType.Load && s.Name.Contains("Total")) cLoad = (int)s.Value.GetValueOrDefault();
                                if (s.SensorType == SensorType.Temperature) cTemp = Math.Max(cTemp, (int)s.Value.GetValueOrDefault());
                                if (s.SensorType == SensorType.Clock) cFreq = Math.Max(cFreq, (int)s.Value.GetValueOrDefault());
                            }
                        }
                        else if (hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd)
                        {
                            foreach (var s in hw.Sensors)
                            {
                                if (s.SensorType == SensorType.Load && s.Name.Contains("Core")) gLoad = (int)s.Value.GetValueOrDefault();
                                if (s.SensorType == SensorType.Temperature && s.Name.Contains("Core")) gTemp = (int)s.Value.GetValueOrDefault();
                                if (s.SensorType == SensorType.Clock && s.Name.Contains("Core")) gFreq = (int)s.Value.GetValueOrDefault();
                            }
                        }
                        else if (hw.HardwareType == HardwareType.Memory)
                        {
                            float used = 0, available = 0;
                            foreach (var s in hw.Sensors)
                            {
                                if (s.SensorType == SensorType.Data && s.Name.Contains("Used")) used = s.Value.GetValueOrDefault();
                                if (s.SensorType == SensorType.Data && s.Name.Contains("Available")) available = s.Value.GetValueOrDefault();
                            }
                            rUsed = used; rTotal = used + available;
                        }

                        Action<IEnumerable<ISensor>> scanFans = (sensors) =>
                        {
                            foreach (var s in sensors)
                            {
                                if (s.SensorType == SensorType.Fan)
                                {
                                    string n = s.Name.ToLower();
                                    if (n.Contains("chassis") || n.Contains("sys") || n.Contains("case") || n.Contains("cha"))
                                    {
                                        maxChassisFan = Math.Max(maxChassisFan, (int)s.Value.GetValueOrDefault());
                                    }
                                }
                            }
                        };
                        scanFans(hw.Sensors);
                        foreach (var sub in hw.SubHardware) scanFans(sub.Sensors);
                    }

                    if (cFreq == 0)
                    {
                        try
                        {
                            using (var searcher = new ManagementObjectSearcher("select CurrentClockSpeed from Win32_Processor"))
                            {
                                foreach (var item in searcher.Get()) { cFreq = Convert.ToInt32(item["CurrentClockSpeed"]); break; }
                            }
                        }
                        catch { }
                    }

                    lock (_globalData)
                    {
                        _globalData.pc_perf.cpu_load = cLoad;
                        _globalData.pc_perf.cpu_temp = cTemp;
                        _globalData.pc_perf.cpu_freq = cFreq;
                        _globalData.pc_perf.gpu_load = gLoad;
                        _globalData.pc_perf.gpu_temp = gTemp;
                        _globalData.pc_perf.gpu_freq = gFreq;
                        _globalData.pc_perf.ram_used = (float)Math.Round(rUsed, 1);
                        _globalData.pc_perf.ram_total = (float)Math.Round(rTotal, 1);
                        _globalData.pc_perf.fan_rpm = maxChassisFan >= 0 ? maxChassisFan.ToString() : "--";
                    }
                }
                catch { }
                await Task.Delay(2000);
            }
        }

        private async Task UdpBroadcastLoop()
        {
            byte[] bytes = Encoding.UTF8.GetBytes("FAPM_DISCOVER:8765");
            while (true)
            {
                try
                {
                    using var globalUdp = new UdpClient(); globalUdp.EnableBroadcast = true;
                    try { await globalUdp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, 8766)); } catch { }

                    foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (iface.OperationalStatus == OperationalStatus.Up && iface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                        {
                            foreach (var ip in iface.GetIPProperties().UnicastAddresses)
                            {
                                if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                                {
                                    byte[] ipBytes = ip.Address.GetAddressBytes(); byte[] maskBytes = ip.IPv4Mask.GetAddressBytes();
                                    if (ipBytes.Length == 4 && maskBytes.Length == 4)
                                    {
                                        byte[] broadcastBytes = new byte[4];
                                        for (int i = 0; i < 4; i++) broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                                        try { using var subUdp = new UdpClient(); subUdp.EnableBroadcast = true; await subUdp.SendAsync(bytes, bytes.Length, new IPEndPoint(new IPAddress(broadcastBytes), 8766)); } catch { }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
                await Task.Delay(2000);
            }
        }

        private void StartWebSocketServer()
        {
            FleckLog.Level = LogLevel.Error;
            _wsServer = new WebSocketServer("ws://0.0.0.0:8765");
            _wsServer.Start(socket =>
            {
                socket.OnOpen = () => { lock (_clients) _clients.Add(socket); this.Invoke((MethodInvoker)delegate { lblWsStatus.Text = $"🔌 设备已连接 ({socket.ConnectionInfo.ClientIpAddress})"; lblWsStatus.ForeColor = Color.LawnGreen; SendHardwareConfig(); }); };
                socket.OnClose = () => { lock (_clients) _clients.Remove(socket); this.Invoke((MethodInvoker)delegate { lblWsStatus.Text = $"📡 设备断开，UDP 局域网广播中 ({GetLocalIPv4()})..."; lblWsStatus.ForeColor = Color.Gray; }); };
                socket.OnMessage = message => { try { var doc = JsonDocument.Parse(message); if (doc.RootElement.TryGetProperty("action", out var actionProp) && actionProp.GetString() == "alarm_ack") _isAlarmMuted = true; } catch { } };
            });
        }

        private async Task SimConnectLoop()
        {
            var client = new SimConnectClient();
            bool isSimConnected = false;
            string currentAircraftTitle = "";
            AircraftProfile activeProfile = _profiles.Find(p => string.IsNullOrEmpty(p.MatchTitle)) ?? new AircraftProfile();

            while (true)
            {
                if (!isSimConnected)
                {
                    try
                    {
                        this.Invoke((MethodInvoker)delegate { lblSimStatus.Text = "⏳ MSFS: 正在等待游戏启动..."; lblSimStatus.ForeColor = Color.DarkOrange; });
                        await client.ConnectAsync();
                        MSFSVariableServices.Init();
                        MSFSVariableServices.Start();
                        isSimConnected = true;
                        this.Invoke((MethodInvoker)delegate { lblSimStatus.Text = "✈️ MSFS: 已连接 (等待进入机舱...)"; lblSimStatus.ForeColor = Color.LawnGreen; });
                    }
                    catch
                    {
                        await Task.Delay(3000);
                        continue;
                    }
                }

                try
                {
                    string title = "";
                    try
                    {
                        title = await client.SimVars.GetAsync<string>("TITLE", "");
                    }
                    catch
                    {
                        await Task.Delay(2000);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(title) && title != currentAircraftTitle)
                    {
                        currentAircraftTitle = title;
                        foreach (var p in _profiles)
                            if (!string.IsNullOrEmpty(p.MatchTitle) && title.IndexOf(p.MatchTitle, StringComparison.OrdinalIgnoreCase) >= 0)
                            { activeProfile = p; break; }
                        this.Invoke((MethodInvoker)delegate { lblSimStatus.Text = $"✈️ MSFS: 已连接 (当前机模: {activeProfile.Name})"; });
                    }

                    // 💡 注意：这连续的十几次底层请求会导致本轮循环耗时近 800 毫秒
                    double ias = await client.SimVars.GetAsync<double>("AIRSPEED INDICATED", "knots");
                    double mach = await client.SimVars.GetAsync<double>("AIRSPEED MACH", "Mach");
                    double gs = await client.SimVars.GetAsync<double>("GROUND VELOCITY", "knots");
                    double altBaro = await client.SimVars.GetAsync<double>("INDICATED ALTITUDE", "feet");
                    double altRadio = await client.SimVars.GetAsync<double>("RADIO HEIGHT", "feet");
                    double vsFps = await client.SimVars.GetAsync<double>("VERTICAL SPEED", "feet per second");
                    double gearHandle = await client.SimVars.GetAsync<double>("GEAR HANDLE POSITION", "Bool");
                    double overspeed = await client.SimVars.GetAsync<double>("OVERSPEED WARNING", "Bool");
                    double stall = await client.SimVars.GetAsync<double>("STALL WARNING", "Bool");

                    double onGround = await client.SimVars.GetAsync<double>("SIM ON GROUND", "Bool");
                    double eng1 = await client.SimVars.GetAsync<double>("ENG COMBUSTION:1", "Bool");
                    double eng2 = await client.SimVars.GetAsync<double>("ENG COMBUSTION:2", "Bool");
                    double flaps = await client.SimVars.GetAsync<double>("TRAILING EDGE FLAPS LEFT PERCENT", "Percent");
                    double fuelLbs = await client.SimVars.GetAsync<double>("FUEL TOTAL QUANTITY WEIGHT", "Pounds");
                    double com1 = await client.SimVars.GetAsync<double>("COM ACTIVE FREQUENCY:1", "MHz");
                    double zulu = await client.SimVars.GetAsync<double>("ZULU TIME", "seconds");

                    bool ap1Active = false, ap2Active = false, athrActive = false, warningActive = false, cautionActive = false;
                    if (activeProfile.UseSimVarsOnly) { ap1Active = await client.SimVars.GetAsync<double>("AUTOPILOT MASTER", "Bool") > 0.5; }
                    else
                    {
                        if (!string.IsNullOrEmpty(activeProfile.AP1_LVar)) ap1Active = GetLVarValue(activeProfile.AP1_LVar) > 0.5;
                        if (!string.IsNullOrEmpty(activeProfile.AP2_LVar)) ap2Active = GetLVarValue(activeProfile.AP2_LVar) > 0.5;
                        if (!string.IsNullOrEmpty(activeProfile.ATHR_LVar)) athrActive = GetLVarValue(activeProfile.ATHR_LVar) > 0.5;
                        if (!string.IsNullOrEmpty(activeProfile.MasterWarning_LVar)) warningActive = GetLVarValue(activeProfile.MasterWarning_LVar) > 0.5;
                        if (!string.IsNullOrEmpty(activeProfile.MasterCaution_LVar)) cautionActive = GetLVarValue(activeProfile.MasterCaution_LVar) > 0.5;
                    }

                    bool isApConnected = ap1Active || ap2Active;
                    bool enginesRunning = eng1 > 0.5 || eng2 > 0.5;
                    int vs = (int)Math.Round(vsFps * 60.0);

                    string phaseStr = "PREP";
                    if (onGround > 0.5)
                    {
                        phaseStr = enginesRunning ? (gs > 35 ? "ROLL" : "TAXI") : "PREP";
                    }
                    else
                    {
                        if (altRadio < 4000 && (gearHandle > 0.5 || flaps > 0)) phaseStr = "APPROACH";
                        else if (vs > 350) phaseStr = "CLIMB";
                        else if (vs < -350) phaseStr = "DESCENT";
                        else phaseStr = "CRUISE";
                    }

                    bool isFlying = phaseStr == "CLIMB" || phaseStr == "CRUISE" || phaseStr == "DESCENT";

                    if (isApConnected) _apDiscLatch = false;
                    else if (_wasApConnected && !isApConnected && isFlying) _apDiscLatch = true;
                    _wasApConnected = isApConnected;

                    if (athrActive) _athrDiscLatch = false;
                    else if (_wasAthrConnected && !athrActive && isFlying) _athrDiscLatch = true;
                    _wasAthrConnected = athrActive;

                    // 💡 终极防抖：由于多变量连续读取会产生轮询延迟，必须将冷却时间加大到 3.5 秒以覆盖采样盲区
                    if (warningActive) _lastWarningTime = DateTime.Now;
                    if (cautionActive) _lastCautionTime = DateTime.Now;
                    bool isWarningLatched = (DateTime.Now - _lastWarningTime).TotalSeconds < 3.5;
                    bool isCautionLatched = (DateTime.Now - _lastCautionTime).TotalSeconds < 3.5;

                    bool isOfflineAlarm = _config.VatsimAlarmEnabled && !string.IsNullOrWhiteSpace(_config.VatsimCid) && !_globalData.vatsim.online;
                    bool inActivePhase = phaseStr != "PREP" && phaseStr != "COMPLETE";

                    string currentHighestAlarm = "NONE";
                    if (stall > 0.5) currentHighestAlarm = "STALL";
                    else if (overspeed > 0.5) currentHighestAlarm = "OVERSPEED";
                    else if (_apDiscLatch) currentHighestAlarm = "AP_DISC";
                    else if (_athrDiscLatch) currentHighestAlarm = "ATHR_DISC";
                    else if (isFlying && isWarningLatched) currentHighestAlarm = "WARNING"; // 💡 已恢复你在天上才响的限制
                    else if (isFlying && isCautionLatched) currentHighestAlarm = "CAUTION"; // 💡 已恢复你在天上才响的限制
                    else if (onGround < 0.5 && vs < -200 && altRadio < 1000 && gearHandle < 0.5) currentHighestAlarm = "GEAR";
                    else if (isOfflineAlarm && inActivePhase) currentHighestAlarm = "VATSIM";

                    if (currentHighestAlarm != _lastHighestAlarm && currentHighestAlarm != "NONE") _isAlarmMuted = false;
                    _lastHighestAlarm = currentHighestAlarm;
                    int totalSecs = (int)zulu % 86400;

                    lock (_globalData)
                    {
                        _globalData.telemetry.ias = (int)Math.Round(ias); _globalData.telemetry.mach = Math.Round(mach, 3);
                        _globalData.telemetry.gs = (int)Math.Round(gs); _globalData.telemetry.alt_baro = (int)Math.Round(altBaro);
                        _globalData.telemetry.alt_radio = (int)Math.Round(altRadio); _globalData.telemetry.vs = vs;
                        _globalData.telemetry.gear_down = gearHandle > 0.5;
                        _globalData.telemetry.fuel_kg = (int)Math.Round(fuelLbs / 2.2046);
                        _globalData.telemetry.com1 = Math.Round(com1, 3); _globalData.telemetry.ap1 = ap1Active;
                        _globalData.telemetry.ap2 = ap2Active; _globalData.telemetry.athr = athrActive;
                        _globalData.telemetry.gs_overspeed = (phaseStr == "TAXI") && gs > 28;
                        _globalData.telemetry.flight_phase = phaseStr;
                        _globalData.telemetry.active_alarm = _isAlarmMuted ? "NONE" : currentHighestAlarm;
                        _globalData.telemetry.sim_time = $"{totalSecs / 3600:D2}:{(totalSecs % 3600) / 60:D2}:{totalSecs % 60:D2}";
                    }

                    string json; lock (_globalData) { json = JsonSerializer.Serialize(_globalData); }
                    lock (_clients) { foreach (var c in _clients) if (c.IsAvailable) c.Send(json); }
                }
                catch
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        lblSimStatus.Text = "⏳ MSFS: 游戏加载中，等待同步...";
                        lblSimStatus.ForeColor = Color.DarkOrange;
                    });
                    await Task.Delay(2000);
                }
                await Task.Delay(100);
            }
        }

        private static double GetLVarValue(string lvarName) { if (MSFSVariableServices.LVars.Exists(lvarName)) return MSFSVariableServices.LVars[lvarName].Value; if (MSFSVariableServices.LVars.Exists("L:" + lvarName)) return MSFSVariableServices.LVars["L:" + lvarName].Value; return 0.0; }

        private static async Task<string> GetPureMetar(string icao)
        {
            try { var res = await new HttpClient().GetStringAsync($"https://metar.vatsim.net/{icao}"); if (!string.IsNullOrWhiteSpace(res)) return res.Trim(); } catch { }
            try { var res = await new HttpClient().GetStringAsync($"https://aviationweather.gov/api/data/metar?ids={icao}"); if (!string.IsNullOrWhiteSpace(res)) return res.Trim(); } catch { }
            return "No METAR Data";
        }

        private static string TranslateMetarToChinese(string rawMetar)
        {
            if (string.IsNullOrEmpty(rawMetar) || rawMetar == "No METAR Data") return "暂无气象数据";
            try
            {
                string[] parts = rawMetar.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string time = "", wind = "", vis = "", wx = "", clouds = "", temp = "", qnh = "", trend = "";

                foreach (string p in parts)
                {
                    if (Regex.IsMatch(p, @"^\d{6}Z$"))
                    {
                        int d = int.Parse(p.Substring(0, 2)), h = int.Parse(p.Substring(2, 2)), m = int.Parse(p.Substring(4, 2));
                        time = $"{d}日 {(h + 8) % 24:D2}:{m:D2}";
                    }
                    else if (Regex.IsMatch(p, @"^(\d{3}|VRB)(\d{2,3})(G\d{2,3})?(KT|MPS|KMH)$"))
                    {
                        var m = Regex.Match(p, @"^(\d{3}|VRB)(\d{2,3})(G\d{2,3})?(KT|MPS|KMH)$");
                        string dir = m.Groups[1].Value == "VRB" ? "不定" : m.Groups[1].Value + "°";
                        string spd = m.Groups[2].Value;
                        string unit = m.Groups[4].Value == "MPS" ? "米/秒" : (m.Groups[4].Value == "KMH" ? "公里/小时" : "节");
                        wind = $"{dir} {spd}{unit}";
                        if (m.Groups[3].Success) wind += $" 阵风{m.Groups[3].Value.Substring(1)}{unit}";
                    }
                    else if (Regex.IsMatch(p, @"^\d{4}$")) vis = p == "9999" ? "≥10公里" : p + "米";
                    else if (p == "CAVOK") { vis = "≥10公里"; clouds = "晴空无云 "; }
                    else if (Regex.IsMatch(p, @"^(M?\d{2})/(M?\d{2})?$"))
                    {
                        var m = Regex.Match(p, @"^(M?\d{2})/(M?\d{2})?$");
                        temp = $"{m.Groups[1].Value.Replace("M", "-")}℃";
                        if (m.Groups[2].Success && !string.IsNullOrEmpty(m.Groups[2].Value)) temp += $", 露点{m.Groups[2].Value.Replace("M", "-")}℃";
                    }
                    else if (Regex.IsMatch(p, @"^[QA](\d{4})$"))
                    {
                        qnh = p.StartsWith("Q") ? p.Substring(1) + "hPa" : (int.Parse(p.Substring(1)) / 100.0).ToString("F2") + "inHg";
                    }
                    else if (Regex.IsMatch(p, @"^(FEW|SCT|BKN|OVC)(\d{3}|///)(CB|TCU)?$"))
                    {
                        var m = Regex.Match(p, @"^(FEW|SCT|BKN|OVC)(\d{3}|///)(CB|TCU)?$");
                        string amt = m.Groups[1].Value switch { "FEW" => "少云", "SCT" => "疏云", "BKN" => "多云", "OVC" => "阴天", _ => "" };
                        string ext = m.Groups[3].Value switch { "CB" => "(积雨云)", "TCU" => "(浓积云)", _ => "" };
                        clouds += amt + ext + " ";
                    }
                    else if (Regex.IsMatch(p, @"^VV(\d{3}|///)$"))
                    {
                        var vvMatch = Regex.Match(p, @"^VV(\d{3}|///)$").Groups[1].Value;
                        clouds += vvMatch == "///" ? "垂直能见度不明 " : $"垂直能见度{int.Parse(vvMatch) * 100}英尺 ";
                    }
                    else if (p == "NSC") clouds += "无重要云 ";
                    else if (p == "NCD") clouds += "未探测到云 ";
                    else if (p == "NOSIG") trend += "无显著变化 ";
                    else if (p == "BECMG") trend += "逐渐变为 ";
                    else if (p == "TEMPO") trend += "短时 ";
                    else
                    {
                        var wxMatch = Regex.Match(p, @"^([-+]|VC)?(MI|PR|BC|DR|BL|SH|TS|FZ)?(DZ|RA|SN|SG|IC|PL|GR|GS|UP|FG|BR|SA|DU|HZ|FU|VA|SQ|FC|SS|DS)$");
                        if (wxMatch.Success)
                        {
                            string intensity = wxMatch.Groups[1].Value switch { "-" => "小", "+" => "大", "VC" => "附近", _ => "" };
                            string desc = wxMatch.Groups[2].Value switch { "MI" => "浅", "PR" => "部分", "BC" => "散布", "DR" => "低吹", "BL" => "高吹", "SH" => "阵", "TS" => "雷暴", "FZ" => "冻", _ => "" };
                            string type = wxMatch.Groups[3].Value switch { "DZ" => "毛毛雨", "RA" => "雨", "SN" => "雪", "SG" => "雪粒", "IC" => "冰晶", "PL" => "冰珠", "GR" => "冰雹", "GS" => "小冰雹", "UP" => "不明", "FG" => "雾", "BR" => "薄雾", "SA" => "沙", "DU" => "浮尘", "HZ" => "霾", "FU" => "烟", "VA" => "火山灰", "SQ" => "飑", "FC" => "龙卷", "SS" => "沙尘暴", "DS" => "尘暴", _ => "" };
                            wx += intensity + desc + type + " ";
                        }
                    }
                }

                List<string> lines = new List<string>();
                if (time != "") lines.Add($"发布: {time}");
                if (wx != "") lines.Add($"天气: {wx.Trim()}");
                if (wind != "") lines.Add($"风向风速: {wind}");
                if (vis != "") lines.Add($"能见度: {vis}");
                if (clouds != "") lines.Add($"云量: {clouds.Trim()}");
                if (temp != "") lines.Add($"气温: {temp}");
                if (qnh != "") lines.Add($"气压: {qnh}");
                if (trend != "") lines.Add($"趋势: {trend.Trim()}");

                return lines.Count > 0 ? string.Join("\n", lines) : rawMetar;
            }
            catch { return rawMetar; }
        }

        private async Task ExternalApiLoop()
        {
            while (true)
            {
                _forceApiFetch = false;
                string simBriefUser = _config.SimBriefUser; string vatsimCid = _config.VatsimCid;
                if (string.IsNullOrEmpty(simBriefUser))
                {
                    this.Invoke((MethodInvoker)delegate { lblNetStatus.Text = "☁️ 网络: 待机 (未配置 SimBrief 账号)"; lblNetStatus.ForeColor = Color.DimGray; });
                    for (int i = 0; i < 5; i++) { if (_forceApiFetch) break; await Task.Delay(1000); }
                    continue;
                }
                try
                {
                    var sbResp = await _httpClient.GetStringAsync($"https://www.simbrief.com/api/xml.fetcher.php?username={simBriefUser}&json=1");
                    using var doc = JsonDocument.Parse(sbResp); var root = doc.RootElement;
                    var origin = root.GetProperty("origin").GetProperty("icao_code").GetString() ?? "----"; var dest = root.GetProperty("destination").GetProperty("icao_code").GetString() ?? "----";
                    var callsign = root.GetProperty("general").GetProperty("icao_airline").GetString() + root.GetProperty("general").GetProperty("flight_number").GetString();

                    string liveOrigMetar = await GetPureMetar(origin);
                    string liveDestMetar = await GetPureMetar(dest);

                    lock (_globalData)
                    {
                        _globalData.simbrief.origin = origin; _globalData.simbrief.destination = dest; _globalData.simbrief.callsign = string.IsNullOrEmpty(callsign) ? "N/A" : callsign;
                        _globalData.simbrief.orig_wx.metar = liveOrigMetar;
                        _globalData.simbrief.orig_wx.metar_zh = TranslateMetarToChinese(liveOrigMetar);
                        _globalData.simbrief.dest_wx.metar = liveDestMetar;
                        _globalData.simbrief.dest_wx.metar_zh = TranslateMetarToChinese(liveDestMetar);
                    }
                    if (!string.IsNullOrEmpty(vatsimCid))
                    {
                        try
                        {
                            var vResp = await _httpClient.GetStringAsync("https://data.vatsim.net/v3/vatsim-data.json"); bool isOnline = vResp.Contains($"\"cid\":{vatsimCid}");
                            lock (_globalData)
                            {
                                _globalData.vatsim.online = isOnline;
                                if (isOnline) { _hasVatsimConnected = true; _vatsimDroppedLatch = false; _vatsimOfflineCounter = 0; }
                                else if (_hasVatsimConnected && _globalData.telemetry.flight_phase != "COMPLETE" && _globalData.telemetry.flight_phase != "PREP")
                                {
                                    _vatsimOfflineCounter++; if (_vatsimOfflineCounter >= 2) { _vatsimDroppedLatch = true; _hasVatsimConnected = false; }
                                }
                            }
                        }
                        catch { }
                    }
                    this.Invoke((MethodInvoker)delegate { lblNetStatus.Text = $"☁️ 网络: 航线已同步 | VATSIM: {(_globalData.vatsim.online ? "在线" : "离线")}"; lblNetStatus.ForeColor = Color.LawnGreen; });
                }
                catch (Exception ex) { this.Invoke((MethodInvoker)delegate { lblNetStatus.Text = $"⚠️ 网络: 同步失败 ({ex.Message})"; lblNetStatus.ForeColor = Color.Crimson; }); }

                for (int i = 0; i < 60; i++) { if (_forceApiFetch) break; await Task.Delay(1000); }
            }
        }
    }
}