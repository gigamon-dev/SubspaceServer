using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct ChatPacket
    {
        static ChatPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            pktype = locationBuilder.CreateDataLocation(8);
            type = locationBuilder.CreateDataLocation(8);
            sound = locationBuilder.CreateDataLocation(8);
            pid = locationBuilder.CreateDataLocation(16);
            HeaderLength = locationBuilder.NumBytes;
            text = locationBuilder.CreateDataLocation(32);
        }

        private static readonly DataLocation pktype;
        private static readonly DataLocation type;
        private static readonly DataLocation sound;
        private static readonly DataLocation pid;
        public static readonly int HeaderLength;
        private static readonly DataLocation text;

        private readonly byte[] data;
        private readonly int len;

        public ChatPacket(byte[] data, int len)
        {
            this.data = data;
            this.len = len;
        }

        public byte PkType
        {
            get { return ExtendedBitConverter.ToByte(data, pktype.ByteOffset, pktype.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, pktype.ByteOffset, pktype.BitOffset, pktype.NumBits); }
        }

        public byte Type
        {
            get { return ExtendedBitConverter.ToByte(data, type.ByteOffset, type.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, type.ByteOffset, type.BitOffset, type.NumBits); }
        }

        public byte Sound
        {
            get { return ExtendedBitConverter.ToByte(data, sound.ByteOffset, sound.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, sound.ByteOffset, sound.BitOffset, sound.NumBits); }
        }

        public short Pid
        {
            get { return ExtendedBitConverter.ToInt16(data, pid.ByteOffset, pid.BitOffset); }
            set { ExtendedBitConverter.WriteInt16Bits(value, data, pid.ByteOffset, pid.BitOffset, pid.NumBits); }
        }

        public string Text
        {
            get
            {
                string str = Encoding.ASCII.GetString(data, text.ByteOffset, len - text.ByteOffset);
                int index = str.IndexOf('\0');
                if (index != -1)
                {
                    return str.Substring(0, index);
                }
                return str;
            }

            set
            {
                int charCount = len - text.ByteOffset;
                if (value.Length < charCount)
                    charCount = value.Length;

                Encoding.ASCII.GetBytes(value, 0, charCount, data, text.ByteOffset);
            }
        }

        public void RemoveControlCharactersFromText()
        {
            for (int x = 0; x < len; x++)
            {
                int characterIndex = text.ByteOffset + x;
                char c = (char)data[characterIndex];
                
                if (c == '\0')
                    break;

                if (char.IsControl(c))
                    data[characterIndex] = (byte)('_');
            }
        }
    }
}

