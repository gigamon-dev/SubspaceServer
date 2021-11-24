using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.C2S
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct SpecRequestPacket
    {
        public static readonly int Length;

        static SpecRequestPacket()
        {
            Length = Marshal.SizeOf<SpecRequestPacket>();
        }

        public readonly byte Type;
        private readonly short playerId;

        public short PlayerId
        {
            get {  return LittleEndianConverter.Convert(playerId); }
        }

        public SpecRequestPacket(short playerId)
        {
            Type = (byte)C2SPacketType.SpecRequest;
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
    }
}
