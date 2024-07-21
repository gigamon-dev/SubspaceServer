using SS.Core;
using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ShipChange(ServerTick ticks, short playerId, ShipType newShip, short newFreq)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<ShipChange>();

        #endregion

        public EventHeader Header = new(ticks, EventType.ShipChange);
        private short playerId = LittleEndianConverter.Convert(playerId);
        private short newShip = LittleEndianConverter.Convert((short)newShip);
        private short newFreq = LittleEndianConverter.Convert(newFreq);

        #region Helper properties

        public short PlayerId
        {
            readonly get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        public ShipType NewShip
        {
            readonly get => (ShipType)LittleEndianConverter.Convert(newShip);
            set => newShip = LittleEndianConverter.Convert((short)value);
        }

        public short NewFreq
        {
            readonly get => LittleEndianConverter.Convert(newFreq);
            set => newFreq = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
