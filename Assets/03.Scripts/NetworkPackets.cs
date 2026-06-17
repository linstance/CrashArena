using System;
using System.Runtime.InteropServices;

public class NetworkPackets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct InputPacket
    {
        public int playerId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string playerName;

        public float inputX;
        public float inputY;

        [MarshalAs(UnmanagedType.U1)] // 1바이트 bool
        public bool isDriving;
        [MarshalAs(UnmanagedType.U1)] // 1바이트 bool
        public bool isBoosting;
    }
    
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    public struct StatePacket
    {
        public int playerId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string playerName;
        
        public float posX, posY, posZ;
        public float rotY;
        public float velX, velZ;
        
        [MarshalAs(UnmanagedType.U1)] // 1바이트 bool
        public bool isBoosting;
        [MarshalAs(UnmanagedType.U1)] // 1바이트 bool
        public bool isAlive;
    }
}