using SS.Packets.Game;
using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Position(ServerTick ticks, in C2S_PositionPacket c2sPosition)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<Position>();

        #endregion

        public EventHeader Header = new(ticks, EventType.Position);
        public C2S_PositionPacket PositionPacket = c2sPosition;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PositionWithExtra(ServerTick ticks, in C2S_PositionPacket c2sPosition, in ExtraPositionData extra)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<PositionWithExtra>();

        #endregion

        public EventHeader Header = new(ticks, EventType.Position);
        public C2S_PositionPacket PositionPacket = c2sPosition;
        public ExtraPositionData ExtraPositionData = extra;
    }
}
