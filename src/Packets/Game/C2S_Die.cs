using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_Die
    {
        public static readonly int Length;

        static C2S_Die()
        {
            Length = Marshal.SizeOf<C2S_Die>();
        }

        public readonly byte Type;
        private readonly short killer;
        private readonly short bounty;

        public short Killer
        {
            get { return LittleEndianConverter.Convert(killer); }
        }

        public short Bounty
        {
            get { return LittleEndianConverter.Convert(bounty); }
        }

        public C2S_Die(short killer, short bounty)
        {
            Type = (byte)C2SPacketType.Die;
            this.killer = LittleEndianConverter.Convert(killer);
            this.bounty = LittleEndianConverter.Convert(bounty);
        }
    }
}
