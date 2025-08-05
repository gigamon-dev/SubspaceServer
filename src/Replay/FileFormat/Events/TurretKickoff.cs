using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TurretKickoff(ServerTick ticks, short playerId)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<TurretKickoff>();

        #endregion

        public EventHeader Header = new(ticks, EventType.TurretKickoff);
        private short playerId = LittleEndianConverter.Convert(playerId);

        #region Helper properties

        public short PlayerId
        {
            readonly get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
