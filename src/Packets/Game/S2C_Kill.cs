using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_Kill(Prize green, short killer, short killed, short bounty, short flags)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2C_Kill>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.Kill;
        public readonly byte Green = (byte)green;
        private readonly short killer = LittleEndianConverter.Convert(killer);
        private readonly short killed = LittleEndianConverter.Convert(killed);
        private readonly short bounty = LittleEndianConverter.Convert(bounty);
        private readonly short flags = LittleEndianConverter.Convert(flags);

        #region Helper Properties

        public short Killer => LittleEndianConverter.Convert(killer);

        public short Killed => LittleEndianConverter.Convert(killed);

        public short Bounty => LittleEndianConverter.Convert(bounty);

        public short Flags => LittleEndianConverter.Convert(flags);

        #endregion
    }
}
