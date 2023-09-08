using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DamageData
    {
        #region Static members

        public static int Length;

        static DamageData()
        {
            Length = Marshal.SizeOf(typeof(DamageData));
        }

        #endregion

        private short attackerPlayerId;
        public WeaponData WeaponData;
        private short energy;
        private short damage;
        private byte unknown;

        /// <summary>
        /// Id of the player that inflicted the damage.
        /// </summary>
        /// <remarks>
        /// For damage taken from a wormhole, this will be the player's own PlayerId.
        /// </remarks>
        public short AttackerPlayerId => LittleEndianConverter.Convert(attackerPlayerId);
        
        /// <summary>
        /// The amount of energy the player had when the damage occurred.
        /// </summary>
        public short Energy => LittleEndianConverter.Convert(energy);

        /// <summary>
        /// The amount of damage inflicted.
        /// </summary>
        /// <remarks>
        /// This can be greater than <see cref="Energy"/> if it's the killing blow or if the player self-inflicted damage.
        /// </remarks>
        public short Damage => LittleEndianConverter.Convert(damage);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_WatchDamageHeader
    {
        #region Static members

        public static int Length;

        static S2C_WatchDamageHeader()
        {
            Length = Marshal.SizeOf(typeof(S2C_WatchDamageHeader));
        }

        #endregion

        public readonly byte Type;
        private short damagedPlayerId;
        private uint timestamp;

        public short DamagedPlayerId => LittleEndianConverter.Convert(damagedPlayerId);

        public ServerTick Timestamp => LittleEndianConverter.Convert(timestamp);

        public S2C_WatchDamageHeader(short damagedPlayerId, ServerTick timestamp)
        {
            Type = (byte)S2CPacketType.Damage;
            this.damagedPlayerId = LittleEndianConverter.Convert(damagedPlayerId);
            this.timestamp = LittleEndianConverter.Convert((uint)timestamp);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_WatchDamageHeader
    {
        #region Static members

        public static int Length;

        static C2S_WatchDamageHeader()
        {
            Length = Marshal.SizeOf(typeof(C2S_WatchDamageHeader));
        }

        #endregion

        public readonly byte Type;
        private uint timestamp;

        public ServerTick Timestamp => new(LittleEndianConverter.Convert(timestamp));
    }
}
