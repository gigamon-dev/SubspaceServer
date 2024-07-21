using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_Die(short killer, short bounty)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<C2S_Die>();

        #endregion

        public readonly byte Type = (byte)C2SPacketType.Die;
        private readonly short killer = LittleEndianConverter.Convert(killer);
        private readonly short bounty = LittleEndianConverter.Convert(bounty);

        #region Helper Properties

        public short Killer => LittleEndianConverter.Convert(killer);

        public short Bounty => LittleEndianConverter.Convert(bounty);

        #endregion
    }
}
