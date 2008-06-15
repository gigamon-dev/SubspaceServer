using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct PlayerDataPacket
    {
        static PlayerDataPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            pktype = locationBuilder.CreateDataLocation(8);
            ship = locationBuilder.CreateDataLocation(8);
            acceptaudio = locationBuilder.CreateDataLocation(8);
            name = locationBuilder.CreateDataLocation(8 * 20);
            squad = locationBuilder.CreateDataLocation(8 * 20);
            killpoints = locationBuilder.CreateDataLocation(32);
            flagpoints = locationBuilder.CreateDataLocation(32);
            pid = locationBuilder.CreateDataLocation(16);
            freq = locationBuilder.CreateDataLocation(16);
            wins = locationBuilder.CreateDataLocation(16);
            losses = locationBuilder.CreateDataLocation(16);
            attachedto = locationBuilder.CreateDataLocation(16);
            flagscarried = locationBuilder.CreateDataLocation(16);
            miscbits = locationBuilder.CreateDataLocation(8);
            Length = locationBuilder.NumBytes;
        }

        private static readonly DataLocation pktype;
        private static readonly DataLocation ship;
        private static readonly DataLocation acceptaudio;
        private static readonly DataLocation name;
        private static readonly DataLocation squad;
        private static readonly DataLocation killpoints;
        private static readonly DataLocation flagpoints;
        private static readonly DataLocation pid;
        private static readonly DataLocation freq;
        private static readonly DataLocation wins;
        private static readonly DataLocation losses;
        private static readonly DataLocation attachedto;
        private static readonly DataLocation flagscarried;
        private static readonly DataLocation miscbits;
        public static readonly int Length;

        private readonly byte[] data;

        public PlayerDataPacket(byte[] data)
        {
            this.data = data;
        }

        public byte[] Bytes
        {
            get { return data; }
        }

        public byte PkType
        {
            get { return ExtendedBitConverter.ToByte(data, pktype.ByteOffset, pktype.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, pktype.ByteOffset, pktype.BitOffset, pktype.NumBits); }
        }

        public sbyte Ship
        {
            get { return ExtendedBitConverter.ToSByte(data, ship.ByteOffset, ship.BitOffset); }
            set { ExtendedBitConverter.WriteSByteBits(value, data, ship.ByteOffset, ship.BitOffset, ship.NumBits); }
        }

        public byte AcceptAudio
        {
            get { return ExtendedBitConverter.ToByte(data, acceptaudio.ByteOffset, acceptaudio.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, acceptaudio.ByteOffset, acceptaudio.BitOffset, acceptaudio.NumBits); }
        }

        public string Name
        {
            get { return Encoding.ASCII.GetString(data, name.ByteOffset, name.NumBits / 8); }
            set
            {
                int charCount = name.NumBits / 8;
                if (value.Length < charCount)
                    charCount = value.Length;

                Encoding.ASCII.GetBytes(value, 0, charCount, data, name.ByteOffset);
            }
        }

        public string Squad
        {
            get { return Encoding.ASCII.GetString(data, squad.ByteOffset, squad.NumBits / 8); }
            set
            {
                if (value == null)
                {
                    int numBytes = squad.NumBits / 8;
                    for (int x = 0; x < numBytes; x++)
                    {
                        data[squad.ByteOffset + x] = 0;
                    }
                }
                else
                {
                    int charCount = squad.NumBits / 8;
                    if (value.Length < charCount)
                        charCount = value.Length;

                    Encoding.ASCII.GetBytes(value, 0, charCount, data, squad.ByteOffset);
                }
            }
        }

        public int KillPoints
        {
            get { return ExtendedBitConverter.ToInt32(data, killpoints.ByteOffset, killpoints.BitOffset); }
            set { ExtendedBitConverter.WriteInt32Bits(value, data, killpoints.ByteOffset, killpoints.BitOffset, killpoints.NumBits); }
        }

        public int FlagPoints
        {
            get { return ExtendedBitConverter.ToInt32(data, flagpoints.ByteOffset, flagpoints.BitOffset); }
            set { ExtendedBitConverter.WriteInt32Bits(value, data, flagpoints.ByteOffset, flagpoints.BitOffset, flagpoints.NumBits); }
        }

        public short Pid
        {
            get { return ExtendedBitConverter.ToInt16(data, pid.ByteOffset, pid.BitOffset); }
            set { ExtendedBitConverter.WriteInt16Bits(value, data, pid.ByteOffset, pid.BitOffset, pid.NumBits); }
        }

        public short Freq
        {
            get { return ExtendedBitConverter.ToInt16(data, freq.ByteOffset, freq.BitOffset); }
            set { ExtendedBitConverter.WriteInt16Bits(value, data, freq.ByteOffset, freq.BitOffset, freq.NumBits); }
        }

        public short Wins
        {
            get { return ExtendedBitConverter.ToInt16(data, wins.ByteOffset, wins.BitOffset); }
            set { ExtendedBitConverter.WriteInt16Bits(value, data, wins.ByteOffset, wins.BitOffset, wins.NumBits); }
        }

        public short Losses
        {
            get { return ExtendedBitConverter.ToInt16(data, losses.ByteOffset, losses.BitOffset); }
            set { ExtendedBitConverter.WriteInt16Bits(value, data, losses.ByteOffset, losses.BitOffset, losses.NumBits); }
        }

        public short AttachedTo
        {
            get { return ExtendedBitConverter.ToInt16(data, attachedto.ByteOffset, attachedto.BitOffset); }
            set { ExtendedBitConverter.WriteInt16Bits(value, data, attachedto.ByteOffset, attachedto.BitOffset, attachedto.NumBits); }
        }

        public short FlagsCarried
        {
            get { return ExtendedBitConverter.ToInt16(data, flagscarried.ByteOffset, flagscarried.BitOffset); }
            set { ExtendedBitConverter.WriteInt16Bits(value, data, flagscarried.ByteOffset, flagscarried.BitOffset, flagscarried.NumBits); }
        }

        public byte MiscBits
        {
            get { return ExtendedBitConverter.ToByte(data, miscbits.ByteOffset, miscbits.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, miscbits.ByteOffset, miscbits.BitOffset, miscbits.NumBits); }
        }
    }
}
