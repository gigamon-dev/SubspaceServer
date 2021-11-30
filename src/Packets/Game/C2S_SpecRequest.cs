using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_SpecRequest
    {
        public static readonly int Length;

        static C2S_SpecRequest()
        {
            Length = Marshal.SizeOf<C2S_SpecRequest>();
        }

        public readonly byte Type;
        private readonly short playerId;

        public short PlayerId
        {
            get {  return LittleEndianConverter.Convert(playerId); }
        }

        public C2S_SpecRequest(short playerId)
        {
            Type = (byte)C2SPacketType.SpecRequest;
            this.playerId = LittleEndianConverter.Convert(playerId);
        }
    }
}
