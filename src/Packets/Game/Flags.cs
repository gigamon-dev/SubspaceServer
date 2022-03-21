using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Packet for when a player touches a flag. Either to claim a static flag or to pick up a flag that can be carried.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_TouchFlag
    {
        public static readonly int Length;

        static C2S_TouchFlag()
        {
            Length = Marshal.SizeOf(typeof(C2S_TouchFlag));
        }

        public readonly byte Type;
        private short flagId;

        public short FlagId => LittleEndianConverter.Convert(flagId);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_FlagPickup
    {
        public static readonly int Length;

        static S2C_FlagPickup()
        {
            Length = Marshal.SizeOf(typeof(S2C_FlagPickup));
        }

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
}
