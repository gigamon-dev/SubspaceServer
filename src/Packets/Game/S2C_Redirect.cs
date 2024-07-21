using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Packet that tells the client to switch to another server.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_Redirect(uint ip, ushort port, short arenaType, ReadOnlySpan<char> arenaName, uint loginId)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2C_Redirect>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.Redirect;
        private uint ip = LittleEndianConverter.Convert(ip);
        private ushort port = LittleEndianConverter.Convert(port);
        private short arenaType = LittleEndianConverter.Convert(arenaType); // Same values as in the ?go packet
        public ArenaNameInlineArray ArenaName = new(arenaName);
        private uint loginId = LittleEndianConverter.Convert(loginId);

        #region Inline Array Types

        [InlineArray(Length)]
        public struct ArenaNameInlineArray
        {
            public const int Length = 16;

            [SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;

            public ArenaNameInlineArray(ReadOnlySpan<char> value)
            {
                Set(value);
            }

            public void Set(ReadOnlySpan<char> value)
            {
                StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length - 1));
            }
        }

        #endregion
    }
}
