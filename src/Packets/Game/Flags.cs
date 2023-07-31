using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_FlagLocation
    {
        #region Static members

        public static readonly int Length;

        static S2C_FlagLocation()
        {
            Length = Marshal.SizeOf(typeof(S2C_FlagLocation));
        }

        #endregion

        public readonly byte Type;
        private short flagId;
        private short x;
        private short y;
        private short freq;

        public short FlagId
        {
            get => LittleEndianConverter.Convert(flagId);
            set => flagId = LittleEndianConverter.Convert(value);
        }

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

        public short Freq
        {
            get => LittleEndianConverter.Convert(freq);
            set => freq = LittleEndianConverter.Convert(value);
        }

        public S2C_FlagLocation(short flagId, short x, short y, short freq)
        {
            Type = (byte)S2CPacketType.FlagLoc;
            this.flagId = LittleEndianConverter.Convert(flagId);
            this.x = LittleEndianConverter.Convert(x);
            this.y = LittleEndianConverter.Convert(y);
            this.freq = LittleEndianConverter.Convert(freq);
        }
    }

    /// <summary>
    /// Packet for when a player touches a flag. Either to claim a static flag or to pick up a flag that can be carried.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_TouchFlag
    {
        #region Static members

        public static readonly int Length;

        static C2S_TouchFlag()
        {
            Length = Marshal.SizeOf(typeof(C2S_TouchFlag));
        }

        #endregion

        public readonly byte Type;
        private short flagId;

        public short FlagId => LittleEndianConverter.Convert(flagId);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_FlagPickup
    {
        #region Static members

        public static readonly int Length;

        static S2C_FlagPickup()
        {
            Length = Marshal.SizeOf(typeof(S2C_FlagPickup));
        }

        #endregion

        public readonly byte Type;
        private short flagId;
        private short playerId;

        public short FlagId
        {
            get => LittleEndianConverter.Convert(flagId);
            set => flagId = LittleEndianConverter.Convert(value);
        }
        
        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        public S2C_FlagPickup(short flagId, short playerId)
        {
            Type = (byte)S2CPacketType.FlagPickup;
            this.flagId = LittleEndianConverter.Convert(flagId);
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
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
    public struct S2C_FlagReset
    {
        #region Static members

        public static readonly int Length;

        static S2C_FlagReset()
        {
            Length = Marshal.SizeOf(typeof(S2C_FlagReset));
        }

        #endregion

        public readonly byte Type;
        private short freq;
        private int points;

        public S2C_FlagReset(short freq, int points)
        {
            Type = (byte)S2CPacketType.FlagReset;
            this.freq = LittleEndianConverter.Convert(freq);
            this.points = LittleEndianConverter.Convert(points);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_FlagDrop
    {
        #region Static members

        public static readonly int Length;

        static S2C_FlagDrop()
        {
            Length = Marshal.SizeOf(typeof(S2C_FlagDrop));
        }

        #endregion

        public readonly byte Type;
        private short playerId;

        public S2C_FlagDrop(short playerId)
        {
            Type = (byte)S2CPacketType.FlagDrop;
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
    }
}
