using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Directory
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2D_Announcement
    {
        #region Static Members

        private static readonly int LengthWithoutDescription = Marshal.SizeOf<S2D_Announcement>() - Marshal.SizeOf<DescriptionInlineArray>();

        #endregion

        private uint ip;
        private ushort port;
        private ushort players;
        private ushort scorekeeping;
        private uint version;
        public NameInlineArray Name;
        public PasswordInlineArray Password;
        public DescriptionInlineArray Description;

        #region Helper Properties

        public uint IP
        {
            readonly get => LittleEndianConverter.Convert(ip);
            set => ip = LittleEndianConverter.Convert(value);
        }

        public ushort Port
        {
            readonly get => LittleEndianConverter.Convert(port);
            set => port = LittleEndianConverter.Convert(value);
        }

        public ushort Players
        {
            readonly get => LittleEndianConverter.Convert(players);
            set => players = LittleEndianConverter.Convert(value);
        }

        public ushort Scorekeeping
        {
            readonly get => LittleEndianConverter.Convert(scorekeeping);
            set => scorekeeping = LittleEndianConverter.Convert(value);
        }

        public uint Version
        {
            readonly get => LittleEndianConverter.Convert(version);
            set => version = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// This packet is of variable length based on the <see cref="Description"/>. Use this property to tell how many bytes to send.
        /// </summary>
        public readonly int Length => LengthWithoutDescription + ((ReadOnlySpan<byte>)Description).SliceNullTerminated().Length + 1;

        #endregion

        #region Inline Array Types

        [InlineArray(Length)]
        public struct NameInlineArray
        {
            public const int Length = 32;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;

            public NameInlineArray(ReadOnlySpan<char> name)
            {
                Set(name);
            }

            public static implicit operator NameInlineArray(string value)
            {
                return new(value);
            }

            public int Get(Span<char> destination)
            {
                Span<byte> bytes = ((Span<byte>)this).SliceNullTerminated();
                return StringUtils.DefaultEncoding.GetChars(bytes, destination);
            }

            public void Set(ReadOnlySpan<char> value)
            {
                StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length - 1));
            }

            public void Clear()
            {
                ((Span<byte>)this).Clear();
            }
        }

        [InlineArray(Length)]
        public struct PasswordInlineArray
        {
            public const int Length = 48;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;

            public PasswordInlineArray(ReadOnlySpan<char> name)
            {
                Set(name);
            }

            public static implicit operator PasswordInlineArray(string value)
            {
                return new(value);
            }

            public int Get(Span<char> destination)
            {
                Span<byte> bytes = ((Span<byte>)this).SliceNullTerminated();
                return StringUtils.DefaultEncoding.GetChars(bytes, destination);
            }

            public void Set(ReadOnlySpan<char> value)
            {
                StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length - 1));
            }

            public void Clear()
            {
                ((Span<byte>)this).Clear();
            }
        }

        [InlineArray(Length)]
        public struct DescriptionInlineArray
        {
            public const int Length = 386;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;

            public DescriptionInlineArray(ReadOnlySpan<char> name)
            {
                Set(name);
            }

            public static implicit operator DescriptionInlineArray(string value)
            {
                return new(value);
            }

            public int Get(Span<char> destination)
            {
                Span<byte> bytes = ((Span<byte>)this).SliceNullTerminated();
                return StringUtils.DefaultEncoding.GetChars(bytes, destination);
            }

            public void Set(ReadOnlySpan<char> value)
            {
                StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length - 1));
            }

            public void Clear()
            {
                ((Span<byte>)this).Clear();
            }
        }

        #endregion
    }
}
