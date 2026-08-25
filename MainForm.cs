using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Fleck;
using SimConnect.NET;
using FSUIPC;

namespace FapmDriver
{
    // ==========================================
    // 1. 数据模型与本地配置模型
    // ==========================================
    public class AppConfig
    {
        public string SimBriefUser { get; set; } = "";
        public string VatsimCid { get; set; } = "";
        public bool VatsimAlarmEnabled { get; set; } = true;
        public int ScreenBrightness { get; set; } = 80;
        public int LedBrightness { get; set; } = 100;
        public int BuzzerVolume { get; set; } = 50;
    }

    public class FlightDataPayload
    {
        public string type { get; set; } = "flight_data";
        public VatsimData vatsim { get; set; } = new();
        public SimBriefData simbrief { get; set; } = new();
        public TelemetryData telemetry { get; set; } = new();
    }
    public class VatsimData { public bool online { get; set; } = false; public string callsign { get; set; } = ""; }
    public class SimBriefData
    {
        public string callsign { get; set; } = "N/A";
        public string origin { get; set; } = "----";
        public string destination { get; set; } = "----";
        public string alternate { get; set; } = "----";
        public AirportWeather orig_wx { get; set; } = new();
        public AirportWeather dest_wx { get; set; } = new();
        public AirportWeather altn_wx { get; set; } = new();
    }
    public class AirportWeather
    {
        public string metar { get; set; } = ""; public string metar_zh { get; set; } = "";
        public string taf { get; set; } = ""; public string taf_zh { get; set; } = "";
        public string notam_preview { get; set; } = "";
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
        public int spoilers { get; set; }
        public int wp_dist { get; set; }
        public int fuel_kg { get; set; }
        public double com1 { get; set; }
        public bool gs_overspeed { get; set; }
        public bool eng1 { get; set; }
        public bool eng2 { get; set; }
        public bool on_ground { get; set; }

        public string flight_phase { get; set; } = "Prep";
        public string active_alarm { get; set; } = "NONE";

        public string sim_time { get; set; } = "00:00:00";
    }

    public enum FlightPhase { Prep, TaxiOut, Climb, Cruise, Descent, Approach, Landing, TaxiIn, Complete }

    // ==========================================
    // 2. 主窗体与核心逻辑
    // ==========================================
    public class MainForm : Form
    {
        private AppConfig _config = new AppConfig();
        private readonly string _configFile = "config.json";

        private TextBox txtSimBrief = null!, txtVatsim = null!;
        private CheckBox chkVatsimAlarm = null!;
        private TrackBar tbScreen = null!, tbLed = null!, tbBuzzer = null!;
        private Label lblScreenVal = null!, lblLedVal = null!, lblBuzzerVal = null!;
        private Label lblSimStatus = null!, lblNetStatus = null!, lblWsStatus = null!;
        private WebSocketServer _wsServer = null!;

        private readonly List<IWebSocketConnection> _clients = new();
        private readonly FlightDataPayload _globalData = new();
        private readonly HttpClient _httpClient = new();

        private FlightPhase _currentPhase = FlightPhase.Prep;

        private bool _wasApConnected = false;
        private bool _apDiscLatch = false;

        private int _vatsimOfflineCounter = 0;
        private bool _hasVatsimConnected = false;
        private bool _vatsimDroppedLatch = false;

        private string _lastHighestAlarm = "NONE";
        private bool _isAlarmMuted = false;

        private bool _forceApiFetch = false;

        public MainForm()
        {
            SetupUI();
            LoadConfig();

            this.Load += (s, e) =>
            {
                CheckAndInjectWasmModule();
                StartWebSocketServer();
                Task.Run(UdpBroadcastLoop);
                Task.Run(SimConnectLoop);
                Task.Run(ExternalApiLoop);
            };
        }

        private void CheckAndInjectWasmModule()
        {
            string moduleName = "fsuipc-lvar-module";
            string sourcePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, moduleName);
            string communityPath = GetCommunityPath();
            if (string.IsNullOrEmpty(communityPath)) return;
            string targetPath = Path.Combine(communityPath, moduleName);

