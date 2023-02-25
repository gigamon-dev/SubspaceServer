using SS.Packets.Game;
using System;

namespace SS.Core.ComponentInterfaces
{
    /// <summary>
    /// Identifies an individual setting within a <see cref="S2C_ClientSettings"/> packet.
    /// </summary>
    public readonly struct ClientSettingIdentifier : ISpanFormattable
    {
        internal ClientSettingIdentifier(bool isSigned, ClientSettingIdentifierFieldType fieldType, int byteOffset, int bitOffset, int bitLength)
        {
            IsSigned = isSigned;
            FieldType = fieldType;
            ByteOffset = byteOffset;
            BitOffset = bitOffset;
            BitLength = bitLength;

            IsValid(true);
        }

        private readonly uint _bitfield;

        private const uint SignMask       = 0b_10000000_00000000_00000000_00000000;
        private const uint FieldTypeMask  = 0b_01100000_00000000_00000000_00000000;
        private const uint ByteOffsetMask = 0b_00001111_11111111_11110000_00000000;
        private const uint BitOffsetMask  = 0b_00000000_00000000_00001111_11000000;
        private const uint BitLengthMask  = 0b_00000000_00000000_00000000_00111111;

        /// <summary>
        /// Whether the client setting is a signed integer.
        /// </summary>
        public bool IsSigned
        {
            get => (_bitfield & SignMask) != 0;
            private init => _bitfield = (_bitfield & ~SignMask) | (value ? SignMask : 0);
        }

        /// <summary>
        /// The underlying type of field the client setting is stored within.
        /// For client settings that are stored as bit fields, this denotes the parent field type.
        /// </summary>
        internal ClientSettingIdentifierFieldType FieldType
        {
            get => (ClientSettingIdentifierFieldType)((_bitfield & FieldTypeMask) >> 29);
            private init => _bitfield = (_bitfield & ~FieldTypeMask) | (((uint)value << 29) & FieldTypeMask);
        }

        /// <summary>
        /// The number of bytes the field is located from the beginning of a <see cref="S2C_ClientSettings"/> packet.
        /// </summary>
        internal int ByteOffset
        {
            get => (int)((_bitfield & ByteOffsetMask) >> 12);
            private init => _bitfield = (_bitfield & ~ByteOffsetMask) | (((uint)value << 12) & ByteOffsetMask);
        }

        /// <summary>
        /// The number of bits offset from the lowest order bit of the field.
        /// In other words, the number of bits to left-shift to get to the lowest order bit of the client setting.
        /// </summary>
        internal int BitOffset
        {
            get => (int)((_bitfield & BitOffsetMask) >> 6);
            private init => _bitfield = (_bitfield & ~BitOffsetMask) | (((uint)value << 6) & BitOffsetMask);
        }

        /// <summary>
        /// The number of bits that the client setting is stored as.
        /// </summary>
        public int BitLength
        {
            get => (int)(_bitfield & BitLengthMask);
            private init => _bitfield = (_bitfield & ~BitLengthMask) | ((uint)value & BitLengthMask);
        }

        internal void Deconstruct(out bool isSigned, out ClientSettingIdentifierFieldType fieldType, out int byteOffset, out int bitOffset, out int bitLength)
        {
            isSigned = IsSigned;
            fieldType = FieldType;
            byteOffset = ByteOffset;
            bitOffset = BitOffset;
            bitLength = BitLength;
        }

        /// <summary>
        /// Gets whether the value refers to a valid client setting.
        /// </summary>
        /// <remarks>
        /// For example, if not properly constructed
        /// <code>ClientSettingsIdentifier foo = new();</code>
        /// or
        /// <code>ClientSettingsIdentifier bar = default;</code>
        /// </remarks>
        /// <returns></returns>
        public bool IsValid()
        {
            return IsValid(false);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2208:Instantiate argument exceptions correctly", Justification = "Helper method, paramName matches that of the contructor.")]
        private bool IsValid(bool throwOnInvalid = false)
        {
            (bool isSigned, ClientSettingIdentifierFieldType fieldType, int byteOffset, int bitOffset, int bitLength) = this;

            if (!Enum.IsDefined(fieldType))
                return throwOnInvalid ? throw new ArgumentOutOfRangeException(nameof(fieldType), "Is not a defined value.") : false;

            if (byteOffset < 0)
                return throwOnInvalid ? throw new ArgumentOutOfRangeException(nameof(byteOffset), "Cannot be negative.") : false;

            if (bitOffset < 0)
                return throwOnInvalid ? throw new ArgumentOutOfRangeException(nameof(bitOffset), "Cannot be negative.") : false;

            if (bitLength <= 0)
                return throwOnInvalid ? throw new ArgumentOutOfRangeException(nameof(bitLength), "Must be > 0.") : false;

            int fieldBytes = fieldType switch
            {
                ClientSettingIdentifierFieldType.Bit8 => 1,
                ClientSettingIdentifierFieldType.Bit16 => 2,
                ClientSettingIdentifierFieldType.Bit32 => 4,
                _ => 0,
            };

            if (fieldBytes == 0)
                return throwOnInvalid ? throw new ArgumentOutOfRangeException(nameof(fieldType)) : false;

            if (byteOffset + fieldBytes - 1 >= S2C_ClientSettings.Length)
                return throwOnInvalid ? throw new ArgumentException("Location specified falls outside of a S2C Client Settings packet.") : false;

            if (fieldType == ClientSettingIdentifierFieldType.Bit8 && bitLength > 8)
                return throwOnInvalid ? throw new ArgumentOutOfRangeException(nameof(bitLength), "Length cannot be > 8 for 8-bit fields.") : false;

            if (fieldType == ClientSettingIdentifierFieldType.Bit16 && bitLength > 16)
                return throwOnInvalid ? throw new ArgumentOutOfRangeException(nameof(bitLength), "Length cannot be > 16 for 16-bit fields.") : false;

            if (fieldType == ClientSettingIdentifierFieldType.Bit32 && bitLength > 32)
                return throwOnInvalid ? throw new ArgumentOutOfRangeException(nameof(bitLength), "Length cannot be > 32 for 32-bit fields.") : false;

            if (fieldType == ClientSettingIdentifierFieldType.Bit8 && bitOffset + bitLength > 8)
                return throwOnInvalid ? throw new ArgumentException("Length + offset cannot be > 8 for 8-bit fields.") : false;

            if (fieldType == ClientSettingIdentifierFieldType.Bit16 && bitOffset + bitLength > 16)
                return throwOnInvalid ? throw new ArgumentException("Length + offset cannot be > 16 for 16-bit fields.") : false;

            if (fieldType == ClientSettingIdentifierFieldType.Bit32 && bitOffset + bitLength > 32)
                return throwOnInvalid ? throw new ArgumentException("Length + offset cannot be > 32 for 32-bit fields.") : false;

            if (bitLength < 2 && isSigned)
                return throwOnInvalid ? throw new ArgumentOutOfRangeException(nameof(bitLength), "Length must be > 1 for signed integers.") : false;

            if (byteOffset + fieldBytes - 1 >= S2C_ClientSettings.Length)
                return throwOnInvalid ? throw new ArgumentOutOfRangeException(nameof(bitLength), "Is outside the length of a S2C Client Settings packet.") : false;

            return true;
        }

        #region ISpanFormattable

        public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider provider)
        {
            return _bitfield.TryFormat(destination, out charsWritten, format, provider);
        }

        public string ToString(string format, IFormatProvider formatProvider)
        {
            return _bitfield.ToString(format, formatProvider);
        }

        #endregion
    }

