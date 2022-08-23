using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CarryFlagDrop
    {
        #region Static members

        public static readonly int Length;

        static CarryFlagDrop()
        {
            Length = Marshal.SizeOf(typeof(CarryFlagDrop));
        }

        #endregion

        public EventHeader Header;
        private short playerId;

        public CarryFlagDrop(ServerTick ticks, short playerId)
        {
            Header = new(ticks, EventType.CarryFlagDrop);
            this.playerId = LittleEndianConverter.Convert(playerId);
        }

        #region Helper properties

        public short PlayerId => LittleEndianConverter.Convert(playerId);

        #endregion
    }
}
