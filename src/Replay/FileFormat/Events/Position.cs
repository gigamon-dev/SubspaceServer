using SS.Packets.Game;
using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Position
    {
        #region Static members

        public static readonly int Length;

        static Position()
        {
            Length = Marshal.SizeOf(typeof(Position));
        }

        #endregion

        public EventHeader Header;
        public C2S_PositionPacket PositionPacket;

        public Position(ServerTick ticks, in C2S_PositionPacket c2sPosition)
        {
            Header = new(ticks, EventType.Position);
            PositionPacket = c2sPosition;
        }
    }

    // A possible way to more accurately record position packets.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Position2
    {
        #region Static members

        public static readonly int Length;

        static Position2()
        {
            Length = Marshal.SizeOf(typeof(Position2));
        }

        #endregion

        public EventHeader Header;
        private short playerId;

        // Fields from C2S_Position, but with modifications:
        // - Instead of "time", the c2s latency.  To generate the fake C2S_Position packet time field, take the current tick count - C2SLatency
        // - No checksum field, no reason to store it. Can generate it, but it's not even checked for fake players, so no need to even do that.

        /// <summary>
        /// The difference of the <see cref="C2S_PositionPacket.Time"/> field from the server's actual time when the packet was processed by the server.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This can be used to calculate the <see cref="C2S_PositionPacket.Time"/> the client originally sent, relative to the current tick count by simply subtracting it.
        /// This is somewhat similar to <see cref="S2C_PositionPacket.C2SLatency"/>, except because this allows for negative values, 
        /// it can accurately represent the original <see cref="C2S_PositionPacket.Time"/>.
        /// </para>
        /// <para>
        /// The <see cref="C2S_PositionPacket.Time"/> is the client's estimated guess of the server's time based on previous TimeSync requests/responses.
        /// If the client estimates too large of a latency, there is a chance that the <see cref="C2S_PositionPacket.Time"/> is greater than the server's time.
        /// In this case, the <see cref="C2SLatency"/> will be a negative value.
        /// </para>
        /// </remarks>
        public short C2SLatency;

        public sbyte Rotation;
        private short x;
        private short y;
        private short xSpeed;
        private short ySpeed;
        private byte status;
        private ushort bounty;
        private short energy;
        public WeaponData Weapon;
        public ExtraPositionData Extra;

        #region Helper properties

        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
