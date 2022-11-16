using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    /// <summary>
    /// Builder for creating a 0x03 (Player Entering) packet cluster, which is simply an array of 0x03 PlayerEnter packets repeated after another. 
    /// Note: The the 0x03 Type byte is included in each item.
    /// </summary>
    /// <remarks>
    /// If the packet size (including the reliable packet that would wrap it) is above the max packet length, 
    /// the Network module will automatically send it using 0x00 0x08 / 0x00 0x09 (big packets).
    /// This is preferable over packet grouping because:
    /// <list type="number">
    /// <item>
    /// Big packets will get filled to their max, whereas there will be some space remaining at the end of clustered packets (doesn't divide evenly: (512 - 6 - 2) / (1 + 64))
    /// </item>
    /// <item>
    /// It will not waste nearly as many buffers compared to sending each player's 0x03 packet separately.
    /// E.g. If there are 150 players in an arena, sending separately means 150 buffers used for adding into the outgoing queue.
    /// Whereas, 1 large packet sent using 0x08/0x09 big packets uses (150 * 64) / 480 = 20 buffers for adding into the outgoing queue.</item>
    /// </list>
    /// </remarks>
    public ref struct S2C_PlayerDataBuilder
    {
        private Span<S2C_PlayerData> _playerDataSpan;

        public S2C_PlayerDataBuilder(Span<byte> buffer)
        {
            _playerDataSpan = MemoryMarshal.Cast<byte, S2C_PlayerData>(buffer);
        }

        public void Set(int index, ref S2C_PlayerData data)
        {
            _playerDataSpan[index] = data;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2C_PlayerData
    {
        #region Static members

        /// <summary>
        /// Number of bytes in a packet.
        /// </summary>
        public static readonly int Length;

        static S2C_PlayerData()
        {
            Length = Marshal.SizeOf<S2C_PlayerData>();
        }

        #endregion

        public byte Type;
        public sbyte Ship;
        public byte AcceptAudio;
        private fixed byte nameBytes[NameBytesLength];
        private fixed byte squadBytes[SquadBytesLength];
        private int killPoints;
        private int flagPoints;
        private short playerId;
        private short freq;
        private short wins;
        private short losses;
        private short attachedTo;
        private short flagsCarried;
        private byte miscBitfield;

        #region Helper properties

        public const int NameBytesLength = 20;
        public Span<byte> NameBytes => MemoryMarshal.CreateSpan(ref nameBytes[0], NameBytesLength);

        public void SetName(ReadOnlySpan<char> value)
        {
            NameBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(NameBytesLength), false);
        }

        public const int SquadBytesLength = 20;
        public Span<byte> SquadBytes => MemoryMarshal.CreateSpan(ref squadBytes[0], SquadBytesLength);

        public void SetSquad(ReadOnlySpan<char> value)
        {
            SquadBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(SquadBytesLength), false);
        }

        public int KillPoints
        {
            get { return LittleEndianConverter.Convert(killPoints); }
            set { killPoints = LittleEndianConverter.Convert(value); }
        }

        public int FlagPoints
        {
            get { return LittleEndianConverter.Convert(flagPoints); }
            set { flagPoints = LittleEndianConverter.Convert(value); }
        }

        public short PlayerId
        {
            get { return LittleEndianConverter.Convert(playerId); }
            set { playerId = LittleEndianConverter.Convert(value); }
        }

        public short Freq
        {
            get { return LittleEndianConverter.Convert(freq); }
            set { freq = LittleEndianConverter.Convert(value); }
        }

        public short Wins
        {
            get { return LittleEndianConverter.Convert(wins); }
            set { wins = LittleEndianConverter.Convert(value); }
        }

        public short Losses
        {
            get { return LittleEndianConverter.Convert(losses); }
            set { losses = LittleEndianConverter.Convert(value); }
        }

        public short AttachedTo
        {
            get { return LittleEndianConverter.Convert(attachedTo); }
            set { attachedTo = LittleEndianConverter.Convert(value); }
        }

        public short FlagsCarried
        {
            get { return LittleEndianConverter.Convert(flagsCarried); }
            set { flagsCarried = LittleEndianConverter.Convert(value); }
        }

        private const byte HasCrownMask = 0b00000001;
        private const byte SendDamageMask = 0b00000010;

        /// <summary>
        /// Whether the player has a crown.
        /// </summary>
        public bool HasCrown
        {
            get { return (miscBitfield & HasCrownMask) != 0; }
            set
            {
                if (value)
                    miscBitfield = (byte)(miscBitfield | HasCrownMask);
                else
                    miscBitfield = (byte)(miscBitfield & ~HasCrownMask);
            }
        }

        /// <summary>
        /// Whether clients should send data for damage done to this player.
        /// </summary>
        // FUTURE: not implemented in continuum yet
        //public bool SendDamage
        //{
        //    get
        //    {
        //        return (miscBitfield & SendDamageMask) != 0;
        //    }
        //    set
        //    {
        //        if (value)
        //            miscBitfield = (byte)(miscBitfield | SendDamageMask);
        //        else
        //            miscBitfield = (byte)(miscBitfield & ~SendDamageMask);
        //    }
        //}

        #endregion
    }
}
