using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct ConnectionInitPacket(int key, byte clientType)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<ConnectionInitPacket>();

        #endregion

        public readonly byte T1 = 0x00;
        public readonly byte T2 = 0x01;
        private readonly int key = LittleEndianConverter.Convert(key);
        public readonly byte ClientType = clientType;
        public readonly byte Zero = 0;

        #region Helper Properties

        public int Key => LittleEndianConverter.Convert(key);

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct ConnectionInitResponsePacket(int key)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<ConnectionInitResponsePacket>();

        #endregion

        public readonly byte T1 = 0x00;
        public readonly byte T2 = 0x02;
        private readonly int key = LittleEndianConverter.Convert(key);

        #region Helper Properties

        public int Key => LittleEndianConverter.Convert(key);

        #endregion
    }
}
