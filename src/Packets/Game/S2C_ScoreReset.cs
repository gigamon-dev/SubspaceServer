using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Packet that tells the client to reset score stats (kill points, flag points, kills, deaths).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_ScoreReset(short playerId)
    {
        public readonly byte Type = (byte)S2CPacketType.ScoreReset;
        private short playerId = LittleEndianConverter.Convert(playerId);

        #region Helper Properties

        /// <summary>
        /// Id of the player to reset, or -1 to reset all players in the arena.
        /// </summary>
        public short PlayerId
        {
            readonly get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
