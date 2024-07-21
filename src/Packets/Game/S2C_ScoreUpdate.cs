using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_ScoreUpdate(short playerId, int killPoints, int flagPoints, ushort kills, ushort deaths)
    {
        public readonly byte Type = (byte)S2CPacketType.ScoreUpdate;
        private readonly short playerId = LittleEndianConverter.Convert(playerId);
        private readonly int killPoints = LittleEndianConverter.Convert(killPoints);
        private readonly int flagPoints = LittleEndianConverter.Convert(flagPoints);
        private readonly ushort kills = LittleEndianConverter.Convert(kills);
        private readonly ushort deaths = LittleEndianConverter.Convert(deaths);

        #region Helper Properties

        public short PlayerId => LittleEndianConverter.Convert(playerId);

        public int KillPoints => LittleEndianConverter.Convert(killPoints);

        public int FlagPoints => LittleEndianConverter.Convert(flagPoints);

        public ushort Kills => LittleEndianConverter.Convert(kills);

        public ushort Deaths => LittleEndianConverter.Convert(deaths);

        #endregion
    }
}
