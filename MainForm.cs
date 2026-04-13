using System;
using System.Drawing;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using MinimalGCS.Connection;
using MinimalGCS.Mavlink;

namespace MinimalGCS
{
    public partial class MainForm : Form
    {
        private AutoConnector _connector;
        private FlowLayoutPanel _workArea;
        private Label _lblSearching;
        private Dictionary<byte, AgriWorkPanel> _workPanels = new Dictionary<byte, AgriWorkPanel>();

        public MainForm()
        {
            InitializeComponent();
            SetupAgriUI();
            _connector = new AutoConnector();
            _connector.OnDeviceConnected += OnDeviceConnected;
            _connector.Start();
        }

        private void SetupAgriUI()
        {
            this.Text = "Agri-Drone Pro v1.0.5 - Prince Tagadiya";
            this.Width = 420;
            this.Height = 650;
            this.BackColor = Color.White;

            _workArea = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(20),
                BackColor = Color.FromArgb(245, 245, 245)
            };
            this.Controls.Add(_workArea);
            _workArea.BringToFront();

            _lblSearching = new Label { 
                Text = "PREPARING SYSTEM...\nSearching for drones...", 
                Size = new Size(360, 100), TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Color.DarkGray,
                Location = new Point(0, 0)
            };
            _workArea.Controls.Add(_lblSearching);

            btnSmartScan.Visible = cmbDrones.Visible = btnConnect.Visible = lblStatus.Visible = cmbActiveDrone.Visible = groupControl.Visible = false;
            lblWatermark.BringToFront();
        }

        private void OnDeviceConnected(DiscoveredDevice device)
        {
            this.Invoke((Action)(() =>
            {
                if (_lblSearching != null) { _workArea.Controls.Remove(_lblSearching); _lblSearching = null; }

                if (!_workPanels.ContainsKey(device.SysId))
                {
                    var panel = new AgriWorkPanel(device, this);
                    _workPanels[device.SysId] = panel;
                    _workArea.Controls.Add(panel);
                    
                    var parser = new MavLinkParser();
                    parser.PacketReceived += (p) => { if (_workPanels.TryGetValue(device.SysId, out var pnl)) pnl.ProcessMavLink(p); };
                    device.Interface.StartReading(data => parser.Parse(data));
                }
            }));
        }

        public string GetModeName(uint mode) => mode switch { 0 => "STABILIZE", 3 => "AUTO", 4 => "GUIDED", 5 => "LOITER", 6 => "RTL", 9 => "LAND", _ => $"MODE({mode})" };

        // --- STABLE AGRI WORK PANEL ---
        public class AgriWorkPanel : Panel
        {
            private DiscoveredDevice _device;
            private MainForm _main;
            private Label _lblDroneTitle, _lblWorkStatus, _lblTelemetry, _lblMsg;
            private Button _btnStart, _btnPause, _btnResume, _btnRTL, _btnLand, _btnEmergency;
            
            private enum WorkState { IDLE, STARTING, WORKING, PAUSED, RETURNING, EMERGENCY }
            private WorkState _state = WorkState.IDLE;
            private bool _isArmed = false;
            private uint _currentMode = 0;
            private byte _gpsFix = 0;
            private float _alt = 0;
            private DateTime _lastHeartbeat = DateTime.Now;
            private int _gpsPacketCount = 0;

            public AgriWorkPanel(DiscoveredDevice device, MainForm main)
            {
                _device = device; _main = main;
                this.Size = new Size(360, 520);
                this.BorderStyle = BorderStyle.FixedSingle;
                this.BackColor = Color.White;
                this.Margin = new Padding(0, 0, 0, 15);
                InitializeAgriControls();
            }

