using SS.Utilities;
using System;
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

        private const int NameBytesLength = 32;
        private fixed byte nameBytes[NameBytesLength];
        public Span<byte> NameBytes => MemoryMarshal.CreateSpan(ref nameBytes[0], NameBytesLength);

        private const int PasswordBytesLength = 32;
        private fixed byte passwordBytes[PasswordBytesLength];
        public Span<byte> PasswordBytes => MemoryMarshal.CreateSpan(ref passwordBytes[0], PasswordBytesLength);

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
            get => LittleEndianConverter.Convert(macId);
        }

        public short CVersion
        {
            get => LittleEndianConverter.Convert(cVersion);
        }

        public uint D2
        {
            get => LittleEndianConverter.Convert(d2);
        }
    }
}
