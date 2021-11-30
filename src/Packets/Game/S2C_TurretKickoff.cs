using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_TurretKickoff
    {
        public readonly byte Type;
        private readonly short playerId;

        public short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
        }

        public S2C_TurretKickoff(short playerId)
        {
            Type = (byte)S2CPacketType.TurretKickoff;
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
    }
}
