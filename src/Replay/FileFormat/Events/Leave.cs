using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Leave
    {
        #region Static members

        public static readonly int Length;

        static Leave()
        {
            Length = Marshal.SizeOf(typeof(Leave));
        }

        #endregion

        public EventHeader Header;
        private short playerId;

        public Leave(ServerTick ticks, short playerId)
        {
            Header = new EventHeader(ticks, EventType.Leave);
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
