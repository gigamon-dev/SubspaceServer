using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct LoginPacket
    {
        #region Static members

        public static readonly int VIELength;
        public static readonly int ContinuumLength;

        static LoginPacket()
        {
            VIELength = Marshal.SizeOf<LoginPacket>();
            ContinuumLength = VIELength + 64;
        }

        #endregion

        public byte Type;
        public byte Flags;
        private fixed byte nameBytes[NameBytesLength];
        private fixed byte passwordBytes[PasswordBytesLength];
        private uint macId;
        private byte blah;
        private ushort timeZoneBias;
        private ushort unk1;
        private ushort cVersion;
        private int field444;
        private int field555;
        private uint d2;
        private fixed byte blah2[12];
        // The continuum login packet (0x24) has 64 more bytes (continuum id field) that come next (not included in this struct).
        // The zone server doesn't know how to interpret the bytes. It just passes them to the billing server.

        #region Helper properties

        private const int NameBytesLength = 32;
        public Span<byte> NameBytes => MemoryMarshal.CreateSpan(ref nameBytes[0], NameBytesLength);

        private const int PasswordBytesLength = 32;
        public Span<byte> PasswordBytes => MemoryMarshal.CreateSpan(ref passwordBytes[0], PasswordBytesLength);

        public uint MacId => LittleEndianConverter.Convert(macId);

        public ushort TimeZoneBias => LittleEndianConverter.Convert(timeZoneBias);

        public ushort CVersion => LittleEndianConverter.Convert(cVersion);

        public uint D2 => LittleEndianConverter.Convert(d2);

        #endregion
    }
}
