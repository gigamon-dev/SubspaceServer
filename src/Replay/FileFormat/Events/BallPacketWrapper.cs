using SS.Packets.Game;
using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BallPacketWrapper(ServerTick ticks, ref readonly BallPacket ballPacket)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<BallPacketWrapper>();

        #endregion

        public EventHeader Header = new(ticks, EventType.BallPacket);
        public BallPacket BallPacket = ballPacket;
    }
}
