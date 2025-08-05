using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AttachChange(ServerTick ticks, short playerId, short toPlayerId)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<AttachChange>();

        #endregion

        public EventHeader Header = new(ticks, EventType.AttachChange);
        private short playerId = LittleEndianConverter.Convert(playerId);
        private short toPlayerId = LittleEndianConverter.Convert(toPlayerId);

        #region Helper properties

        public short PlayerId
        {
            readonly get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        public short ToPlayerId
        {
            readonly get => LittleEndianConverter.Convert(toPlayerId);
            set => toPlayerId = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
