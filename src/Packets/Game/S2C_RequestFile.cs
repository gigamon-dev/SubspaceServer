using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Packet that requests the client to send a file.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_RequestFile(ReadOnlySpan<char> path, string filename)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2C_RequestFile>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.RequestForFile;
        public readonly PathInlineArray Path = new(path);
        public readonly FilenameInlineArray Filename = new(filename);

        #region Inline Array Types

        [InlineArray(Length)]
        public struct PathInlineArray
        {
            public const int Length = 256;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;

            public PathInlineArray(ReadOnlySpan<char> value)
            {
                StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length - 1));
            }
        }

        [InlineArray(Length)]
        public struct FilenameInlineArray
        {
            public const int Length = 16;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;

            public FilenameInlineArray(ReadOnlySpan<char> value)
            {
                StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length - 1));
            }
        }

        #endregion
    }
}
