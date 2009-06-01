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
            pktype = locationBuilder.CreateByteDataLocation();
            type = locationBuilder.CreateByteDataLocation();
            sound = locationBuilder.CreateByteDataLocation();
            pid = locationBuilder.CreateInt16DataLocation();
            HeaderLength = locationBuilder.NumBytes;
            text = locationBuilder.CreateDataLocation(1);
        }

        private static readonly ByteDataLocation pktype;
        private static readonly ByteDataLocation type;
        private static readonly ByteDataLocation sound;
        private static readonly Int16DataLocation pid;
        public static readonly int HeaderLength;
        private static readonly DataLocation text; // NumBytes is variable for this location, do not use

        // used for finding the end of the text
        private static Predicate<byte> _nullCharPredicate =
            delegate(byte b)
            {
                return b == 0;
            };

        private readonly byte[] data;

        public ChatPacket(byte[] data)
        {
            this.data = data;
        }

        public byte PkType
        {
            get { return pktype.GetValue(data); }
            set { pktype.SetValue(data, value); }
        }

        public byte Type
        {
            get { return type.GetValue(data); }
            set { type.SetValue(data, value); }
        }

        public byte Sound
        {
            get { return sound.GetValue(data); }
            set { sound.SetValue(data, value); }
        }

        public short Pid
        {
            get { return pid.GetValue(data); }
            set { pid.SetValue(data, value); }
        }

        /// <summary>
        /// To get the text stored in the packet.
        /// </summary>
        /// <param name="maxBufferBytes">Maximum # of bytes the ENTIRE buffer consists of.  This is used to help determine the end of the text.</param>
        /// <returns></returns>
        public string GetText(int maxBufferBytes)
        {
            if (maxBufferBytes > data.Length)
                maxBufferBytes = data.Length; // can't be > than the buffer itself

            int textByteCount = maxBufferBytes - text.ByteOffset;
            if (textByteCount <= 0)
                return string.Empty;

            int stopIndex = Array.FindIndex<byte>(data, text.ByteOffset, textByteCount, _nullCharPredicate);

            if (stopIndex == -1)
                return Encoding.ASCII.GetString(data, text.ByteOffset, textByteCount);
            else
                return Encoding.ASCII.GetString(data, text.ByteOffset, stopIndex - text.ByteOffset);
        }

        /// <summary>
        /// To set the text field of the chat packet.
        /// </summary>
        /// <param name="value">text to store</param>
        /// <returns>the # of bytes used by the text (includes the ending '\0' character)</returns>
        public int SetText(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            int maxTextLength = (data.Length - text.ByteOffset) - 1; // -1 for a '\0' (required by continuum)
            int numTextBytes;
            if (value.Length > maxTextLength)
                numTextBytes = Encoding.ASCII.GetBytes(value, 0, maxTextLength, data, text.ByteOffset);
            else
                numTextBytes = Encoding.ASCII.GetBytes(value, 0, value.Length, data, text.ByteOffset);

            data[text.ByteOffset + numTextBytes] = 0; // ending '\0'

            return numTextBytes + 1;
        }

        /// <summary>
        /// To make sure that the bytes in the text do not contain any control characters.
        /// Any control character found will be changed to an underscore '_'.
        /// </summary>
        /// <param name="maxBufferBytes">Maximum # of bytes the ENTIRE buffer consists of.  This is used to help determine the end of the text.</param>
        public void RemoveControlCharactersFromText(int maxBufferBytes)
        {
            if (maxBufferBytes > data.Length)
                maxBufferBytes = data.Length; // can't be > than the buffer itself

            int textByteCount = maxBufferBytes - text.ByteOffset;
            if (textByteCount <= 0)
                return;

            for (int x = text.ByteOffset; x < maxBufferBytes; x++)
            {
                byte b = data[x];
                if(b == 0)
                    return; // null char (end of string)

                if (char.IsControl((char)b))
                    data[x] = (byte)('_');
            }
        }
    }
}

