using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ConnectionInitPacket
    {
        public static readonly int Length;

        static ConnectionInitPacket()
        {
            Length = Marshal.SizeOf<ConnectionInitPacket>();
        }

        public byte T1;
        public byte T2;

        private int key;
        public int Key => LittleEndianConverter.Convert(key);

        public byte ClientType;
        public byte Zero;

        public ConnectionInitPacket(int key, byte clientType)
        {
            T1 = 0x00;
            T2 = 0x01;
            this.key = LittleEndianConverter.Convert(key);
            ClientType = clientType;
            Zero = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ConnectionInitResponsePacket
    {
        public static readonly int Length;

        static ConnectionInitResponsePacket()
        {
            Length = Marshal.SizeOf<ConnectionInitResponsePacket>();
        }

        public byte T1;
        public byte T2;
        private int key;

        public int Key
        {
            get => LittleEndianConverter.Convert(key);
            set => key = LittleEndianConverter.Convert(value);
        }

        public ConnectionInitResponsePacket(int key)
        {
            T1 = 0x00;
            T2 = 0x02;
            this.key = LittleEndianConverter.Convert(key);
        }
    }
}
