using System;
using System.IO.Ports;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MinimalGCS.Mavlink;

namespace MinimalGCS.Connection
{
    public class DiscoveredDevice
    {
        public MavLinkInterface Interface { get; set; } = null!;
        public byte SysId { get; set; }
        public byte CompId { get; set; }
        public DateTime LastHeartbeat { get; set; }

        public override string ToString() => $"Drone {SysId} ({Interface.Name})";
    }

    public class AutoConnector
    {
        private readonly List<MavLinkInterface> _probingInterfaces = new List<MavLinkInterface>();
        public readonly ConcurrentDictionary<string, DiscoveredDevice> ConnectedDevices = new ConcurrentDictionary<string, DiscoveredDevice>();
        private readonly object _lock = new object();

        public event Action<DiscoveredDevice>? OnDeviceConnected;
        public event Action<string>? OnDeviceDisconnected;

        private CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _isRunning = false;

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            StartBackend();
            Task.Run(() => ScanningLoop(_cts.Token));
        }

        public void Stop()
        {
            _cts.Cancel();
            _isRunning = false;
            lock (_lock)
            {
                foreach (var device in ConnectedDevices.Values) device.Interface.Close();
                foreach (var iface in _probingInterfaces) iface.Close();
                _probingInterfaces.Clear();
                ConnectedDevices.Clear();
            }
        }

        private void StartBackend()
        {
            try
            {
                // Cleanup old proxy instances to free ports
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "taskkill", Arguments = "/IM mavproxy.exe /F", CreateNoWindow = true, UseShellExecute = false }).WaitForExit(); } catch { }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "mavproxy.exe",
                    Arguments = "--master=tcp:127.0.0.1:5760 --out=udp:127.0.0.1:14550 --out=udp:127.0.0.1:14551 --nodefaults",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch { }
        }

        private async Task ScanningLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var now = DateTime.Now;
                foreach (var kvp in ConnectedDevices.ToList())
                {
                    if ((now - kvp.Value.LastHeartbeat).TotalSeconds > 10)
                    {
                        if (ConnectedDevices.TryRemove(kvp.Key, out var device))
                        {
                            device.Interface.Close();
                            OnDeviceDisconnected?.Invoke(kvp.Key);
                        }
                    }
                }

                // INSTANT PARALLEL PROBING
                var tasks = new List<Task>();
                
                // Probe standard UDP ports
                for (int p = 14550; p <= 14560; p++) { int port = p; tasks.Add(Task.Run(() => CheckUdp(port))); }
                
                // Probe standard TCP ports with short timeout
                for (int p = 5760; p <= 5780; p++) 
                { 
                    int port = p; 
                    tasks.Add(Task.Run(async () => await CheckTcpAsync("127.0.0.1", port))); 
                }
                
                tasks.Add(Task.Run(() => CheckSerialPorts()));

                await Task.WhenAll(tasks);
                await Task.Delay(3000, token); // Slower scanning to save CPU
            }
        }

        private void CheckUdp(int port)
        {
            lock (_lock)
            {
                string name = $"UDP:{port}";
                if (ConnectedDevices.ContainsKey(name) || _probingInterfaces.Any(i => i.Name == name)) return;
                try { SetupProbe(new UdpInterface(port)); } catch { }
            }
        }

        private async Task CheckTcpAsync(string host, int port)
        {
            string name = $"TCP:{host}:{port}";
            lock (_lock) { if (ConnectedDevices.ContainsKey(name) || _probingInterfaces.Any(i => i.Name == name)) return; }

            try
            {
                using (var client = new TcpClient())
                {
                    var connectTask = client.ConnectAsync(host, port);
                    if (await Task.WhenAny(connectTask, Task.Delay(500)) == connectTask) // 500ms for more reliability
                    {
                        if (client.Connected)
                        {
                            var iface = new TcpInterface(host, port); // New instance to keep open
                            lock (_lock) { SetupProbe(iface); }
                        }
                    }
                }
            }
            catch { }
        }

        private void CheckSerialPorts()
        {
            var ports = SerialPort.GetPortNames();
            lock (_lock)
            {
                foreach (var port in ports)
                {
                    foreach (var baud in new[] { 115200, 57600 })
                    {
                        string name = $"{port}@{baud}";
                        if (ConnectedDevices.ContainsKey(name) || _probingInterfaces.Any(i => i.Name == name)) continue;
                        try { SetupProbe(new SerialInterface(port, baud)); } catch { }
                    }
                }
            }
        }

        private void SetupProbe(MavLinkInterface iface)
        {
            _probingInterfaces.Add(iface);
            var parser = new MavLinkParser();
            parser.PacketReceived += (pkt) => {
                if (pkt.MessageId == MavLinkMessages.HEARTBEAT_ID) HandleDiscovery(iface, pkt);
            };
            iface.StartReading(data => parser.Parse(data));
        }

        private void HandleDiscovery(MavLinkInterface iface, MavLinkPacket pkt)
        {
            // Filter out GCS/MAVProxy heartbeats (SysId 255 or 0)
            if (pkt.SystemId == 255 || pkt.SystemId == 0) return;

            if (ConnectedDevices.TryGetValue(iface.Name, out var device))
            {
                device.LastHeartbeat = DateTime.Now;
                return;
            }

            lock (_lock)
            {
                _probingInterfaces.Remove(iface);
                var newDevice = new DiscoveredDevice { 
                    Interface = iface, SysId = pkt.SystemId, CompId = pkt.ComponentId, LastHeartbeat = DateTime.Now 
                };
                if (ConnectedDevices.TryAdd(iface.Name, newDevice))
                {
                    OnDeviceConnected?.Invoke(newDevice);
                }
            }
        }
    }
}
