using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct CrownToggle
    {
        #region Static members

        public static readonly int Length;

        static CrownToggle()
        {
            Length = Marshal.SizeOf(typeof(CrownToggle));
        }

        #endregion

        public EventHeader Header;
        private short playerId;

        public CrownToggle(ServerTick ticks, bool on, short playerId)
        {
            Header = new EventHeader(ticks, on ? EventType.CrownToggleOn : EventType.CrownToggleOff);
            this.playerId = LittleEndianConverter.Convert(playerId);
        }

        #region Helper properties

        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
