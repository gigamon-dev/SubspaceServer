using SS.Core;
using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ShipChange
    {
        #region Static members

        public static readonly int Length;

        static ShipChange()
        {
            Length = Marshal.SizeOf(typeof(ShipChange));
        }

        #endregion

        public EventHeader Header;
        private short playerId;
        private short newShip;
        private short newFreq;

        public ShipChange(ServerTick ticks, short playerId, ShipType newShip, short newFreq)
        {
            Header = new(ticks, EventType.FreqChange);
            this.playerId = LittleEndianConverter.Convert(playerId);
            this.newShip = LittleEndianConverter.Convert((short)newShip);
            this.newFreq = LittleEndianConverter.Convert(newFreq);
        }

        #region Helper properties

        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        public ShipType NewShip
        {
            get => (ShipType)LittleEndianConverter.Convert(newShip);
            set => newShip = LittleEndianConverter.Convert((short)value);
        }

        public short NewFreq
        {
            get => LittleEndianConverter.Convert(newFreq);
            set => newFreq = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
