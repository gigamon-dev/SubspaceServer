using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.S2C
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct TurretKickoffPacket
    {
        public readonly byte Type;
        private readonly short playerId;

        public short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
        }

        public TurretKickoffPacket(short playerId)
        {
            Type = (byte)S2CPacketType.TurretKickoff;
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
    }
}
