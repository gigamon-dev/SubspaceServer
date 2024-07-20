using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BallPacket(bool isS2C, byte ballId, short x, short y, short xSpeed, short ySpeed, short playerId, uint time)
	{
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<BallPacket>();

        #endregion

        public readonly byte Type = isS2C ? (byte)S2CPacketType.Ball : (byte)C2SPacketType.ShootBall;
        public readonly byte BallId = ballId;
        private short x = LittleEndianConverter.Convert(x);
        private short y = LittleEndianConverter.Convert(y);
        private short xSpeed = LittleEndianConverter.Convert(xSpeed);
        private short ySpeed = LittleEndianConverter.Convert(ySpeed);
        private short playerId = LittleEndianConverter.Convert(playerId);
        private uint time = LittleEndianConverter.Convert(time);

		#region Helper Properties

		public short X
		{
			readonly get => LittleEndianConverter.Convert(x);
			set => x = LittleEndianConverter.Convert(value);
		}

		public short Y
        {
			readonly get => LittleEndianConverter.Convert(y);
            set => y = LittleEndianConverter.Convert(value);
        }

        public short XSpeed
        {
			readonly get => LittleEndianConverter.Convert(xSpeed);
            set => xSpeed = LittleEndianConverter.Convert(value);
        }

        public short YSpeed
        {
			readonly get => LittleEndianConverter.Convert(ySpeed);
            set => ySpeed = LittleEndianConverter.Convert(value);
        }

        public short PlayerId
        {
			readonly get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        public uint Time
        {
			readonly get => LittleEndianConverter.Convert(time);
            set => time = LittleEndianConverter.Convert(value);
        }

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_PickupBall(byte ballId, uint time)
	{
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<C2S_PickupBall>();

        #endregion

        public readonly byte Type = (byte)C2SPacketType.PickupBall;
        public readonly byte BallId = ballId;
        private readonly uint time = LittleEndianConverter.Convert(time);

		#region Helper Properties

		public uint Time => LittleEndianConverter.Convert(time);

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_Goal(byte ballId, short x, short y)
	{
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<C2S_Goal>();

        #endregion

        public readonly byte Type = (byte)C2SPacketType.Goal;
        public readonly byte BallId = ballId;
        private readonly short x = LittleEndianConverter.Convert(x);
        private readonly short y = LittleEndianConverter.Convert(y);

		#region Helper Properties

		public short X => LittleEndianConverter.Convert(x);

        public short Y => LittleEndianConverter.Convert(y);

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_Goal(short freq, int points)
	{
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2C_Goal>();

		#endregion

		public readonly byte Type = (byte)S2CPacketType.SoccerGoal;
        private readonly short freq = LittleEndianConverter.Convert(freq);
        private readonly int points = LittleEndianConverter.Convert(points);

		#region Helper Properties

		public short Freq => LittleEndianConverter.Convert(freq);

        public int Points => LittleEndianConverter.Convert(points);

        #endregion
    }
}
