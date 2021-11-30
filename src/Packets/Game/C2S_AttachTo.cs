using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_AttachTo
    {
        public static readonly int Length;

        static C2S_AttachTo()
        {
            Length = Marshal.SizeOf<C2S_AttachTo>();
        }

        public readonly byte Type;
        private readonly short playerId;

        public short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
        }

        public C2S_AttachTo(short playerId)
        {
            Type = (byte)C2SPacketType.AttachTo;
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
    }
}
