using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.C2S
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct DiePacket
    {
        public static readonly int Length;

        static DiePacket()
        {
            Length = Marshal.SizeOf<DiePacket>();
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

        public DiePacket(short killer, short bounty)
        {
            Type = (byte)C2SPacketType.Die;
            this.killer = LittleEndianConverter.Convert(killer);
            this.bounty = LittleEndianConverter.Convert(bounty);
        }
    }
}
