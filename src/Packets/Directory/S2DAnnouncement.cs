using SS.Utilities;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Core.Packets.Directory
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2DAnnouncement
    {
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

        private const int NameLength = 32;
        private fixed byte nameBytes[NameLength];
        public Span<byte> NameBytes => new(Unsafe.AsPointer(ref nameBytes[0]), NameLength);
        public string Name
        {
            get => NameBytes.ReadNullTerminatedASCII();
            set => NameBytes.WriteNullPaddedString(value);
        }

        private const int PasswordLength = 48;
        private fixed byte passwordBytes[PasswordLength];
        public Span<byte> PasswordBytes => new(Unsafe.AsPointer(ref passwordBytes[0]), PasswordLength);
        public string Password
        {
            get => PasswordBytes.ReadNullTerminatedASCII();
            set => PasswordBytes.WriteNullPaddedString(value);
        }

        private const int DescriptionLength = 386;
        private fixed byte descriptionBytes[DescriptionLength];
        public Span<byte> DescriptionBytes => new(Unsafe.AsPointer(ref descriptionBytes[0]), DescriptionLength);
        public string Description
        {
            get => DescriptionBytes.ReadNullTerminatedASCII();
            set => DescriptionBytes.WriteNullPaddedString(value);
        }

        /// <summary>
        /// This packet is of variable length based on the <see cref="Description"/>. Use this property to tell how many bytes to send.
        /// </summary>
        public int Length => Marshal.OffsetOf<S2DAnnouncement>("descriptionBytes").ToInt32() + Description.Length + 1;
    }
}
