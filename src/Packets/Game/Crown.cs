using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    public enum CrownAction
    {
        /// <summary>
        /// Toggles the crown to be off.
        /// </summary>
        Off = 0,

        /// <summary>
        /// Toggles the crown to be on.
        /// </summary>
        On = 1,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_Crown(CrownAction action, TimeSpan duration, short playerId)
	{
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2C_Crown>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.Crown;
        private readonly byte action = (byte)action;
        private readonly uint duration = LittleEndianConverter.Convert((uint)(duration.TotalMilliseconds / 10));
        private readonly short playerId = LittleEndianConverter.Convert(playerId);

		#region Helper Properties

		public CrownAction Action => (CrownAction)LittleEndianConverter.Convert(action);

        public TimeSpan Duration => TimeSpan.FromMilliseconds(LittleEndianConverter.Convert(duration) * 10);

        public short PlayerId => LittleEndianConverter.Convert(playerId);

        #endregion
    }

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_CrownTimer(bool add, TimeSpan duration)
	{
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2C_CrownTimer>();

        #endregion

        public readonly byte Type = add ? (byte)S2CPacketType.AddCrownTimer : (byte)S2CPacketType.SetCrownTimer;
        private readonly uint duration = LittleEndianConverter.Convert((uint)(duration.TotalMilliseconds / 10));

		#region Helper Properties

		public TimeSpan Duration => TimeSpan.FromMilliseconds(LittleEndianConverter.Convert(duration) * 10);

        #endregion
    }
}
