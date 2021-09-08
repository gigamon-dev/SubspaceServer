using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.S2C
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct PlayerLeavingPacket
    {
        public readonly byte Type;
        private readonly short playerId;

        public readonly short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
        }

        public PlayerLeavingPacket(short playerId)
        {
            Type = (byte)S2CPacketType.PlayerLeaving;
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
    }
}
