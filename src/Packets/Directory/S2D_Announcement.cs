using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Directory
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2D_Announcement
    {
        private static readonly int LengthWithoutDescription;

        static S2D_Announcement()
        {
            LengthWithoutDescription = Marshal.SizeOf<S2D_Announcement>() - DescriptionBytesLength;
        }

        private uint ip;
        public uint IP
        {
            get => LittleEndianConverter.Convert(ip);
            set => ip = LittleEndianConverter.Convert(value);
        }

        private ushort port;
        public ushort Port
        {
            get => LittleEndianConverter.Convert(port);
            set => port = LittleEndianConverter.Convert(value);
        }

        private ushort players;
        public ushort Players
        {
            get => LittleEndianConverter.Convert(players);
            set => players = LittleEndianConverter.Convert(value);
        }

        private ushort scorekeeping;
        public ushort Scorekeeping
        {
            get => LittleEndianConverter.Convert(scorekeeping);
            set => scorekeeping = LittleEndianConverter.Convert(value);
        }

        private uint version;
        public uint Version
        {
            get => LittleEndianConverter.Convert(version);
            set => version = LittleEndianConverter.Convert(value);
        }

        private const int NameBytesLength = 32;
        private fixed byte nameBytes[NameBytesLength];
        public Span<byte> NameBytes => MemoryMarshal.CreateSpan(ref nameBytes[0], NameBytesLength);
        public string Name
        {
            get => NameBytes.ReadNullTerminatedASCII();
            set => NameBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(NameBytesLength - 1));
        }

        private const int PasswordBytesLength = 48;
        private fixed byte passwordBytes[PasswordBytesLength];
        public Span<byte> PasswordBytes => MemoryMarshal.CreateSpan(ref passwordBytes[0], PasswordBytesLength);
        public string Password
        {
            get => PasswordBytes.ReadNullTerminatedASCII();
            set => PasswordBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(PasswordBytesLength - 1));
        }

        private const int DescriptionBytesLength = 386;
        private fixed byte descriptionBytes[DescriptionBytesLength];
        public Span<byte> DescriptionBytes => MemoryMarshal.CreateSpan(ref descriptionBytes[0], DescriptionBytesLength);
        public string Description
        {
            get => DescriptionBytes.ReadNullTerminatedASCII();
            set => DescriptionBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(DescriptionBytesLength - 1));
        }

        /// <summary>
        /// This packet is of variable length based on the <see cref="Description"/>. Use this property to tell how many bytes to send.
        /// </summary>
        public int Length => LengthWithoutDescription + DescriptionBytes.SliceNullTerminated().Length + 1;
    }
}
