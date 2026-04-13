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
            this.Text = "Agri-Drone Enterprise v1.1.5 - Prince Tagadiya";
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
                    device.Interface.OnDataReceived += (data) => parser.Parse(data);
                    device.Interface.StartReading();
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
            else if (pkt.MessageId == 42) // MISSION_CURRENT
            {
                state.CurrentWp = BitConverter.ToUInt16(pkt.Payload, 0);
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
                
                // --- SWIPE TO DISARM ---
                var pnlSwipe = new Panel { Location = new Point(20, 390), Size = new Size(320, 60), BackColor = Color.FromArgb(220, 53, 69), BorderStyle = BorderStyle.None };
                var lblSwipe = new Label { Text = ">>> SWIPE TO DISARM >>>", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White, Font = new Font("Segoe UI", 10, FontStyle.Bold), Cursor = Cursors.Hand };
                var pnlHandle = new Panel { Location = new Point(2, 2), Size = new Size(80, 56), BackColor = Color.White, Cursor = Cursors.Hand };
                pnlSwipe.Controls.Add(pnlHandle);
                pnlSwipe.Controls.Add(lblSwipe);
                lblSwipe.SendToBack();

                bool isSwiping = false; int startX = 0;
                pnlHandle.MouseDown += (s, e) => { isSwiping = true; startX = e.X; };
                pnlHandle.MouseMove += (s, e) => 
                {
                    if (!isSwiping) return;
                    int newX = pnlHandle.Left + (e.X - startX);
                    if (newX < 2) newX = 2;
                    if (newX > pnlSwipe.Width - pnlHandle.Width - 2) newX = pnlSwipe.Width - pnlHandle.Width - 2;
                    pnlHandle.Left = newX;
                    if (pnlHandle.Left > pnlSwipe.Width - pnlHandle.Width - 10) 
                    {
                        isSwiping = false; pnlHandle.Left = 2;
                        SendCmd(400, 0, 21196); _pState = PanelState.IDLE;
                        _state.AddLog("EMERGENCY SWIPE TRIPPED!");
                    }
                };
                pnlHandle.MouseUp += (s, e) => { isSwiping = false; pnlHandle.Left = 2; };

                _btnStart.Click += async (s, e) => await CommandStartMission();
                _btnPause.Click += (s, e) => { _state.ResumeWp = _state.CurrentWp; SendSetMode(5); };
                _btnResume.Click += (s, e) => SendSetMode(3);
                _btnRTL.Click += (s, e) => { _state.ResumeWp = _state.CurrentWp; SendSetMode(6); };
                _btnLand.Click += (s, e) => { _state.ResumeWp = _state.CurrentWp; SendSetMode(9); };

                this.Controls.AddRange(new Control[] { lblTitle, _lblStatus, _lblTelemetry, _lblMsg, _btnStart, _btnPause, _btnResume, _btnRTL, _btnLand, pnlSwipe });
            }

            public void SyncWithState(DroneState state)
            {
                _lblMsg.Text = state.LastMessage;
                string gps = state.GpsFixType switch { 3 => "3D OK", 4 => "DGPS", 5 => "RTK-F", 6 => "RTK-FIX", _ => "SEARCHING" };
                _lblTelemetry.Text = $"GPS: {gps} | ALT: {state.Alt:F1}m | WP: {state.CurrentWp} | {_main.GetModeName(state.Mode)}";
                _lblTelemetry.ForeColor = state.IsArmed ? Color.DarkRed : Color.Black;
                
                if (!state.IsConnected) { _lblStatus.Text = "LOST CONNECTION"; _lblStatus.ForeColor = Color.Red; return; }
                
                if (_pState == PanelState.IDLE)
                {
                    if (state.Mode == 3) { _lblStatus.Text = "MISSION ACTIVE"; _lblStatus.ForeColor = Color.Green; }
                    else if (state.Mode == 5) { _lblStatus.Text = "MISSION PAUSED"; _lblStatus.ForeColor = Color.Orange; }
                    else { _lblStatus.Text = "READY"; _lblStatus.ForeColor = Color.Blue; }
                    
                    // DYNAMIC BUTTON: START (On Ground) vs RESUME (In Air)
                    if (!state.IsArmed)
                    {
                        _btnStart.Text = "START MISSION";
                        _btnStart.BackColor = Color.FromArgb(40, 167, 69);
                        _btnStart.Visible = true;
                    }
                    else if (state.Mode != 3) // RTL, LAND, LOITER
                    {
                        _btnStart.Text = "RESUME MISSION";
                        _btnStart.BackColor = Color.FromArgb(0, 123, 255);
                        _btnStart.Visible = true;
                    }
                    else
                    {
                        _btnStart.Visible = false;
                    }
                }

                _btnPause.Visible = (state.IsArmed && state.Mode == 3);
                _btnResume.Visible = false; // Internal resume integrated into main button

                if (!state.IsArmed && _pState == PanelState.IDLE) 
                { 
                    _lblStatus.Text = "READY"; _lblStatus.ForeColor = Color.Blue; 
                }
            }

            private async Task CommandStartMission()
            {
                _pState = PanelState.BUSY;
                
                if (!_state.IsArmed)
                {
                    _lblStatus.Text = "STARTING...";
                    _state.AddLog("ACTION -> PREP GUIDED");
                    SendSetMode(4); 
                    await Task.Delay(50);
                    _state.AddLog("ACTION -> ARMING");
                    SendCmd(400, 1, 21196); 
                    await Task.Delay(100);
                }
                else
                {
                    _lblStatus.Text = "RESUMING...";
                    _state.AddLog("ACTION -> SYNC AUTO");
                    SendSetMode(3); 
                    await Task.Delay(50);
                }

                _state.AddLog($"ACTION -> START (WP: {_state.ResumeWp})");
                SendCmd(300, _state.ResumeWp, 0); 
                
                _state.ResumeWp = 0; // Reset after use

                _pState = PanelState.IDLE;
                _lblStatus.Text = "MISSION ACTIVE";
                _state.AddLog("COMMAND SENT.");
            }

            private async Task<bool> ExecuteStep(string log, Action send, Func<bool> verify)
            {
                _state.AddLog(log);
                int retries = 5;
                while (retries-- > 0)
                {
                    send();
                    // High-frequency polling (50ms) for 1 second per retry
                    for (int i = 0; i < 20; i++) 
                    { 
                        if (verify()) return true; 
                        await Task.Delay(50); 
                    }
                }
                _state.AddLog($"Step Failed: {log}");
                return false;
            }

            private Button CreateBtn(string t, Color c, int y)
            {
                var b = new Button { Text = t, Location = new Point(20, y), Size = new Size(320, 50), BackColor = c, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 11, FontStyle.Bold), Cursor = Cursors.Hand };
                b.FlatAppearance.BorderSize = 0; return b;
            }

            private void SendSetMode(uint m) => _device.Interface.Send(MavLinkCommands.CreateSetMode(255, 1, _device.SysId, 1, m));
            private void SendCmd(ushort c, float p1, float p2=0, float p3=0, float p4=0, float p5=0, float p6=0, float p7=0) 
                => _device.Interface.Send(MavLinkCommands.CreateCommandLong(255, 1, _device.SysId, _device.CompId, c, p1, p2, p3, p4, p5, p6, p7));
        }
    }
}
