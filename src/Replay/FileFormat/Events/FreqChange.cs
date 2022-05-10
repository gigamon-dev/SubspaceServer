using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FreqChange
    {
        #region Static members

        public static readonly int Length;

        static FreqChange()
        {
            Length = Marshal.SizeOf(typeof(FreqChange));
        }

        #endregion

        public EventHeader Header;
        private short playerId;
        private short newFreq;

        public FreqChange(ServerTick ticks, short playerId, short newFreq)
        {
            Header = new(ticks, EventType.FreqChange);
            this.playerId = LittleEndianConverter.Convert(playerId);
            this.newFreq = LittleEndianConverter.Convert(newFreq);
        }

        #region Helper properties

        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        public short NewFreq
        {
            get => LittleEndianConverter.Convert(newFreq);
            set => newFreq = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
