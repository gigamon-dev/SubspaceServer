using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.C2S
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct AttachToPacket
    {
        public static readonly int Length;

        static AttachToPacket()
        {
            Length = Marshal.SizeOf<AttachToPacket>();
        }

        public readonly byte Type;
        private readonly short playerId;

        public short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
        }

        public AttachToPacket(short playerId)
        {
            Type = (byte)C2SPacketType.AttachTo;
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
    }
}