            private void InitializeAgriControls()
            {
                _lblDroneTitle = new Label { Text = $"DRONE #{_device.SysId}", Location = new Point(10, 10), Size = new Size(340, 25), Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Color.Green };
                _lblWorkStatus = new Label { Text = "Status: READY", Location = new Point(10, 40), Size = new Size(340, 30), Font = new Font("Segoe UI", 16, FontStyle.Bold), ForeColor = Color.Blue };
                _lblTelemetry = new Label { Text = "GPS: Waiting...", Location = new Point(10, 75), Size = new Size(340, 20), Font = new Font("Segoe UI", 9, FontStyle.Bold) };
                _lblMsg = new Label { Text = "System Ready", Location = new Point(10, 100), Size = new Size(340, 40), Font = new Font("Segoe UI", 9, FontStyle.Italic), ForeColor = Color.DarkSlateGray };

                _btnStart = CreateWorkButton("START WORK", Color.FromArgb(40, 167, 69), 150);
                _btnPause = CreateWorkButton("PAUSE", Color.FromArgb(255, 193, 7), 210);
                _btnResume = CreateWorkButton("RESUME", Color.FromArgb(23, 162, 184), 210);
                _btnRTL = CreateWorkButton("RETURN TO HOME", Color.FromArgb(108, 117, 125), 270);
                _btnLand = CreateWorkButton("LAND NOW", Color.FromArgb(255, 69, 0), 330);
                _btnEmergency = CreateWorkButton("EMERGENCY STOP", Color.Red, 390);

                _btnStart.Click += async (s, e) => await StartMissionAsync();
                _btnPause.Click += (s, e) => { SetMode(5); _state = WorkState.PAUSED; UpdateUIState(); };
                _btnResume.Click += (s, e) => { SetMode(3); _state = WorkState.WORKING; UpdateUIState(); };
                _btnRTL.Click += (s, e) => { SetMode(6); _state = WorkState.RETURNING; UpdateUIState(); };
                _btnLand.Click += (s, e) => { SetMode(9); _state = WorkState.RETURNING; UpdateUIState(); };
                _btnEmergency.Click += (s, e) => EmergencyStop();

                this.Controls.AddRange(new Control[] { _lblDroneTitle, _lblWorkStatus, _lblTelemetry, _lblMsg, _btnStart, _btnPause, _btnResume, _btnRTL, _btnLand, _btnEmergency });
                UpdateUIState();
            }

            private Button CreateWorkButton(string text, Color color, int y)
            {
                var btn = new Button { Text = text, Location = new Point(20, y), Size = new Size(320, 50), BackColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 11, FontStyle.Bold), Cursor = Cursors.Hand };
                btn.FlatAppearance.BorderSize = 0; return btn;
            }

            public void ProcessMavLink(MavLinkPacket pkt)
            {
                if (pkt.MessageId == MavLinkMessages.HEARTBEAT_ID)
                {
                    _lastHeartbeat = DateTime.Now;
                    _currentMode = BitConverter.ToUInt32(pkt.Payload, 0);
                    _isArmed = (pkt.Payload[6] & 128) != 0;
                    _main.Invoke((Action)(() => {
                        string fixText = _gpsFix switch {
                            3 => "3D FIX OK",
                            4 => "DGPS FIXED",
                            5 => "RTK FLOAT",
                            6 => "RTK FIXED",
                            _ => "SEARCHING..."
                        };
                        _lblTelemetry.Text = $"GPS: {fixText} | Alt: {_alt:F1}m | {_main.GetModeName(_currentMode)} | {(_isArmed ? "ARMED" : "DISARMED")}";
                        if (_currentMode != 3 && _state == WorkState.WORKING) { _state = WorkState.IDLE; UpdateUIState(); }
                    }));
                }
                else if (pkt.MessageId == 33) { _alt = BitConverter.ToInt32(pkt.Payload, 16) / 1000.0f; }
                else if (pkt.MessageId == 24) 
                { 
                    _gpsPacketCount++;
                    byte f1 = pkt.Payload.Length > 8 ? pkt.Payload[8] : (byte)0;
                    byte f2 = pkt.Payload.Length > 2 ? pkt.Payload[2] : (byte)0;
                    _gpsFix = Math.Max(_gpsFix, Math.Max(f1, f2));
                }
                else if (pkt.MessageId == 124) // GPS2_RAW
                {
                    byte f = pkt.Payload.Length > 8 ? pkt.Payload[8] : (byte)0;
                    _gpsFix = Math.Max(_gpsFix, f);
                }
                else if (pkt.MessageId == 127) // GPS_RTK
                {
                    _gpsFix = 6; // If we get RTK specific packets, assume high level
                }
                else if (pkt.MessageId == 74) // VFR_HUD
                {
                    _alt = BitConverter.ToSingle(pkt.Payload, 8); // alt is float at offset 8 in VFR_HUD
                }
                else if (pkt.MessageId == 253) 
                
                // Watchdog: If no heartbeat > 2s, show disconnected in UI
                if ((DateTime.Now - _lastHeartbeat).TotalSeconds > 2)
                {
                    _main.Invoke((Action)(() => { _lblWorkStatus.Text = "DISCONNECTED"; _lblWorkStatus.ForeColor = Color.Red; }));
                }
            }