            if (!Directory.Exists(targetPath) && Directory.Exists(sourcePath))
            {
                try
                {
                    CopyDirectory(sourcePath, targetPath);
                    MessageBox.Show(
                        "检测到您是首次使用 FAPM-Display。\n\n程序已自动将必要的通信模块(WASM)安装到您的 MSFS 社区文件夹中。\n\n为了使模块生效，如果您当前正在运行游戏，请【重启微软模拟飞行】。",
                        "模块安装成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
            }
        }

        private string GetCommunityPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            string[] cfgPaths = {
                Path.Combine(localAppData, @"Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt"),
                Path.Combine(appData, @"Microsoft Flight Simulator 2024\UserCfg.opt"),
                Path.Combine(localAppData, @"Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\LocalCache\UserCfg.opt"),
                Path.Combine(appData, @"Microsoft Flight Simulator\UserCfg.opt")
            };

            foreach (string cfg in cfgPaths)
            {
                if (File.Exists(cfg))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(cfg);
                        foreach (string line in lines)
                        {
                            if (line.Trim().StartsWith("InstalledPackagesPath"))
                            {
                                string basePath = line.Substring(line.IndexOf('"')).Trim('"');
                                string community = Path.Combine(basePath, "Community");
                                if (Directory.Exists(community)) return community;
                            }
                        }
                    }
                    catch { }
                }
            }
            return "";
        }

        private void CopyDirectory(string sourceDir, string destinationDir)
        {
            var dir = new DirectoryInfo(sourceDir);
            Directory.CreateDirectory(destinationDir);
            foreach (FileInfo file in dir.GetFiles()) file.CopyTo(Path.Combine(destinationDir, file.Name), true);
            foreach (DirectoryInfo subDir in dir.GetDirectories()) CopyDirectory(subDir.FullName, Path.Combine(destinationDir, subDir.Name));
        }

        private Panel CreateCard(int y, int height, string title)
        {
            Panel pnl = new Panel { Bounds = new Rectangle(20, y, 420, height), BackColor = Color.White };
            Label lblTitle = new Label { Text = title, Font = new Font("Microsoft YaHei", 10F, FontStyle.Bold), Location = new Point(15, 12), AutoSize = true, ForeColor = Color.FromArgb(50, 50, 50) };
            Label separator = new Label { Bounds = new Rectangle(15, 40, 390, 1), BackColor = Color.FromArgb(235, 235, 235) };
            pnl.Controls.Add(lblTitle); pnl.Controls.Add(separator);
            return pnl;
        }

        private void SetupUI()
        {
            this.Text = "FAPM-Display 服务端";
            // 💡 整体窗口瘦身
            this.Size = new Size(480, 560);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(243, 244, 246);
            this.Font = new Font("Microsoft YaHei", 9F);

            // 💡 第一卡片：稍微加高容纳按钮
            Panel pnlAccount = CreateCard(20, 150, "网络账号配置");
            pnlAccount.Controls.Add(new Label { Text = "SimBrief ID:", Bounds = new Rectangle(15, 55, 90, 25), ForeColor = Color.FromArgb(100, 100, 100) });
            txtSimBrief = new TextBox { Bounds = new Rectangle(110, 52, 290, 25), BorderStyle = BorderStyle.FixedSingle };
            pnlAccount.Controls.Add(txtSimBrief);

            pnlAccount.Controls.Add(new Label { Text = "VATSIM CID:", Bounds = new Rectangle(15, 85, 90, 25), ForeColor = Color.FromArgb(100, 100, 100) });
            txtVatsim = new TextBox { Bounds = new Rectangle(110, 82, 290, 25), BorderStyle = BorderStyle.FixedSingle };
            pnlAccount.Controls.Add(txtVatsim);

            // 💡 将复选框缩窄，右侧留给保存按钮
            chkVatsimAlarm = new CheckBox { Bounds = new Rectangle(110, 112, 180, 25), Text = "开启 VATSIM 离线报警", ForeColor = Color.FromArgb(100, 100, 100) };
            chkVatsimAlarm.CheckedChanged += (s, e) => { _config.VatsimAlarmEnabled = chkVatsimAlarm.Checked; };
            pnlAccount.Controls.Add(chkVatsimAlarm);

            // 💡 新的精简版“保存”按钮，和复选框同一行
            Button btnSave = new Button
            {
                Text = "保存",
                Bounds = new Rectangle(300, 110, 100, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += (s, e) => SaveConfig();
            pnlAccount.Controls.Add(btnSave);

            this.Controls.Add(pnlAccount);

            // 💡 第二卡片：坐标上提
            Panel pnlHw = CreateCard(185, 170, "设备参数同步调节");
            pnlHw.Controls.Add(new Label { Text = "屏幕背光:", Bounds = new Rectangle(15, 55, 80, 25), ForeColor = Color.FromArgb(100, 100, 100) });
            tbScreen = new TrackBar { Bounds = new Rectangle(90, 50, 270, 30), Maximum = 100, TickStyle = TickStyle.None };
            lblScreenVal = new Label { Text = "80%", Bounds = new Rectangle(365, 55, 40, 25), ForeColor = Color.DodgerBlue, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold) };
            pnlHw.Controls.Add(tbScreen); pnlHw.Controls.Add(lblScreenVal);

            pnlHw.Controls.Add(new Label { Text = "警告灯亮度:", Bounds = new Rectangle(15, 95, 80, 25), ForeColor = Color.FromArgb(100, 100, 100) });
            tbLed = new TrackBar { Bounds = new Rectangle(90, 90, 270, 30), Maximum = 100, TickStyle = TickStyle.None };
            lblLedVal = new Label { Text = "100%", Bounds = new Rectangle(365, 95, 40, 25), ForeColor = Color.DodgerBlue, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold) };
            pnlHw.Controls.Add(tbLed); pnlHw.Controls.Add(lblLedVal);

            pnlHw.Controls.Add(new Label { Text = "蜂鸣器音量:", Bounds = new Rectangle(15, 135, 80, 25), ForeColor = Color.FromArgb(100, 100, 100) });
            tbBuzzer = new TrackBar { Bounds = new Rectangle(90, 130, 270, 30), Maximum = 100, TickStyle = TickStyle.None };
            lblBuzzerVal = new Label { Text = "50%", Bounds = new Rectangle(365, 135, 40, 25), ForeColor = Color.DodgerBlue, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold) };
            pnlHw.Controls.Add(tbBuzzer); pnlHw.Controls.Add(lblBuzzerVal);

            // 实时滚动：发给硬件看效果
            EventHandler onSliderChanged = (s, e) =>
            {
                lblScreenVal.Text = tbScreen.Value + "%"; lblLedVal.Text = tbLed.Value + "%"; lblBuzzerVal.Text = tbBuzzer.Value + "%";
                SendHardwareConfig();
            };
            tbScreen.Scroll += onSliderChanged; tbLed.Scroll += onSliderChanged; tbBuzzer.Scroll += onSliderChanged;

            // 💡 鼠标松开：静默写入配置文件
            EventHandler onSliderReleased = (s, e) =>
            {
                _config.ScreenBrightness = tbScreen.Value;
                _config.LedBrightness = tbLed.Value;
                _config.BuzzerVolume = tbBuzzer.Value;
                File.WriteAllText(_configFile, JsonSerializer.Serialize(_config));
            };
            tbScreen.MouseCaptureChanged += onSliderReleased;
            tbLed.MouseCaptureChanged += onSliderReleased;
            tbBuzzer.MouseCaptureChanged += onSliderReleased;

            this.Controls.Add(pnlHw);

            // 💡 第三卡片：坐标上提
            Panel pnlStatus = CreateCard(370, 140, "系统状态面板");
            lblSimStatus = new Label { Text = "正在等待连接...", Bounds = new Rectangle(15, 55, 390, 20), ForeColor = Color.Gray };
            lblNetStatus = new Label { Text = "待机中...", Bounds = new Rectangle(15, 80, 390, 20), ForeColor = Color.Gray };
            lblWsStatus = new Label { Text = "UDP 广播寻址中，等待设备接入...", Bounds = new Rectangle(15, 105, 390, 20), ForeColor = Color.Gray };
            pnlStatus.Controls.Add(lblSimStatus); pnlStatus.Controls.Add(lblNetStatus); pnlStatus.Controls.Add(lblWsStatus);
            this.Controls.Add(pnlStatus);
        }

        private void LoadConfig()
        {
            if (File.Exists(_configFile))
            {
                try
                {
                    string json = File.ReadAllText(_configFile);
                    _config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
                }
                catch { }
            }
            txtSimBrief.Text = _config.SimBriefUser; txtVatsim.Text = _config.VatsimCid;
            chkVatsimAlarm.Checked = _config.VatsimAlarmEnabled;
            tbScreen.Value = _config.ScreenBrightness; tbLed.Value = _config.LedBrightness; tbBuzzer.Value = _config.BuzzerVolume;
            lblScreenVal.Text = _config.ScreenBrightness + "%"; lblLedVal.Text = _config.LedBrightness + "%"; lblBuzzerVal.Text = _config.BuzzerVolume + "%";
        }

        private void SaveConfig()
        {
            _config.SimBriefUser = txtSimBrief.Text.Trim(); _config.VatsimCid = txtVatsim.Text.Trim();
            _config.VatsimAlarmEnabled = chkVatsimAlarm.Checked;
            File.WriteAllText(_configFile, JsonSerializer.Serialize(_config));

            lblNetStatus.Text = "☁️ 网络: 正在拉取最新数据...";
            lblNetStatus.ForeColor = Color.DarkOrange;
            _forceApiFetch = true;
        }

        private void SendHardwareConfig()
        {
            var hwMsg = new { type = "hw_config", screen_brightness = tbScreen.Value, warn_led_brightness = tbLed.Value, buzzer_volume = tbBuzzer.Value };
            string json = JsonSerializer.Serialize(hwMsg);
            lock (_clients) { foreach (var client in _clients) if (client.IsAvailable) client.Send(json); }
        }

        private async Task UdpBroadcastLoop()
        {
            using var udpClient = new UdpClient(); udpClient.EnableBroadcast = true;
            var endPoint = new IPEndPoint(IPAddress.Broadcast, 8766);
            byte[] bytes = Encoding.UTF8.GetBytes("FAPM_DISCOVER:8765");
            while (true)
            {
                try { await udpClient.SendAsync(bytes, bytes.Length, endPoint); } catch { }
                await Task.Delay(2000);
            }
        }

        private void StartWebSocketServer()
        {
            FleckLog.Level = LogLevel.Error;
            _wsServer = new WebSocketServer("ws://0.0.0.0:8765");
            _wsServer.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    lock (_clients) _clients.Add(socket);
                    this.Invoke((MethodInvoker)delegate { lblWsStatus.Text = $"🔌 设备已连接 ({socket.ConnectionInfo.ClientIpAddress})"; lblWsStatus.ForeColor = Color.MediumSeaGreen; });
                    SendHardwareConfig();
                };
                socket.OnClose = () =>
                {
                    lock (_clients) _clients.Remove(socket);
                    this.Invoke((MethodInvoker)delegate { lblWsStatus.Text = "📡 设备已断开，UDP 广播寻址中..."; lblWsStatus.ForeColor = Color.DimGray; });
                };
                socket.OnMessage = message =>
                {
                    try
                    {
                        var doc = JsonDocument.Parse(message);
                        if (doc.RootElement.TryGetProperty("action", out var actionProp))
                        {
                            if (actionProp.GetString() == "alarm_ack")
                            {
                                _isAlarmMuted = true;
                            }
                        }
                    }
                    catch { }
                };
            });
        }

        private async Task SimConnectLoop()
        {
            var client = new SimConnectClient();
            bool isSimConnected = false;

            while (true)
            {
                if (File.Exists("debug.json"))
                {
                    try
                    {
                        string mockJson = File.ReadAllText("debug.json");
                        var mockData = JsonSerializer.Deserialize<FlightDataPayload>(mockJson);
                        if (mockData != null) { lock (_clients) { foreach (var c in _clients) if (c.IsAvailable) c.Send(mockJson); } }
                        this.Invoke((MethodInvoker)delegate { lblSimStatus.Text = "🛠️ 调试模式: 正在循环下发 debug.json 覆盖数据"; lblSimStatus.ForeColor = Color.DarkOrange; });
                    }
                    catch { }
                    await Task.Delay(1000);
                    continue;
                }

                if (!isSimConnected)
                {
                    try
                    {
                        await client.ConnectAsync();
                        MSFSVariableServices.Init(); MSFSVariableServices.Start();
                        isSimConnected = true;
                        this.Invoke((MethodInvoker)delegate { lblSimStatus.Text = "✈️ MSFS: 已连接 (底层数据通道就绪)"; lblSimStatus.ForeColor = Color.MediumSeaGreen; });
                    }
                    catch { await Task.Delay(2000); continue; }
                }

                try
                {
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

                    double spoilers = await client.SimVars.GetAsync<double>("SPOILERS HANDLE POSITION", "Percent");
                    double wpDist = await client.SimVars.GetAsync<double>("GPS WP DISTANCE", "Nautical miles");
                    double fuelLbs = await client.SimVars.GetAsync<double>("FUEL TOTAL QUANTITY WEIGHT", "Pounds");
                    double com1 = await client.SimVars.GetAsync<double>("COM ACTIVE FREQUENCY:1", "MHz");
                    double zulu = await client.SimVars.GetAsync<double>("ZULU TIME", "seconds");

                    bool fenixAp1 = GetLVarValue("I_FCU_AP1") > 0.5; bool fenixAp2 = GetLVarValue("I_FCU_AP2") > 0.5;
                    bool fenixAthr = GetLVarValue("I_FCU_ATHR") > 0.5;
                    bool fenixWarning = GetLVarValue("I_MIP_MASTER_WARNING_CAPT") > 0.5;
                    bool fenixCaution = GetLVarValue("I_MIP_MASTER_CAUTION_CAPT") > 0.5;
                    bool isApConnected = fenixAp1 || fenixAp2;
                    bool enginesRunning = eng1 > 0.5 || eng2 > 0.5;
                    int vs = (int)Math.Round(vsFps * 60.0);

                    switch (_currentPhase)
                    {
                        case FlightPhase.Prep:
                            if (enginesRunning && onGround > 0.5) _currentPhase = FlightPhase.TaxiOut;
                            break;
                        case FlightPhase.TaxiOut:
                            if (onGround < 0.5) _currentPhase = FlightPhase.Climb;
                            break;
                        case FlightPhase.Climb:
                            if (vs > -100 && vs < 100) _currentPhase = FlightPhase.Cruise;
                            else if (altRadio < 5000 && flaps > 0) _currentPhase = FlightPhase.Approach;
                            break;
                        case FlightPhase.Cruise:
                            if (vs < -500) _currentPhase = FlightPhase.Descent;
                            break;
                        case FlightPhase.Descent:
                            if (altRadio < 5000 && flaps > 0) _currentPhase = FlightPhase.Approach;
                            break;
                        case FlightPhase.Approach:
                            if (onGround > 0.5 && gs > 30) _currentPhase = FlightPhase.Landing;
                            break;
                        case FlightPhase.Landing:
                            if (onGround > 0.5 && gs <= 30) _currentPhase = FlightPhase.TaxiIn;
                            break;
                        case FlightPhase.TaxiIn:
                            if (!enginesRunning && onGround > 0.5) _currentPhase = FlightPhase.Complete;
                            break;
                        case FlightPhase.Complete:
                            if (enginesRunning) _currentPhase = FlightPhase.Prep;
                            break;
                    }

                    if (isApConnected) _apDiscLatch = false;
                    else if (_wasApConnected && !isApConnected && _currentPhase == FlightPhase.Cruise) _apDiscLatch = true;
                    _wasApConnected = isApConnected;

                    bool condStall = stall > 0.5;
                    bool condOverspeed = overspeed > 0.5;
                    bool condApDisc = _apDiscLatch;
                    bool condWarning = (_currentPhase == FlightPhase.Climb || _currentPhase == FlightPhase.Cruise || _currentPhase == FlightPhase.Descent) && fenixWarning;
                    bool condCaution = (_currentPhase == FlightPhase.Climb || _currentPhase == FlightPhase.Cruise || _currentPhase == FlightPhase.Descent) && fenixCaution;
                    bool condGear = (onGround < 0.5 && altRadio < 1000 && gearHandle < 0.5);

                    bool condVatsim = _config.VatsimAlarmEnabled && !string.IsNullOrWhiteSpace(_config.VatsimCid) && (
                        (_currentPhase == FlightPhase.TaxiOut && !_globalData.vatsim.online && !_hasVatsimConnected) ||
                        (_vatsimDroppedLatch)
                    );

                    string currentHighestAlarm = "NONE";
                    if (condStall) currentHighestAlarm = "STALL";
                    else if (condOverspeed) currentHighestAlarm = "OVERSPEED";
                    else if (condApDisc) currentHighestAlarm = "AP_DISC";
                    else if (condWarning) currentHighestAlarm = "WARNING";
                    else if (condCaution) currentHighestAlarm = "CAUTION";
                    else if (condGear) currentHighestAlarm = "GEAR";
                    else if (condVatsim) currentHighestAlarm = "VATSIM";

                    if (currentHighestAlarm != _lastHighestAlarm && currentHighestAlarm != "NONE")
                    {
                        _isAlarmMuted = false;
                    }
                    _lastHighestAlarm = currentHighestAlarm;

                    int totalSecs = (int)zulu % 86400;

                    lock (_globalData)
                    {
                        _globalData.telemetry.ias = (int)Math.Round(ias); _globalData.telemetry.mach = Math.Round(mach, 3);
                        _globalData.telemetry.gs = (int)Math.Round(gs); _globalData.telemetry.alt_baro = (int)Math.Round(altBaro);
                        _globalData.telemetry.alt_radio = (int)Math.Round(altRadio); _globalData.telemetry.vs = vs;
                        _globalData.telemetry.gear_down = gearHandle > 0.5; _globalData.telemetry.spoilers = (int)Math.Round(spoilers);
                        _globalData.telemetry.wp_dist = (int)Math.Round(wpDist); _globalData.telemetry.fuel_kg = (int)Math.Round(fuelLbs / 2.2046);
                        _globalData.telemetry.com1 = Math.Round(com1, 3); _globalData.telemetry.ap1 = fenixAp1;
                        _globalData.telemetry.ap2 = fenixAp2; _globalData.telemetry.athr = fenixAthr;

                        _globalData.telemetry.gs_overspeed = (_currentPhase == FlightPhase.TaxiOut || _currentPhase == FlightPhase.TaxiIn) && gs > 28;
                        _globalData.telemetry.eng1 = eng1 > 0.5; _globalData.telemetry.eng2 = eng2 > 0.5; _globalData.telemetry.on_ground = onGround > 0.5;
                        _globalData.telemetry.flight_phase = _currentPhase.ToString();

                        _globalData.telemetry.active_alarm = _isAlarmMuted ? "NONE" : currentHighestAlarm;

                        _globalData.telemetry.sim_time = $"{totalSecs / 3600:D2}:{(totalSecs % 3600) / 60:D2}:{totalSecs % 60:D2}";
                    }

                    string json;
                    lock (_globalData) { json = JsonSerializer.Serialize(_globalData); }
                    lock (_clients) { foreach (var c in _clients) if (c.IsAvailable) c.Send(json); }

                }
                catch (Exception ex)
                {
                    this.Invoke((MethodInvoker)delegate { lblSimStatus.Text = $"⚠️ MSFS: 异常断开 ({ex.Message})"; lblSimStatus.ForeColor = Color.Crimson; });
                    isSimConnected = false;
                }
                await Task.Delay(100);
            }
        }

        private static double GetLVarValue(string lvarName)
        {
            if (MSFSVariableServices.LVars.Exists(lvarName)) return MSFSVariableServices.LVars[lvarName].Value;
            if (MSFSVariableServices.LVars.Exists("L:" + lvarName)) return MSFSVariableServices.LVars["L:" + lvarName].Value;
            return 0.0;
        }

        private static async Task<string> GetVatsimMetar(string icao)
        {
            try { return (await new HttpClient().GetStringAsync($"https://metar.vatsim.net/{icao}")).Trim(); } catch { return ""; }
        }

        private static async Task<string> GetNoaaTaf(string icao)
        {
            try { return (await new HttpClient().GetStringAsync($"https://aviationweather.gov/api/data/taf?ids={icao}")).Trim(); } catch { return ""; }
        }

        private static string SafeGetJsonString(JsonElement element, string propName)
        {
            try { if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String) return prop.GetString() ?? ""; } catch { }
            return "";
        }

        private static string SafeExtractNotam(JsonElement notamsElement, string propName)
        {
            try
            {
                if (notamsElement.ValueKind == JsonValueKind.Object && notamsElement.TryGetProperty(propName, out var targetAirport))
                {
                    if (targetAirport.TryGetProperty("notam", out var notamArray) && notamArray.ValueKind == JsonValueKind.Array && notamArray.GetArrayLength() > 0)
                    {
                        string firstNotam = notamArray[0].GetProperty("message").GetString() ?? "";
                        if (firstNotam.Length > 250) return firstNotam.Substring(0, 250) + "... [已截断]";
                        return firstNotam;
                    }
                }
            }
            catch { }
            return "暂无通告";
        }

        private static string TranslateMetarToChinese(string rawMetar)
        {
            if (string.IsNullOrEmpty(rawMetar)) return ""; string zh = rawMetar;
            var dict = new Dictionary<string, string> { { " NOSIG", " [无显著变化]" }, { " CAVOK", " [能见度极佳]" }, { " RA", " [降雨]" }, { " +RA", " [大雨]" }, { " -RA", " [小雨]" }, { " FG", " [大雾]" }, { " BR", " [薄雾]" }, { " TS", " [雷暴]" }, { " WS ", " [风切变] " } };
            foreach (var kvp in dict) zh = zh.Replace(kvp.Key, kvp.Value);
            return zh.Trim();
        }

        private static string TranslateTafToChinese(string rawTaf)
        {
            if (string.IsNullOrEmpty(rawTaf)) return ""; string zh = rawTaf;
            zh = zh.Replace("TAF AMD", "[修正预报]").Replace("TAF", "[预报]").Replace("BECMG", "\n👉 [逐步变为]").Replace("TEMPO", "\n⚡ [短时波动]").Replace("NSW", "[天气现象结束]");
            return TranslateMetarToChinese(zh);
        }

        private async Task ExternalApiLoop()
        {
            while (true)
            {
                _forceApiFetch = false;

                if (File.Exists("debug.json"))
                {
                    this.Invoke((MethodInvoker)delegate { lblNetStatus.Text = "🛠️ 调试模式: 外部网络 API 请求已暂停"; lblNetStatus.ForeColor = Color.DarkOrange; });
                    await Task.Delay(2000); continue;
                }

                string simBriefUser = _config.SimBriefUser; string vatsimCid = _config.VatsimCid;
                if (string.IsNullOrEmpty(simBriefUser))
                {
                    this.Invoke((MethodInvoker)delegate { lblNetStatus.Text = "☁️ 网络: 待机 (未配置 SimBrief 账号)"; lblNetStatus.ForeColor = Color.DimGray; });

                    for (int i = 0; i < 5; i++)
                    {
                        if (_forceApiFetch) break;
                        await Task.Delay(1000);
                    }
                    continue;
                }
                try
                {
                    var sbResp = await _httpClient.GetStringAsync($"https://www.simbrief.com/api/xml.fetcher.php?username={simBriefUser}&json=1");
                    using var doc = JsonDocument.Parse(sbResp); var root = doc.RootElement;
                    var origin = root.GetProperty("origin").GetProperty("icao_code").GetString() ?? "----"; var dest = root.GetProperty("destination").GetProperty("icao_code").GetString() ?? "----"; var altn = root.GetProperty("alternate").GetProperty("icao_code").GetString() ?? "----";
                    var callsign = root.GetProperty("general").GetProperty("icao_airline").GetString() + root.GetProperty("general").GetProperty("flight_number").GetString();
                    var wx = root.TryGetProperty("weather", out var w) ? w : default; var notams = root.TryGetProperty("notams", out var n) ? n : default;

                    string liveOrigMetar = await GetVatsimMetar(origin); string liveOrigTaf = await GetNoaaTaf(origin);
                    string liveDestMetar = await GetVatsimMetar(dest); string liveDestTaf = await GetNoaaTaf(dest);
                    string liveAltnMetar = await GetVatsimMetar(altn); string liveAltnTaf = await GetNoaaTaf(altn);

                    lock (_globalData)
                    {
                        _globalData.simbrief.origin = origin; _globalData.simbrief.destination = dest; _globalData.simbrief.alternate = altn; _globalData.simbrief.callsign = string.IsNullOrEmpty(callsign) ? "N/A" : callsign;
                        _globalData.simbrief.orig_wx.metar = string.IsNullOrEmpty(liveOrigMetar) ? SafeGetJsonString(wx, "orig_metar") : liveOrigMetar; _globalData.simbrief.orig_wx.metar_zh = TranslateMetarToChinese(_globalData.simbrief.orig_wx.metar); _globalData.simbrief.orig_wx.taf = string.IsNullOrEmpty(liveOrigTaf) ? SafeGetJsonString(wx, "orig_taf") : liveOrigTaf; _globalData.simbrief.orig_wx.taf_zh = TranslateTafToChinese(_globalData.simbrief.orig_wx.taf); _globalData.simbrief.orig_wx.notam_preview = SafeExtractNotam(notams, "origin");
                        _globalData.simbrief.dest_wx.metar = string.IsNullOrEmpty(liveDestMetar) ? SafeGetJsonString(wx, "dest_metar") : liveDestMetar; _globalData.simbrief.dest_wx.metar_zh = TranslateMetarToChinese(_globalData.simbrief.dest_wx.metar); _globalData.simbrief.dest_wx.taf = string.IsNullOrEmpty(liveDestTaf) ? SafeGetJsonString(wx, "dest_taf") : liveDestTaf; _globalData.simbrief.dest_wx.taf_zh = TranslateTafToChinese(_globalData.simbrief.dest_wx.taf); _globalData.simbrief.dest_wx.notam_preview = SafeExtractNotam(notams, "destination");
                        _globalData.simbrief.altn_wx.metar = string.IsNullOrEmpty(liveAltnMetar) ? SafeGetJsonString(wx, "altn_metar") : liveAltnMetar; _globalData.simbrief.altn_wx.metar_zh = TranslateMetarToChinese(_globalData.simbrief.altn_wx.metar); _globalData.simbrief.altn_wx.taf = string.IsNullOrEmpty(liveAltnTaf) ? SafeGetJsonString(wx, "altn_taf") : liveAltnTaf; _globalData.simbrief.altn_wx.taf_zh = TranslateTafToChinese(_globalData.simbrief.altn_wx.taf); _globalData.simbrief.altn_wx.notam_preview = SafeExtractNotam(notams, "alternate");
                    }

                    if (!string.IsNullOrEmpty(vatsimCid))
                    {
                        try
                        {
                            var vResp = await _httpClient.GetStringAsync("https://data.vatsim.net/v3/vatsim-data.json");
                            bool isOnline = vResp.Contains($"\"cid\":{vatsimCid}");

                            lock (_globalData)
                            {
                                _globalData.vatsim.online = isOnline;
                                if (isOnline)
                                {
                                    _hasVatsimConnected = true;
                                    _vatsimDroppedLatch = false;
                                    _vatsimOfflineCounter = 0;
                                }
                                else if (_hasVatsimConnected && _currentPhase != FlightPhase.Complete && _currentPhase != FlightPhase.Prep)
                                {
                                    _vatsimOfflineCounter++;
                                    if (_vatsimOfflineCounter >= 2)
                                    {
                                        _vatsimDroppedLatch = true;
                                        _hasVatsimConnected = false;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    this.Invoke((MethodInvoker)delegate { lblNetStatus.Text = $"☁️ 网络: 航线已同步 | VATSIM: {(_globalData.vatsim.online ? "在线" : "离线")}"; lblNetStatus.ForeColor = Color.MediumSeaGreen; });
                }
                catch (Exception ex) { this.Invoke((MethodInvoker)delegate { lblNetStatus.Text = $"⚠️ 网络: 同步失败 ({ex.Message})"; lblNetStatus.ForeColor = Color.Crimson; }); }

                for (int i = 0; i < 60; i++)
                {
                    if (_forceApiFetch) break;
                    await Task.Delay(1000);
                }
            }
        }
    }
}