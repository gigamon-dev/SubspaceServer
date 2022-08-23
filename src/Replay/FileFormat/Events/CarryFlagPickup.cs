using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CarryFlagPickup
    {
        #region Static members

        public static readonly int Length;

        static CarryFlagPickup()
        {
            Length = Marshal.SizeOf(typeof(CarryFlagPickup));
        }

        #endregion

        public EventHeader Header;
        private short flagId;
        private short playerId;

        public CarryFlagPickup(ServerTick ticks, short flagId, short playerId)
        {
            Header = new(ticks, EventType.CarryFlagPickup);
            this.flagId = LittleEndianConverter.Convert(flagId);
            this.playerId = LittleEndianConverter.Convert(playerId);
        }

        #region Helper properties

        public short FlagId => LittleEndianConverter.Convert(flagId);
        public short PlayerId => LittleEndianConverter.Convert(playerId);
        
        #endregion
    }
}
