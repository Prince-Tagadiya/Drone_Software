using System;
using System.IO;
using MinimalGCS.Mavlink;

namespace MinimalGCS.Mavlink
{
    public static class MavLinkCommands
    {
        public static byte[] CreateCommandLong(byte sysId, byte compId, byte targetSys, byte targetComp, ushort command, float p1, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0)
        {
            byte[] payload = new byte[33];
            // Format: float p1-p7 (28 bytes), ushort command (2 bytes), byte target_sys (1 byte), byte target_comp (1 byte), byte confirmation (1 byte)
            
            Buffer.BlockCopy(BitConverter.GetBytes(p1), 0, payload, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p2), 0, payload, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p3), 0, payload, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p4), 0, payload, 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p5), 0, payload, 16, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p6), 0, payload, 20, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(p7), 0, payload, 24, 4);
            
            payload[28] = (byte)(command & 0xFF);
            payload[29] = (byte)((command >> 8) & 0xFF);
            
            payload[30] = targetSys;
            payload[31] = targetComp;
            payload[32] = 0; // confirmation

            return BuildPacket(sysId, compId, MavLinkMessages.COMMAND_LONG_ID, payload);
        }

        public static byte[] CreateSetMode(byte sysId, byte compId, byte targetSys, byte baseMode, uint customMode)
        {
            byte[] payload = new byte[6];
            Buffer.BlockCopy(BitConverter.GetBytes(customMode), 0, payload, 0, 4);
            payload[4] = targetSys;
            payload[5] = baseMode;

            return BuildPacket(sysId, compId, MavLinkMessages.SET_MODE_ID, payload);
        }

        private static byte[] BuildPacket(byte sysId, byte compId, uint msgId, byte[] payload)
        {
            // MAVLink v1 for simplicity in command sending
            int len = 6 + payload.Length + 2;
            byte[] packet = new byte[len];
            packet[0] = 0xFE;
            packet[1] = (byte)payload.Length;
            packet[2] = 0; // Sequence
            packet[3] = sysId;
            packet[4] = compId;
            packet[5] = (byte)msgId;
            
            Buffer.BlockCopy(payload, 0, packet, 6, payload.Length);
            
            // CRC calculation
            byte[] forCrc = new byte[5 + payload.Length];
            Buffer.BlockCopy(packet, 1, forCrc, 0, 5 + payload.Length);
            
            byte crcExtra = MavLinkMessages.CrcExtras.ContainsKey(msgId) ? MavLinkMessages.CrcExtras[msgId] : (byte)0;
            ushort crc = MavLinkPacket.CalculateChecksum(forCrc, crcExtra);
            
            packet[len - 2] = (byte)(crc & 0xFF);
            packet[len - 1] = (byte)((crc >> 8) & 0xFF);
            
            return packet;
        }
    }
}
