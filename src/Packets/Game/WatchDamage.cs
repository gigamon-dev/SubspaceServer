using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct DamageData
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<DamageData>();

        #endregion

        private readonly short attackerPlayerId;
        public readonly WeaponData WeaponData;
        private readonly short energy;
        private readonly short damage;
        private readonly byte unknown;

        #region Helper Properties

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

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_WatchDamageHeader(short damagedPlayerId, ServerTick timestamp)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2C_WatchDamageHeader>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.Damage;
        private readonly short damagedPlayerId = LittleEndianConverter.Convert(damagedPlayerId);
        private readonly uint timestamp = LittleEndianConverter.Convert((uint)timestamp);

        #region Helper Properties

        public short DamagedPlayerId => LittleEndianConverter.Convert(damagedPlayerId);

        public ServerTick Timestamp => LittleEndianConverter.Convert(timestamp);

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_WatchDamageHeader
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<C2S_WatchDamageHeader>();

        #endregion

        public readonly byte Type;
        private readonly uint timestamp;

        #region Helper Properties

        public ServerTick Timestamp => new(LittleEndianConverter.Convert(timestamp));

        #endregion
    }
}
