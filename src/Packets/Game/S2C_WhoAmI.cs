using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// S2C 0x01: tells the client its player id when it enters an arena.
    /// </summary>
    /// <remarks>
    /// The original form is 3 bytes. For Continuum, the arena name and its null terminator can follow the player id,
    /// which <see cref="SetArenaName"/> adds. Continuum accepts 3 to 19 bytes and drops a longer packet.
    /// <para>
    /// Continuum keeps the name as the arena it is in:
    /// <list type="bullet">
    /// <item>3 bytes: no name, so it keeps the name it last asked for (empty for a public arena). That is wrong when the
    /// server placed or moved the player to a different arena.</item>
    /// <item>4 bytes: an empty name, meaning a public arena.</item>
    /// <item>5 to 19 bytes: the arena name. Continuum treats the last byte as the null terminator.</item>
    /// </list>
    /// The name selects the arena's folder under the zone folder (the "public" folder when empty), whose files take
    /// precedence over the zone's. Continuum only saves and restores a ship (Misc:AllowSavedShips) while the name is empty,
    /// so a public arena should be sent an empty name, not its "0", "1", ... name.
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct S2C_WhoAmI(short playerId)
    {
        #region Static Members

        /// <summary>
        /// The # of bytes without the arena name.
        /// </summary>
        public const int Length = 3;

        /// <summary>
        /// The maximum # of bytes, with the arena name.
        /// </summary>
        public static readonly int MaxLength = Marshal.SizeOf<S2C_WhoAmI>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.WhoAmI;
        private readonly short playerId = LittleEndianConverter.Convert(playerId);
        private ArenaNameInlineArray arenaName;

        #region Helper Properties

        public short PlayerId => LittleEndianConverter.Convert(playerId);

        #endregion

        #region Methods

        /// <summary>
        /// Appends the arena name, for Continuum clients.
        /// </summary>
        /// <param name="value">The arena name, or empty for a public arena. It is truncated to fit (15 bytes), since Continuum drops a packet of more than 19 bytes.</param>
        /// <returns>Number of bytes for the packet.</returns>
        public int SetArenaName(ReadOnlySpan<char> value)
        {
            return Length + arenaName.Set(value);
        }

        #endregion

        #region Inline Array Types

        [InlineArray(Length)]
        private struct ArenaNameInlineArray
        {
            /// <summary>
            /// 15 characters and a null terminator.
            /// </summary>
            public const int Length = 16;

            [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
            private byte _element0;

            public int Set(ReadOnlySpan<char> value)
            {
                return StringUtils.WriteNullTerminatedString(this, value.TruncateForEncodedByteLimit(Length - 1));
            }
        }

        #endregion
    }
}
