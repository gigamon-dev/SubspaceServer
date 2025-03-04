﻿using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_GoArenaVIE(byte shipType, sbyte wavMsg, sbyte obscenityFilter, short xRes, short yRes, short arenaType, ReadOnlySpan<char> arenaName)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<C2S_GoArenaVIE>();

        #endregion

        public readonly byte Type = (byte)C2SPacketType.GotoArena;
        public readonly byte ShipType = shipType;
        public readonly sbyte WavMsg = wavMsg;
        public readonly sbyte ObscenityFilter = obscenityFilter;
        private readonly short xRes = LittleEndianConverter.Convert(xRes);
        private readonly short yRes = LittleEndianConverter.Convert(yRes);
        private readonly short arenaType = LittleEndianConverter.Convert(arenaType);
        public readonly ArenaNameInlineArray ArenaName = new(arenaName);

        #region Helpers

        public short XRes => LittleEndianConverter.Convert(xRes);

        public short YRes => LittleEndianConverter.Convert(yRes);

        public short ArenaType => LittleEndianConverter.Convert(arenaType);

        #endregion

        [InlineArray(Length)]
        public struct ArenaNameInlineArray
        {
            public const int Length = 16;

            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;

            public ArenaNameInlineArray(ReadOnlySpan<char> value)
            {
                StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length - 1));
            }

            /// <summary>
            /// Converts the arena name from encoded bytes to buffer of characters.
            /// </summary>
            /// <param name="destination">The buffer to write characters to. The buffer provided should be at least 15 characters long.</param>
            /// <returns>The number of characters written to <paramref name="destination"/>.</returns>
            public readonly int Get(Span<char> destination)
            {
                ReadOnlySpan<byte> bytes = StringUtils.SliceNullTerminated((ReadOnlySpan<byte>)this);
                if (bytes.Length >= Length)
                {
                    // The last byte is supposed to be a null-terminator, but it wasn't. Just ignore it.
                    bytes = bytes[..(Length - 1)];
                }

                return StringUtils.DefaultEncoding.GetChars(bytes, destination);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_GoArenaContinuum(byte shipType, sbyte obscenityFilter, sbyte wavMsg, short xRes, short yRes, short arenaType, ReadOnlySpan<char> arenaName, byte optionalGraphics)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<C2S_GoArenaContinuum>();

        #endregion

        public readonly C2S_GoArenaVIE VIE = new(shipType, wavMsg, obscenityFilter, xRes, yRes, arenaType, arenaName);
        public readonly byte OptionalGraphics = optionalGraphics;
    }
}
