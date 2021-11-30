using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_WhoAmI
    {
        public byte Type;
        private short playerId;

        public short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
            set { playerId = LittleEndianConverter.Convert(playerId); }
        }

        public S2C_WhoAmI(short playerId)
        {
            Type = (byte)S2CPacketType.WhoAmI;
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
    }
}
