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
    public struct S2C_Crown
    {
        #region Static members

        public static int Length;

        static S2C_Crown()
        {
            Length = Marshal.SizeOf(typeof(S2C_Crown));
        }

        #endregion

        public readonly byte Type;
        private byte action;
        private uint duration;
        private short playerId;

        public CrownAction Action => (CrownAction)LittleEndianConverter.Convert(action);

        public TimeSpan Duration => TimeSpan.FromMilliseconds(LittleEndianConverter.Convert(duration) * 10);

        public short PlayerId => LittleEndianConverter.Convert(playerId);

        public S2C_Crown(CrownAction action, TimeSpan duration, short playerId)
        {
            Type = (byte)S2CPacketType.Crown;
            this.action = (byte)action;
            this.duration = LittleEndianConverter.Convert((uint)(duration.TotalMilliseconds / 10));
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_CrownTimer
    {
        #region Static members

        public static int Length;

        static S2C_CrownTimer()
        {
            Length = Marshal.SizeOf(typeof(S2C_CrownTimer));
        }

        #endregion

        public readonly byte Type;
        private uint duration;

        public TimeSpan Duration => TimeSpan.FromMilliseconds(LittleEndianConverter.Convert(duration) * 10);

        public S2C_CrownTimer(bool add, TimeSpan duration)
        {
            Type = add ? (byte)S2CPacketType.AddCrownTimer : (byte)S2CPacketType.SetCrownTimer;
            this.duration = LittleEndianConverter.Convert((uint)(duration.TotalMilliseconds / 10));
        }
    }
}
