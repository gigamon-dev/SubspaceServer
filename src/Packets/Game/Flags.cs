using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Packet for when a player touches a flag. Either to claim a static flag or to pick up a flag that can be carried.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct C2S_TouchFlag
    {
        public static readonly int Length;

        static C2S_TouchFlag()
        {
            Length = Marshal.SizeOf(typeof(C2S_TouchFlag));
        }

        public readonly byte Type;
        private short flagId;

        public short FlagId => LittleEndianConverter.Convert(flagId);
    }
}
