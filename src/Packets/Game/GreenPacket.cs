using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    public enum Prize : short
    {
        Recharge = 1,
        Energy,
        Rotation,
        Stealth,
        Cloak,
        XRadar,
        Warp,
        Gun,
        Bomb,
        Bounce,
        Thrust,
        Speed,
        FullCharge,
        Shutdown,
        Multifire,
        Prox,
        Super,
        Shield,
        Shrap,
        Antiwarp,
        Repel,
        Burst,
        Decoy,
        Thor,
        Multiprize,
        Brick,
        Rocket,
        Portal
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_Green(byte type, uint time, short x, short y, short prize)
    {
		#region Static Members

		public static readonly int Length = Marshal.SizeOf<C2S_Green>();

        #endregion

        public readonly byte Type = type;
        private readonly uint time = LittleEndianConverter.Convert(time);
        private readonly short x = LittleEndianConverter.Convert(x);
        private readonly short y = LittleEndianConverter.Convert(y);
        private readonly short prize = LittleEndianConverter.Convert(prize);

        public C2S_Green(uint time, short x, short y, short prize)
            : this((byte)C2SPacketType.Green, time, x, y, prize)
        {
        }

		#region Helper Properties

		public uint Time => LittleEndianConverter.Convert(time);

		public short X => LittleEndianConverter.Convert(x);

		public short Y => LittleEndianConverter.Convert(y);

		public Prize Prize => (Prize)LittleEndianConverter.Convert(prize);

		#endregion
	}

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_Green(ref readonly C2S_Green c2s, short playerId)
    {
		#region Static Members

		public static readonly int Length = Marshal.SizeOf<S2C_Green>();

        #endregion

        public readonly C2S_Green C2S = new((byte)S2CPacketType.Green, c2s.Time, c2s.X, c2s.Y, (short)c2s.Prize);
		private readonly short playerId = LittleEndianConverter.Convert(playerId);

		#region Helper Properties

		public short PlayerId => LittleEndianConverter.Convert(playerId);

		#endregion
	}
}
