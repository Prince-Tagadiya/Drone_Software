using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
        private System.Windows.Forms.Timer _uiTicker;
        
        // CENTRAL STATE MANAGER: One dictionary for all drones
        private ConcurrentDictionary<byte, DroneState> _drones = new ConcurrentDictionary<byte, DroneState>();
        private Dictionary<byte, AgriWorkPanel> _panels = new Dictionary<byte, AgriWorkPanel>();

        public MainForm()
        {
            InitializeComponent();
            SetupAgriUI();
            
            _connector = new AutoConnector();
            _connector.OnDeviceConnected += OnDeviceConnected;
            _connector.Start();

            // UI UPDATE TIMER (10Hz) - Reads from stable state
            _uiTicker = new System.Windows.Forms.Timer { Interval = 100 };
            _uiTicker.Tick += (s, e) => UpdateUIFromState();
            _uiTicker.Start();
        }

        private void SetupAgriUI()
        {
            this.Text = "Agri-Drone Enterprise v1.1.0-Refactored";
            this.Width = 420; this.Height = 700;
            this.BackColor = Color.White;

            _workArea = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(240, 240, 240), Padding = new Padding(15) };
            this.Controls.Add(_workArea);
            _workArea.BringToFront();

            _lblSearching = new Label { Text = "SCANNING FOR FLEET...", Size = new Size(380, 100), TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Color.Gray };
            _workArea.Controls.Add(_lblSearching);

            btnSmartScan.Visible = cmbDrones.Visible = btnConnect.Visible = lblStatus.Visible = cmbActiveDrone.Visible = groupControl.Visible = false;
            lblWatermark.BringToFront();
        }

        private void OnDeviceConnected(DiscoveredDevice device)
        {
            this.Invoke((Action)(() =>
            {
                if (_lblSearching != null) { _workArea.Controls.Remove(_lblSearching); _lblSearching = null; }

                if (!_drones.ContainsKey(device.SysId))
                {
                    var state = new DroneState(device.SysId);
                    _drones.TryAdd(device.SysId, state);
                    
                    var panel = new AgriWorkPanel(device, state, this);
                    _panels[device.SysId] = panel;
                    _workArea.Controls.Add(panel);

                    // SINGLE READER PER DEVICE -> Dispatches to Central State
                    var parser = new MavLinkParser();
                    parser.PacketReceived += (pkt) => DispatchPacket(pkt);
                    device.Interface.StartReading(data => parser.Parse(data));
                }
            }));
        }

        private void DispatchPacket(MavLinkPacket pkt)
        {
            if (!_drones.TryGetValue(pkt.SystemId, out var state)) return;

            if (pkt.MessageId == MavLinkMessages.HEARTBEAT_ID)
            {
                state.LastHeartbeat = DateTime.Now;
                state.Mode = BitConverter.ToUInt32(pkt.Payload, 0);
                bool armed = (pkt.Payload[6] & 128) != 0;
                if (state.IsArmed != armed) state.AddLog(armed ? "MOTORS ARMED" : "MOTORS DISARMED");
                state.IsArmed = armed;
            }
            else if (pkt.MessageId == 24) // GPS_RAW_INT
            {
                byte fix = (pkt.Payload.Length > 8) ? pkt.Payload[8] : (byte)0;
                // Stability: Only update if we get a valid fix or if we haven't seen a fix for 5s
                if (fix >= 3 || (DateTime.Now - state.LastHeartbeat).TotalSeconds > 5)
                {
                    state.AddLog($"GPS: {fix}");
                    state.GpsFixType = fix;
                }
            }
            else if (pkt.MessageId == 33) // GLOBAL_POSITION_INT
            {
                state.Alt = BitConverter.ToInt32(pkt.Payload, 16) / 1000.0f;
            }
            else if (pkt.MessageId == 253) // STATUSTEXT
            {
                string msg = System.Text.Encoding.ASCII.GetString(pkt.Payload, 1, pkt.Payload.Length - 1).TrimEnd('\0');
                state.AddLog(msg);
            }
        }

        private void UpdateUIFromState()
        {
            foreach (var kvp in _panels)
            {
                if (_drones.TryGetValue(kvp.Key, out var state)) kvp.Value.SyncWithState(state);
            }
        }

        public string GetModeName(uint mode) => mode switch { 0 => "STABILIZE", 3 => "AUTO", 4 => "GUIDED", 5 => "LOITER", 6 => "RTL", 9 => "LAND", _ => $"MODE({mode})" };

        // --- MANAGES ONE DRONE'S UI AND COMMANDS ---
        public class AgriWorkPanel : Panel
        {
            private DiscoveredDevice _device;
            private DroneState _state;
            private MainForm _main;
            private Label _lblStatus, _lblTelemetry, _lblMsg;
            private Button _btnStart, _btnPause, _btnResume, _btnRTL, _btnLand, _btnEmergency;
            
            private enum PanelState { IDLE, BUSY }
            private PanelState _pState = PanelState.IDLE;

            public AgriWorkPanel(DiscoveredDevice device, DroneState state, MainForm main)
            {
                _device = device; _state = state; _main = main;
                this.Size = new Size(360, 520); this.BorderStyle = BorderStyle.FixedSingle; this.BackColor = Color.White; this.Margin = new Padding(0, 0, 0, 15);
                InitializeControls();
            }

            private void InitializeControls()
            {
                var lblTitle = new Label { Text = $"DRONE #{_state.SysId}", Location = new Point(10, 10), Size = new Size(340, 25), Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Color.DarkGreen };
                _lblStatus = new Label { Text = "READY", Location = new Point(10, 40), Size = new Size(340, 30), Font = new Font("Segoe UI", 16, FontStyle.Bold), ForeColor = Color.Blue };
                _lblTelemetry = new Label { Text = "...", Location = new Point(10, 75), Size = new Size(340, 20), Font = new Font("Segoe UI", 10, FontStyle.Bold) };
                _lblMsg = new Label { Text = "Initializing...", Location = new Point(10, 100), Size = new Size(340, 40), Font = new Font("Segoe UI", 9, FontStyle.Italic), ForeColor = Color.DarkSlateGray };

                _btnStart = CreateBtn("START MISSION", Color.FromArgb(40, 167, 69), 150);
                _btnPause = CreateBtn("PAUSE", Color.FromArgb(255, 193, 7), 210);
                _btnResume = CreateBtn("RESUME", Color.FromArgb(23, 162, 184), 210);
                _btnRTL = CreateBtn("RETURN HOME (RTL)", Color.FromArgb(108, 117, 125), 270);
                _btnLand = CreateBtn("LAND NOW", Color.FromArgb(255, 69, 0), 330);
                _btnEmergency = CreateBtn("EMERGENCY STOP", Color.Red, 390);

                _btnStart.Click += async (s, e) => await CommandStartMission();
                _btnPause.Click += (s, e) => SendSetMode(5);
                _btnResume.Click += (s, e) => SendSetMode(3);
                _btnRTL.Click += (s, e) => SendSetMode(6);
                _btnLand.Click += (s, e) => SendSetMode(9);
                _btnEmergency.Click += (s, e) => { SendCmd(400, 0, 21196); _pState = PanelState.IDLE; };

                this.Controls.AddRange(new Control[] { lblTitle, _lblStatus, _lblTelemetry, _lblMsg, _btnStart, _btnPause, _btnResume, _btnRTL, _btnLand, _btnEmergency });
            }

            public void SyncWithState(DroneState state)
            {
                _lblMsg.Text = state.LastMessage;
                string gps = state.GpsFixType switch { 3 => "3D OK", 4 => "DGPS", 5 => "RTK-F", 6 => "RTK-FIX", _ => "SEARCHING" };
                _lblTelemetry.Text = $"GPS: {gps} | Alt: {state.Alt:F1}m | {_main.GetModeName(state.Mode)} | {(state.IsArmed ? "ARMED" : "DISARMED")}";
                
                if (!state.IsConnected) { _lblStatus.Text = "LOST CONNECTION"; _lblStatus.ForeColor = Color.Red; return; }
                
                if (_pState == PanelState.IDLE)
                {
                    _lblStatus.Text = state.Mode == 3 ? "MISSION ACTIVE" : "READY";
                    _lblStatus.ForeColor = state.Mode == 3 ? Color.Green : Color.Blue;
                }

                _btnStart.Visible = (state.Mode != 3 && _pState == PanelState.IDLE);
                _btnPause.Visible = (state.Mode == 3);
                _btnResume.Visible = (state.Mode == 5);
            }

            private async Task CommandStartMission()
            {
                _pState = PanelState.BUSY;
                _lblStatus.Text = "STARTING..."; _lblStatus.ForeColor = Color.Orange;

                // Step 1: Set GUIDED
                if (!await ExecuteStep("Setting Mode: GUIDED", () => SendSetMode(4), () => _state.Mode == 4)) return;
                
                // Step 2: ARM
                if (!_state.IsArmed)
                {
                    if (!await ExecuteStep("Arming Motors...", () => SendCmd(400, 1, 21196), () => _state.IsArmed)) return;
                }

                // Step 3: Wait for stabilization
                _state.AddLog("Stabilizing (2s)...");
                await Task.Delay(2000);

                // Step 4: Set AUTO
                if (!await ExecuteStep("Switching to AUTO...", () => SendSetMode(3), () => _state.Mode == 3)) return;

                // Step 5: Start Mission
                SendCmd(300, 1);
                _state.AddLog("Mission Executed Successfully");
                _pState = PanelState.IDLE;
            }

            private async Task<bool> ExecuteStep(string log, Action send, Func<bool> verify)
            {
                _state.AddLog(log);
                int retries = 5;
                while (retries-- > 0)
                {
                    send();
                    for (int i = 0; i < 15; i++) { if (verify()) return true; await Task.Delay(200); }
                }
                _state.AddLog("Step Failed: Timeout");
                _pState = PanelState.IDLE;
                return false;
            }

            private Button CreateBtn(string t, Color c, int y)
            {
                var b = new Button { Text = t, Location = new Point(20, y), Size = new Size(320, 50), BackColor = c, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 11, FontStyle.Bold), Cursor = Cursors.Hand };
                b.FlatAppearance.BorderSize = 0; return b;
            }

            private void SendSetMode(uint m) => _device.Interface.Send(MavLinkCommands.CreateSetMode(255, 1, 1, m));
            private void SendCmd(ushort c, float p1, float p2=0, float p3=0, float p4=0, float p5=0, float p6=0, float p7=0) 
                => _device.Interface.Send(MavLinkCommands.CreateCommandLong(255, 1, _device.SysId, _device.CompId, c, p1, p2, p3, p4, p5, p6, p7));
        }
    }
}
