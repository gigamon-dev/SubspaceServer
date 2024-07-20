using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_FlagLocation(short flagId, short x, short y, short freq)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<S2C_FlagLocation>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.FlagLoc;
        private readonly short flagId = LittleEndianConverter.Convert(flagId);
        private readonly short x = LittleEndianConverter.Convert(x);
        private readonly short y = LittleEndianConverter.Convert(y);
        private readonly short freq = LittleEndianConverter.Convert(freq);

		#region Helper Properties

		public short FlagId => LittleEndianConverter.Convert(flagId);

		public short X => LittleEndianConverter.Convert(x);

		public short Y => LittleEndianConverter.Convert(y);

		public short Freq => LittleEndianConverter.Convert(freq);

		#endregion
	}

	/// <summary>
	/// Packet for when a player touches a flag. Either to claim a static flag or to pick up a flag that can be carried.
	/// </summary>
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_TouchFlag
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<C2S_TouchFlag>();

        #endregion

        public readonly byte Type;
        private readonly short flagId;

		#region Helper Properties

		public short FlagId => LittleEndianConverter.Convert(flagId);

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_FlagPickup(short flagId, short playerId)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<S2C_FlagPickup>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.FlagPickup;
        private readonly short flagId = LittleEndianConverter.Convert(flagId);
        private readonly short playerId = LittleEndianConverter.Convert(playerId);

		#region Helper Properties

		public short FlagId => LittleEndianConverter.Convert(flagId);

		public short PlayerId => LittleEndianConverter.Convert(playerId);

		#endregion
	}

	/// <summary>
	/// Notifies the client that the flag game is over.
	/// - The client will print an arena message:
	///   + No winner (freq = -1):
	///     Flag game reset.
	///   + On the winning freq (even if in spec or in a safe zone):
	///     Team Victory
	///     Reward: [points] points
	///   + Not on the winning freq:
	///     Opposing team won ([points] points given)
	/// - The client will increment points for each player on the winning team that is in a ship and not in a safe zone.
	/// - The client will reset the player's ship if the player is on the winning team, in a ship, and not in a safe zone.
	/// - The client will reset (remove) all of the flags. This includes carryable flags and static flags.
	///   Carryable flags will reappear when they are respawned (S2C 0x12 Flag Location packets are received).
	///   Static flags will reappear when the it gets a full flag update (S2C 0x22 Turf Flags packet is received).
	/// </summary>
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_FlagReset(short freq, int points)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<S2C_FlagReset>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.FlagReset;
        private readonly short freq = LittleEndianConverter.Convert(freq);
        private readonly int points = LittleEndianConverter.Convert(points);

		#region Helper Properties

		public short Freq => LittleEndianConverter.Convert(freq);

		public int Points => LittleEndianConverter.Convert(points);

		#endregion
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_FlagDrop(short playerId)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<S2C_FlagDrop>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.FlagDrop;
        private readonly short playerId = LittleEndianConverter.Convert(playerId);

		#region Helper Properties

		public short PlayerId => LittleEndianConverter.Convert(playerId);

		#endregion
	}
}
