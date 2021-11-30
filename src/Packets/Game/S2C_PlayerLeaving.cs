using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_PlayerLeaving
    {
        public readonly byte Type;
        private readonly short playerId;

        public readonly short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
        }

        public S2C_PlayerLeaving(short playerId)
        {
            Type = (byte)S2CPacketType.PlayerLeaving;
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
    }
}