    /// <summary>
    /// Represents the size of the an underlying field used to store a client setting.
    /// </summary>
    /// <remarks>
    /// For bit fields, this is the field that contains the bit field.
    /// </remarks>
    internal enum ClientSettingIdentifierFieldType
    {
        Bit8  = 0x00,
        Bit16 = 0x01,
        Bit32 = 0x02,
    }

    /// <summary>
    /// Interface for a service that manages the client settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Client settings are the settings that are sent to the client in the <see cref="S2C_ClientSettings"/> packet. 
    /// Client settings include ship settings and much more.
    /// Client settings are sent to a player: upon entering an arena, when a client setting is changed in the conf file, or programatically via <see cref="SendClientSettings(Player)"/>.
    /// </para>
    /// <para>
    /// Client settings are read from an arena's config.
    /// Client settings can be programatically overridden for an entire arena or individual players.
    /// If a client setting is overridden for both a player and the arena, the player override takes precedence.
    /// </para>
    /// </remarks>
    public interface IClientSettings : IComponentInterface
    {
        /// <summary>
        /// Sends client settings to a player.
        /// </summary>
        /// <remarks>
        /// This is called by the <see cref="Modules.ArenaManager"/> module when a player enters an arena.
        /// <para>
        /// Modules that override settings will also need to call it to send updated settings to clients.
        /// The client settings packet is a very large packet, so it is recommended to reduce sending it to a minimum.
        /// Therefore, if overriding multiple settings, first override all the settings, then call <see cref="SendClientSettings(Player)"/> once.
        /// </para>
        /// </remarks>
        /// <param name="player">The player to send settings to.</param>
        void SendClientSettings(Player player);

        /// <summary>
        /// Gets the checksum of a player's client settings.
        /// </summary>
        /// <remarks>This is used by the <see cref="Modules.Security"/> module to validate checksums.</remarks>
        /// <param name="player">The player to get the client-side settings checksum for.</param>
        /// <param name="key">The key to use when generating the checksum.</param>
        /// <returns>The checksum.</returns>
        uint GetChecksum(Player player, uint key);

