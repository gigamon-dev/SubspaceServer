﻿using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct S2C_PlayerData
    {
        private byte _type;
        public sbyte Ship;
        public byte AcceptAudio;

        public const int NameBytesLength = 20;
        private fixed byte nameBytes[NameBytesLength];
        public Span<byte> NameBytes => MemoryMarshal.CreateSpan(ref nameBytes[0], NameBytesLength);

        public const int SquadBytesLength = 20;
        private fixed byte squadBytes[SquadBytesLength];
        public Span<byte> SquadBytes => MemoryMarshal.CreateSpan(ref squadBytes[0], SquadBytesLength);

        private int killPoints;
        private int flagPoints;
        private short playerId;
        private short freq;
        private short wins;
        private short losses;
        private short attachedTo;
        private short flagsCarried;
        private byte miscBitfield;

        private const byte HasCrownMask = 0b00000001;
        private const byte SendDamageMask = 0b00000010;

        public byte Type
        {
            get { return _type; }
            init { _type = value; }
        }

        public string Name
        {
            get { return NameBytes.ReadNullTerminatedString(); }
            set { NameBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(NameBytesLength), false); }
        }

        public string Squad
        {
            get { return SquadBytes.ReadNullTerminatedString(); }
            set { SquadBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(SquadBytesLength), false); }
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

        /// <summary>
        /// Whether the player has a crown.
        /// </summary>
        private bool HasCrown
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
        //private bool SendDamage
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
    }
}