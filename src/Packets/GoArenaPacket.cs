using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public struct GoArenaPacket
    {
        static GoArenaPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateDataLocation(8);
            shipType = locationBuilder.CreateDataLocation(8);
            obscenityFilter = locationBuilder.CreateDataLocation(8);
            wavMsg = locationBuilder.CreateDataLocation(8);
            xRes = locationBuilder.CreateDataLocation(16);
            yRes = locationBuilder.CreateDataLocation(16);
            arenaType = locationBuilder.CreateDataLocation(16);
            arenaName = locationBuilder.CreateDataLocation(8 * 16);
            LengthVIE = locationBuilder.NumBytes;
            optionalGraphics = locationBuilder.CreateDataLocation(8);
            LengthContinuum = locationBuilder.NumBytes;
        }

        private static readonly DataLocation type;
        private static readonly DataLocation shipType;
        private static readonly DataLocation obscenityFilter;
        private static readonly DataLocation wavMsg;
        private static readonly DataLocation xRes;
        private static readonly DataLocation yRes;
        private static readonly DataLocation arenaType;
        private static readonly DataLocation arenaName;
        private static readonly DataLocation optionalGraphics; // cont
        public static readonly int LengthVIE;
        public static readonly int LengthContinuum;

        private readonly byte[] data;

        public GoArenaPacket(byte[] data)
        {
            this.data = data;
        }

        public byte Type
        {
            get { return ExtendedBitConverter.ToByte(data, type.ByteOffset, type.BitOffset); }
            //set { ExtendedBitConverter.WriteByteBits(value, data, type.ByteOffset, type.BitOffset, type.NumBits); }
        }

        public byte ShipType
        {
            get { return ExtendedBitConverter.ToByte(data, shipType.ByteOffset, shipType.BitOffset); }
            //set { ExtendedBitConverter.WriteByteBits(value, data, shipType.ByteOffset, shipType.BitOffset, shipType.NumBits); }
        }

        public sbyte ObscenityFilter
        {
            get { return ExtendedBitConverter.ToSByte(data, obscenityFilter.ByteOffset, obscenityFilter.BitOffset); }
            //set { ExtendedBitConverter.WriteSByteBits(value, data, obscenityFilter.ByteOffset, obscenityFilter.BitOffset, obscenityFilter.NumBits); }
        }

        public sbyte WavMsg
        {
            get { return ExtendedBitConverter.ToSByte(data, wavMsg.ByteOffset, wavMsg.BitOffset); }
            //set { ExtendedBitConverter.WriteSByteBits(value, data, wavMsg.ByteOffset, wavMsg.BitOffset, wavMsg.NumBits); }
        }

        public short XRes
        {
            get { return ExtendedBitConverter.ToInt16(data, xRes.ByteOffset, xRes.BitOffset); }
            //set { ExtendedBitConverter.WriteInt16Bits(value, data, xRes.ByteOffset, xRes.BitOffset, xRes.NumBits); }
        }

        public short YRes
        {
            get { return ExtendedBitConverter.ToInt16(data, yRes.ByteOffset, yRes.BitOffset); }
            //set { ExtendedBitConverter.WriteInt16Bits(value, data, yRes.ByteOffset, yRes.BitOffset, yRes.NumBits); }
        }

        public short ArenaType
        {
            get { return ExtendedBitConverter.ToInt16(data, arenaType.ByteOffset, arenaType.BitOffset); }
            //set { ExtendedBitConverter.WriteInt16Bits(value, data, arenaType.ByteOffset, arenaType.BitOffset, arenaType.NumBits); }
        }

        public string ArenaName
        {
            get
            {
                string str = Encoding.ASCII.GetString(data, arenaName.ByteOffset, arenaName.NumBits / 8);
                int index = str.IndexOf('\0');
                if (index != -1)
                {
                    return str.Substring(0, index);
                }
                return str;
            }
        }

        public byte OptionalGraphics
        {
            get { return ExtendedBitConverter.ToByte(data, optionalGraphics.ByteOffset, optionalGraphics.BitOffset); }
        }
    }
}
