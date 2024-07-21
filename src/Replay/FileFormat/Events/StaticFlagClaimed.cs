using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    /// <summary>
    /// Event for when a player claims a static flag by touching it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StaticFlagClaimed(ServerTick ticks, short flagId, short playerId)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<StaticFlagClaimed>();

        #endregion

        public EventHeader Header = new(ticks, EventType.StaticFlagClaimed);
        private short flagId = LittleEndianConverter.Convert(flagId);
        private short playerId = LittleEndianConverter.Convert(playerId);

        #region Helper properties

        public short FlagId
        {
            readonly get => LittleEndianConverter.Convert(flagId);
            set => flagId = LittleEndianConverter.Convert(value);
        }

        public short PlayerId
        {
            readonly get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
