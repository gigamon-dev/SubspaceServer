using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Billing
{
    /// <summary>
    /// The restrictions a billing server can place on a player without preventing them from logging in.
    /// </summary>
    /// <remarks>
    /// The billing server enforces the restrictions on traffic that passes through it and delegates the
    /// rest to the zone, since only the zone can force a ship to spectator mode or drop an arena message.
    /// </remarks>
    [Flags]
    public enum BillingRestrictions : uint
    {
        None = 0x00,

        /// <summary>
        /// The player may not leave spectator mode. Enforced by the zone.
        /// </summary>
        SpecLock = 0x01,

        /// <summary>
        /// The player may not send public (arena) messages. Enforced by the zone.
        /// </summary>
        SilencePublic = 0x02,

        /// <summary>
        /// The player may not send team messages. Enforced by the zone.
        /// </summary>
        SilenceTeam = 0x04,

        /// <summary>
        /// The player may not send private messages to players in the same zone. Enforced by the zone.
        /// </summary>
        SilencePrivate = 0x08,

        /// <summary>
        /// The player may not send private messages to players in other zones.
        /// Enforced by the billing server, which drops them before they reach the zone.
        /// </summary>
        SilenceRemotePrivate = 0x10,

        /// <summary>
        /// The player may not send chat channel messages.
        /// Enforced by the billing server, which drops them before they reach the zone.
        /// </summary>
        SilenceChat = 0x20,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct B2S_PlayerRestrictions
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<B2S_PlayerRestrictions>();

        #endregion

        public readonly byte Type;
        private readonly int connectionId;
        private readonly uint restrictions;

        #region Helper Properties

        public int ConnectionId => LittleEndianConverter.Convert(connectionId);

        /// <summary>
        /// The complete set of restrictions currently on the player, already scoped for the receiving zone.
        /// </summary>
        /// <remarks>
        /// This is not a delta. Apply exactly what is here and lift anything that is not, including when
        /// it is <see cref="BillingRestrictions.None"/>.
        /// </remarks>
        public BillingRestrictions Restrictions => (BillingRestrictions)LittleEndianConverter.Convert(restrictions);

        #endregion
    }
}
