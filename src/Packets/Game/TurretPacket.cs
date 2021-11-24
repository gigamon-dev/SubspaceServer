using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.S2C
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct TurretPacket
    {
        public readonly byte Type;
        private readonly short playerId;
        private readonly short toPlayerId;

        public short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
        }

        public short ToPlayerId
        {
            get { return LittleEndianConverter.Convert(toPlayerId); }
        }

        public TurretPacket(short playerId, short toPlayerId)
        {
            Type = (byte)S2CPacketType.Turret;
            this.playerId = LittleEndianConverter.Convert(playerId);
            this.toPlayerId = LittleEndianConverter.Convert(toPlayerId);
        }
    }
}
