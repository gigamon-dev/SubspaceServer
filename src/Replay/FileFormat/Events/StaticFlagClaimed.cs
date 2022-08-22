using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    /// <summary>
    /// Event for when a player claims a static flag by touching it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StaticFlagClaimed
    {
        #region Static members

        public static readonly int Length;

        static StaticFlagClaimed()
        {
            Length = Marshal.SizeOf(typeof(StaticFlagClaimed));
        }

        #endregion

        public EventHeader Header;
        private short flagId;
        private short playerId;

        public StaticFlagClaimed(ServerTick ticks, short flagId, short playerId)
        {
            Header = new(ticks, EventType.StaticFlagClaimed);
            this.flagId = LittleEndianConverter.Convert(flagId);
            this.playerId = LittleEndianConverter.Convert(playerId);
        }

        #region Helper properties

        public short FlagId => LittleEndianConverter.Convert(flagId);

        public short PlayerId => LittleEndianConverter.Convert(playerId);

        #endregion
    }
}
