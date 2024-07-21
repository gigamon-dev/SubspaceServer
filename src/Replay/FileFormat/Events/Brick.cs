using SS.Packets.Game;
using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    /// <summary>
    /// Based on the PowerBall Zone implementation.
    /// </summary>
    /// <remarks>
    /// Only can represent a single brick.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Brick(ServerTick ticks, in BrickData brickData)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<Brick>();

        #endregion

        public EventHeader Header = new(ticks, EventType.Brick);
        public byte Type = (byte)S2CPacketType.Brick;
        public BrickData BrickData = brickData;
    }
}
