using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.S2C
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct KillPacket
    {
        public readonly byte Type;
        public readonly byte Green;
        private readonly short killer;
        private readonly short killed;
        private readonly short bounty;
        private readonly short flags;

        public readonly short Killer
        {
            get {  return LittleEndianConverter.Convert(killer); }
        }

        public readonly short Killed
        {
            get { return LittleEndianConverter.Convert(killed); }
        }

        public readonly short Bounty
        {
            get { return LittleEndianConverter.Convert(bounty); }
        }

        public readonly short Flags
        {
            get { return LittleEndianConverter.Convert(flags); }
        }

        public KillPacket(Prize green, short killer, short killed, short bounty, short flags)
        {
            Type = (byte)S2CPacketType.Kill;
            Green = (byte)green;
            this.killer = LittleEndianConverter.Convert(killer);
            this.killed = LittleEndianConverter.Convert(killed);
            this.bounty = LittleEndianConverter.Convert(bounty);
            this.flags = LittleEndianConverter.Convert(flags);
        }
    }
}