        /// <summary>
        /// Generates a random prize for an arena based on client settings.
        /// </summary>
        /// <remarks>
        /// If the Prize:UseDeathPrizeWeights setting is enabled, the weights configured in the [DPrizeWeight] section are used.
        /// Otherwise, the weights configured in the [PrizeWeight] section are used.
        /// </remarks>
        /// <param name="arena">The arena to get a random prize for.</param>
        /// <returns>A random prize.</returns>
        Prize GetRandomPrize(Arena arena);

        /// <summary>
        /// Gets an identifier for a client setting by its associated config <paramref name="section"/> + <paramref name="key"/> pair.
        /// </summary>
        /// <param name="section">The config section of the client setting.</param>
        /// <param name="key">The config key of the client setting.</param>
        /// <param name="id">When this method returns, the identifier, if the <paramref name="section"/> and <paramref name="key"/> was to a valid client setting.</param>
        /// <returns><see langword="true"/> if the <paramref name="section"/> and <paramref name="key"/> was to a valid client setting. Otherwise, <see langword="false"/>.</returns>
        bool TryGetSettingsIdentifier(ReadOnlySpan<char> section, ReadOnlySpan<char> key, out ClientSettingIdentifier id);

        /// <summary>
        /// Overrides a client setting for an arena.
        /// </summary>
        /// <remarks>
        /// This method does NOT automatically send the settings to player(s).
        /// Remember to call <see cref="SendClientSettings(Player)"/> for the affected players after [un]overriding 1 or more settings.
        /// </remarks>
        /// <param name="arena">The arena to override a setting for.</param>
        /// <param name="id">Identifies the setting to override.</param>
        /// <param name="value">The value to override the setting to.</param>
        void OverrideSetting(Arena arena, ClientSettingIdentifier id, int value);

        /// <summary>
        /// Overrides a client setting for a player.
        /// </summary>
        /// <remarks>
        /// This method does NOT automatically send the settings to player(s).
        /// Remember to call <see cref="SendClientSettings(Player)"/> for the affected players after [un]overriding 1 or more settings.
        /// </remarks>
        /// <param name="player">The player to override a setting for.</param>
        /// <param name="id">Identifies the setting to override.</param>
        /// <param name="value">The value to override the setting to.</param>
        void OverrideSetting(Player player, ClientSettingIdentifier id, int value);

        /// <summary>
        /// Unoverrides a client setting for an arena.
        /// </summary>
        /// <remarks>
        /// This method does NOT automatically send the settings to player(s).
        /// Remember to call <see cref="SendClientSettings(Player)"/> for the affected players after [un]overriding 1 or more settings.
        /// </remarks>
        /// <param name="arena">The arena to unoverride a setting for.</param>
        /// <param name="id">Identifies the setting to unoverride.</param>
        void UnoverrideSetting(Arena arena, ClientSettingIdentifier id);

        /// <summary>
        /// Unoverrides a client setting for a player.
        /// </summary>
        /// <remarks>
        /// This method does NOT automatically send the settings to player(s).
        /// Remember to call <see cref="SendClientSettings(Player)"/> for the affected players after [un]overriding 1 or more settings.
        /// </remarks>
        /// <param name="player">The player to unoverride a setting for.</param>
        /// <param name="id">Identifies the setting to unoverride.</param>
        void UnoverrideSetting(Player player, ClientSettingIdentifier id);

        /// <summary>
        /// Gets a client setting override value for an arena.
        /// </summary>
        /// <param name="arena">The arena to get the setting for.</param>
        /// <param name="id">Identifies the setting to get.</param>
        /// <param name="value">When this method returns, contains the overridden value, if overriden. Otherwise, 0.</param>
        /// <returns><see langword="true"/> if the setting is overridden; otherwise <see langword="false"/>.</returns>
        bool TryGetSettingOverride(Arena arena, ClientSettingIdentifier id, out int value);

        /// <summary>
        /// Gets a client setting override value for a player.
        /// </summary>
        /// <param name="player">The player to get the setting for.</param>
        /// <param name="id">Identifies the setting to get.</param>
        /// <param name="value">When this method returns, contains the overridden value, if overriden. Otherwise, 0.</param>
        /// <returns><see langword="true"/> if the setting is overridden; otherwise <see langword="false"/>.</returns>
        bool TryGetSettingOverride(Player player, ClientSettingIdentifier id, out int value);

        /// <summary>
        /// Gets the configured value of a client setting for an arena.
        /// This is the value based on the arena.conf, without any override logic.
        /// </summary>
        /// <param name="arena">The arena to get the setting for.</param>
        /// <param name="id">Identifies the setting to get.</param>
        /// <returns>The value of the setting.</returns>
        int GetSetting(Arena arena, ClientSettingIdentifier id);

        /// <summary>
        /// Gets the current value of a client setting for a player.
        /// This is the value last sent to the player, which may be due to an override.
        /// </summary>
        /// <param name="player">The player to get the setting for.</param>
        /// <param name="id">Identifies the setting to get.</param>
        /// <returns>The value of the setting.</returns>
        int GetSetting(Player player, ClientSettingIdentifier id);
    }
}
