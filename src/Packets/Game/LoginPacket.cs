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
            ContinuumLength = Marshal.SizeOf<LoginPacket>();
            VIELength = ContinuumLength - 64;
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
        private fixed byte contIdBytes[contIdBytesLength]; // continuum only
        // TODO: maybe move contIdBytes out of the struct? potentially another client could send a different length of extra data (that could be passed to the biller)?

        #region Helper properties

        private const int NameBytesLength = 32;
        public Span<byte> NameBytes => MemoryMarshal.CreateSpan(ref nameBytes[0], NameBytesLength);
        public string Name => NameBytes.ReadNullTerminatedString(); // TODO: remove string allocation

        private const int PasswordBytesLength = 32;
        public Span<byte> PasswordBytes => MemoryMarshal.CreateSpan(ref passwordBytes[0], PasswordBytesLength);
        public string Password => PasswordBytes.ReadNullTerminatedString(); // TODO: remove string allocation

        public uint MacId => LittleEndianConverter.Convert(macId);

        public ushort TimeZoneBias => LittleEndianConverter.Convert(timeZoneBias);

        public ushort CVersion => LittleEndianConverter.Convert(cVersion);

        public uint D2 => LittleEndianConverter.Convert(d2);

        private const int contIdBytesLength = 64;
        public Span<byte> ContId => MemoryMarshal.CreateSpan(ref contIdBytes[0], contIdBytesLength);

        #endregion
    }
}
