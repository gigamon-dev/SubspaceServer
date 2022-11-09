using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Packet that tells the client to switch to another server.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2C_Redirect
    {
        public readonly byte Type;
        private uint ip;
        private ushort port;
        private short arenaType; // Same values as in the ?go packet
        private fixed byte arenaNameBytes[ArenaNameBytesLength];
        private uint loginId;
        
        public S2C_Redirect(uint ip, ushort port, short arenaType, ReadOnlySpan<char> arenaName, uint loginId)
        {
            Type = (byte)S2CPacketType.Redirect;
            this.ip = LittleEndianConverter.Convert(ip);
            this.port = LittleEndianConverter.Convert(port);
            this.arenaType = LittleEndianConverter.Convert(arenaType);
            this.loginId = LittleEndianConverter.Convert(loginId);
            ArenaNameBytes.WriteNullPaddedString(arenaName, false);
        }

        #region Helper Properties

        private const int ArenaNameBytesLength = 16;
        private Span<byte> ArenaNameBytes => MemoryMarshal.CreateSpan(ref arenaNameBytes[0], ArenaNameBytesLength);
        public string ArenaName
        {
            get => ArenaNameBytes.ReadNullTerminatedString();
            set => ArenaNameBytes.WriteNullPaddedString(value, false);
        }

        #endregion
    }
}
