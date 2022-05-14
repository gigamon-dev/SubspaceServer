using SS.Packets.Game;
using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BallPacketWrapper
    {
        #region Static members

        public static readonly int Length;

        static BallPacketWrapper()
        {
            Length = Marshal.SizeOf(typeof(BallPacketWrapper));
        }

        #endregion

        public EventHeader Header;
        public BallPacket BallPacket;

        public BallPacketWrapper(ServerTick ticks, in BallPacket ballPacket)
        {
            Header = new EventHeader(ticks, EventType.BallPacket);
            BallPacket = ballPacket;
        }
    }
}
