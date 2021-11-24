using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.S2C
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct WhoAmIPacket
    {
        public byte Type;
        private short playerId;

        public short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
            set { playerId = LittleEndianConverter.Convert(playerId); }
        }

        public WhoAmIPacket(short playerId)
        {
            Type = (byte)S2CPacketType.WhoAmI;
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
    }
}
