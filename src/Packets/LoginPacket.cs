using SS.Utilities;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct LoginPacket
    {
        public static readonly int VIELength;
        public static readonly int ContinuumLength;

        static LoginPacket()
        {
            ContinuumLength = Marshal.SizeOf<LoginPacket>();
            VIELength = ContinuumLength - 64;
        }

        public byte Type;
        public byte Flags;

        private const int NameLength = 32;
        private fixed byte nameBytes[NameLength];
        public Span<byte> NameBytes => new(Unsafe.AsPointer(ref nameBytes[0]), NameLength);

        private const int PasswordLength = 32;
        private fixed byte passwordBytes[PasswordLength];
        public Span<byte> PasswordBytes => new(Unsafe.AsPointer(ref passwordBytes[0]), PasswordLength);

        private uint macId;
        private byte blah;
        private ushort timeZoneBias;
        private ushort unk1;
        private short cVersion;
        private int field444;
        private int field555;
        private uint d2;
        private fixed byte blah2[12];
        private fixed byte contId[64]; // continuum only

        public string Name
        {
            get => NameBytes.ReadNullTerminatedASCII();
        }

        public uint MacId
        {
            get => macId;
        }

        public short CVersion
        {
            get => cVersion;
        }

        public uint D2
        {
            get => d2;
        }
    }
}
