using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CarryFlagPickup(ServerTick ticks, short flagId, short playerId)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<CarryFlagPickup>();

        #endregion

        public EventHeader Header = new(ticks, EventType.CarryFlagPickup);
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
