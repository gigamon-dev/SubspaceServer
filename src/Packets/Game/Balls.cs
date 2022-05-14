using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BallPacket
    {
        #region Static Members

        public static readonly int Length;
        static BallPacket() => Length = Marshal.SizeOf<BallPacket>();

        #endregion

        public readonly byte Type;
        public readonly byte BallId;
        private short x;
        private short y;
        private short xSpeed;
        private short ySpeed;
        private short playerId;
        private uint time;

        public BallPacket(bool isS2C, byte ballId, short x, short y, short xSpeed, short ySpeed, short playerId, uint time)
        {
            Type = isS2C ? (byte)S2CPacketType.Ball : (byte)C2SPacketType.ShootBall;
            BallId = ballId;
            this.x = LittleEndianConverter.Convert(x);
            this.y = LittleEndianConverter.Convert(y);
            this.xSpeed = LittleEndianConverter.Convert(xSpeed);
            this.ySpeed = LittleEndianConverter.Convert(ySpeed);
            this.playerId = LittleEndianConverter.Convert(playerId);
            this.time = LittleEndianConverter.Convert(time);
        }

        #region Helper Properties

        public short X
        {
            get => LittleEndianConverter.Convert(x);
            set => x = LittleEndianConverter.Convert(value);
        }

        public short Y
        {
            get => LittleEndianConverter.Convert(y);
            set => y = LittleEndianConverter.Convert(value);
        }

        public short XSpeed
        {
            get => LittleEndianConverter.Convert(xSpeed);
            set => xSpeed = LittleEndianConverter.Convert(value);
        }

        public short YSpeed
        {
            get => LittleEndianConverter.Convert(ySpeed);
            set => ySpeed = LittleEndianConverter.Convert(value);
        }

        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        public uint Time
        {
            get => LittleEndianConverter.Convert(time);
            set => time = LittleEndianConverter.Convert(value);
        }

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_PickupBall
    {
        #region Static Members

        public static readonly int Length;
        static C2S_PickupBall() => Length = Marshal.SizeOf<C2S_PickupBall>();

        #endregion

        public readonly byte Type;
        public readonly byte BallId;
        private readonly uint time;

        public C2S_PickupBall(byte ballId, uint time)
        {
            Type = (byte)C2SPacketType.PickupBall;
            BallId = ballId;
            this.time = LittleEndianConverter.Convert(time);
        }

        #region Helper Properties

        public uint Time => LittleEndianConverter.Convert(time);

        #endregion

    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_Goal
    {
        #region Static Members

        public static readonly int Length;
        static C2S_Goal() => Length = Marshal.SizeOf<C2S_Goal>();

        #endregion

        public readonly byte Type;
        public readonly byte BallId;
        private readonly short x;
        private readonly short y;

        public C2S_Goal(byte ballId, short x, short y)
        {
            Type = (byte)C2SPacketType.Goal;
            BallId = ballId;
            this.x = LittleEndianConverter.Convert(x);
            this.y = LittleEndianConverter.Convert(y);
        }

        #region Helper Properties

        public short X => LittleEndianConverter.Convert(x);

        public short Y => LittleEndianConverter.Convert(y);

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_Goal
    {
        #region Static Members

        public static readonly int Length;
        static S2C_Goal() => Length = Marshal.SizeOf<S2C_Goal>();

        #endregion

        public readonly byte Type;
        private readonly short freq;
        private readonly int points;

        public S2C_Goal(short freq, int points)
        {
            Type = (byte)S2CPacketType.SoccerGoal;
            this.freq = LittleEndianConverter.Convert(freq);
            this.points = LittleEndianConverter.Convert(points);
        }

        #region Helper Properties

        public short Freq => LittleEndianConverter.Convert(freq);

        public int Points => LittleEndianConverter.Convert(points);

        #endregion
    }
}
