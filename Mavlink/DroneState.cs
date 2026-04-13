using System;
using System.Collections.Concurrent;

namespace MinimalGCS.Mavlink
{
    public class DroneState
    {
        public int SysId { get; set; }
        public bool IsArmed { get; set; }
        public uint Mode { get; set; }
        public int GpsFixType { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public float Alt { get; set; }
        public int CurrentWp { get; set; }
        public int ResumeWp { get; set; }
        public DateTime LastHeartbeat { get; set; }
        public bool IsConnected => (DateTime.Now - LastHeartbeat).TotalSeconds < 10;
        
        // Log history and status messages
        public ConcurrentQueue<string> Messages { get; } = new ConcurrentQueue<string>();
        public string LastMessage { get; set; } = "Ready";

        public DroneState(int sysId)
        {
            SysId = sysId;
            LastHeartbeat = DateTime.Now;
        }

        public void AddLog(string msg)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Messages.Enqueue(entry);
            LastMessage = msg;
            Console.WriteLine($"Drone {SysId}: {entry}"); // Internal logging
        }
    }
}
