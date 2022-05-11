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
    public struct Brick
    {
        #region Static members

        public static readonly int Length;

        static Brick()
        {
            Length = Marshal.SizeOf(typeof(Brick));
        }

        #endregion

        public EventHeader Header;
        public readonly byte Type;
        public BrickData BrickData;

        public Brick(ServerTick ticks, in BrickData brickData)
        {
            Header = new(ticks, EventType.Brick);
            Type = (byte)S2CPacketType.Brick;
            BrickData = brickData;
        }
    }
}
