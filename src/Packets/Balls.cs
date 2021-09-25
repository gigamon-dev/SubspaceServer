using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BallPacket
    {
        public static readonly int Length;
        static BallPacket() => Length = Marshal.SizeOf<BallPacket>();

        public readonly byte Type;

        public readonly byte BallId;

        private readonly short x;
        public short X => LittleEndianConverter.Convert(x);

        private readonly short y;
        public short Y => LittleEndianConverter.Convert(y);

        private readonly short xSpeed;
        public short XSpeed => LittleEndianConverter.Convert(xSpeed);

        private readonly short ySpeed;
        public short YSpeed => LittleEndianConverter.Convert(ySpeed);

        private readonly short playerId;
        public short PlayerId => LittleEndianConverter.Convert(playerId);

        private uint time;
        public uint Time
        {
            get => LittleEndianConverter.Convert(time);
            set => time = LittleEndianConverter.Convert(value);
        }

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
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2SPickupBall
    {
        public static readonly int Length;
        static C2SPickupBall() => Length = Marshal.SizeOf<C2SPickupBall>();

        public readonly byte Type;

        public readonly byte BallId;

        private readonly uint time;
        public uint Time => LittleEndianConverter.Convert(time);

        public C2SPickupBall(byte ballId, uint time)
        {
            Type = (byte)C2SPacketType.PickupBall;
            BallId = ballId;
            this.time = LittleEndianConverter.Convert(time);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2SGoal
    {
        public static readonly int Length;
        static C2SGoal() => Length = Marshal.SizeOf<C2SGoal>();

        public readonly byte Type;

        public readonly byte BallId;

        private readonly short x;
        public short X => LittleEndianConverter.Convert(x);

        private readonly short y;
        public short Y => LittleEndianConverter.Convert(y);

        public C2SGoal(byte ballId, short x, short y)
        {
            Type = (byte)C2SPacketType.Goal;
            BallId = ballId;
            this.x = LittleEndianConverter.Convert(x);
            this.y = LittleEndianConverter.Convert(y);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2CGoal
    {
        public static readonly int Length;
        static S2CGoal() => Length = Marshal.SizeOf<S2CGoal>();

        public readonly byte Type;

        private readonly short freq;
        public short Freq => LittleEndianConverter.Convert(freq);

        private readonly int points;
        public int Points => LittleEndianConverter.Convert(points);

        public S2CGoal(short freq, int points)
        {
            Type = (byte)S2CPacketType.SoccerGoal;
            this.freq = LittleEndianConverter.Convert(freq);
            this.points = LittleEndianConverter.Convert(points);
        }
    }
}
