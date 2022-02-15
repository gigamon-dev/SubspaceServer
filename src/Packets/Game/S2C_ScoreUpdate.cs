using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_ScoreUpdate
    {
        public byte Type;

        private short playerId;
        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(playerId);
        }

        private int killPoints;
        public int KillPoints
        {
            get => LittleEndianConverter.Convert(killPoints);
            set => killPoints = LittleEndianConverter.Convert(killPoints);
        }

        private int flagPoints;
        public int FlagPoints
        {
            get => LittleEndianConverter.Convert(flagPoints);
            set => flagPoints = LittleEndianConverter.Convert(flagPoints);
        }

        private short kills;
        public short Kills
        {
            get => LittleEndianConverter.Convert(kills);
            set => kills = LittleEndianConverter.Convert(kills);
        }

        private short deaths;
        public short Deaths
        {
            get => LittleEndianConverter.Convert(deaths);
            set => deaths = LittleEndianConverter.Convert(deaths);
        }

        public S2C_ScoreUpdate(short playerId, int killPoints, int flagPoints, short kills, short deaths)
        {
            Type = (byte)S2CPacketType.ScoreUpdate;
            this.playerId = LittleEndianConverter.Convert(playerId);
            this.killPoints = LittleEndianConverter.Convert(killPoints);
            this.flagPoints = LittleEndianConverter.Convert(flagPoints);
            this.kills = LittleEndianConverter.Convert(kills);
            this.deaths = LittleEndianConverter.Convert(deaths);
        }
    }
}
