using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
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
        public string Name => NameBytes.ReadNullTerminatedString();

        private const int PasswordBytesLength = 32;
        private fixed byte passwordBytes[PasswordBytesLength];
        public Span<byte> PasswordBytes => MemoryMarshal.CreateSpan(ref passwordBytes[0], PasswordBytesLength);
        public string Password => PasswordBytes.ReadNullTerminatedString();

        private uint macId;
        public uint MacId => LittleEndianConverter.Convert(macId);

        private byte blah;

        private ushort timeZoneBias;
        public ushort TimeZoneBias => LittleEndianConverter.Convert(timeZoneBias);

        private ushort unk1;

        private ushort cVersion;
        public ushort CVersion => LittleEndianConverter.Convert(cVersion);

        private int field444;
        private int field555;

        private uint d2;
        public uint D2 => LittleEndianConverter.Convert(d2);

        private fixed byte blah2[12];

        private const int contIdBytesLength = 64;
        private fixed byte contIdBytes[contIdBytesLength]; // continuum only
        public Span<byte> ContId => MemoryMarshal.CreateSpan(ref contIdBytes[0], contIdBytesLength);
    }
}