            private void UpdateUIState()
            {
                _main.Invoke((Action)(() => {
                    _btnStart.Visible = (_state == WorkState.IDLE);
                    _btnPause.Visible = (_state == WorkState.WORKING);
                    _btnResume.Visible = (_state == WorkState.PAUSED);
                    _btnRTL.Visible = _btnLand.Visible = (_state != WorkState.IDLE);
                    
                    switch(_state) {
                        case WorkState.IDLE: _lblWorkStatus.Text = "READY"; _lblWorkStatus.ForeColor = Color.Blue; break;
                        case WorkState.STARTING: _lblWorkStatus.Text = "STARTING..."; break;
                        case WorkState.WORKING: _lblWorkStatus.Text = "MISSION RUNNING"; _lblWorkStatus.ForeColor = Color.Green; break;
                        case WorkState.PAUSED: _lblWorkStatus.Text = "WORK PAUSED"; _lblWorkStatus.ForeColor = Color.Orange; break;
                        case WorkState.RETURNING: _lblWorkStatus.Text = "RETURNING..."; break;
                    }
                }));
            }

            public async Task StartMissionAsync()
            {
                try
                {
                    if ((DateTime.Now - _lastHeartbeat).TotalSeconds > 2) { MessageBox.Show("No drone heartbeat. Check connection."); return; }
                    if (_gpsFix < 3) { if (MessageBox.Show("No GPS Fix. Fly anyway?", "GPS WARNING", MessageBoxButtons.YesNo) == DialogResult.No) return; }

                    _state = WorkState.STARTING; UpdateUIState();
                    _main.Invoke((Action)(() => _lblMsg.Text = "Step 1: Setting GUIDED mode..."));

                    // STEP 1: Set GUIDED
                    if (!await SetModeAndVerify(4)) { FailMission("Mode change to GUIDED failed."); return; }
                    await Task.Delay(500);

                    // STEP 2: ARM
                    _main.Invoke((Action)(() => _lblMsg.Text = "Step 2: Arming Motors..."));
                    if (!await ArmAndVerify(true)) 
                    {
                        _main.Invoke((Action)(() => _lblMsg.Text = "Retry 1: Arming..."));
                        if (!await ArmAndVerify(true)) { FailMission("Arming failed after retry."); return; }
                    }

                    // STEP 3: STABILIZATION
                    _main.Invoke((Action)(() => _lblMsg.Text = "Step 3: Stabilization (2s)..."));
                    await Task.Delay(2000); // Prevent auto-disarm

                    // STEP 4: Set AUTO
                    _main.Invoke((Action)(() => _lblMsg.Text = "Step 4: Switcing to AUTO mission..."));
                    if (!await SetModeAndVerify(3)) { FailMission("Mode change to AUTO failed."); return; }

                    // STEP 5: MISSION START
                    _main.Invoke((Action)(() => _lblMsg.Text = "Step 5: Starting mission execution..."));
                    SendCmd(300, 1); // MAV_CMD_MISSION_START

                    _state = WorkState.WORKING; 
                    _main.Invoke((Action)(() => _lblMsg.Text = "Mission Active - Safe Flight."));
                    UpdateUIState();
                }
                catch (Exception ex) { FailMission(ex.Message); }
            }

            private void FailMission(string msg)
            {
                _state = WorkState.IDLE;
                UpdateUIState();
                _main.Invoke((Action)(() => _lblMsg.Text = "ERROR: " + msg));
                MessageBox.Show(msg, "Mission Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            private async Task<bool> SetModeAndVerify(uint targetMode)
            {
                int retries = 5;
                while (retries-- > 0)
                {
                    SetMode(targetMode);
                    for (int i = 0; i < 10; i++) { if (_currentMode == targetMode) return true; await Task.Delay(200); }
                }
                return false;
            }

            private async Task<bool> ArmAndVerify(bool arm)
            {
                int retries = 5;
                while (retries-- > 0)
                {
                    SendCmd(400, arm ? 1 : 0, 21196); // Force arm algorithm
                    for (int i = 0; i < 10; i++) { if (_isArmed == arm) return true; await Task.Delay(200); }
                }
                return false;
            }

            private void EmergencyStop()
            {
                if (MessageBox.Show("KILL MOTORS IMMEDIATELY?", "EMERGENCY", MessageBoxButtons.YesNo) == DialogResult.Yes) {
                    SendCmd(400, 0, 21196); _state = WorkState.IDLE; UpdateUIState();
                }
            }

            private void SetMode(uint m) => _device.Interface.Send(MavLinkCommands.CreateSetMode(255, 1, 1, m));
            private void SendCmd(ushort c, float p1, float p2=0, float p3=0, float p4=0, float p5=0, float p6=0, float p7=0) 
                => _device.Interface.Send(MavLinkCommands.CreateCommandLong(255, 1, _device.SysId, _device.CompId, c, p1, p2, p3, p4, p5, p6, p7));
        }
    }
}
