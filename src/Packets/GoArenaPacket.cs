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
            type = locationBuilder.CreateByteDataLocation();
            shipType = locationBuilder.CreateByteDataLocation();
            obscenityFilter = locationBuilder.CreateSByteDataLocation();
            wavMsg = locationBuilder.CreateSByteDataLocation();
            xRes = locationBuilder.CreateInt16DataLocation();
            yRes = locationBuilder.CreateInt16DataLocation();
            arenaType = locationBuilder.CreateInt16DataLocation();
            arenaName = locationBuilder.CreateDataLocation(16);
            LengthVIE = locationBuilder.NumBytes;
            optionalGraphics = locationBuilder.CreateByteDataLocation();
            LengthContinuum = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static readonly ByteDataLocation shipType;
        private static readonly SByteDataLocation obscenityFilter;
        private static readonly SByteDataLocation wavMsg;
        private static readonly Int16DataLocation xRes;
        private static readonly Int16DataLocation yRes;
        private static readonly Int16DataLocation arenaType;
        private static readonly DataLocation arenaName;
        private static readonly ByteDataLocation optionalGraphics; // cont
        public static readonly int LengthVIE;
        public static readonly int LengthContinuum;

        private readonly byte[] data;

        public GoArenaPacket(byte[] data)
        {
            this.data = data;
        }

        public byte Type
        {
            get { return type.GetValue(data); }
        }

        public byte ShipType
        {
            get { return shipType.GetValue(data); }
        }

        public sbyte ObscenityFilter
        {
            get { return obscenityFilter.GetValue(data); }
        }

        public sbyte WavMsg
        {
            get { return wavMsg.GetValue(data); }
        }

        public short XRes
        {
            get { return xRes.GetValue(data); }
        }

        public short YRes
        {
            get { return yRes.GetValue(data); }
        }

        public short ArenaType
        {
            get { return arenaType.GetValue(data); }
        }

        public string ArenaName
        {
            get
            {
                string str = Encoding.ASCII.GetString(data, arenaName.ByteOffset, arenaName.NumBytes);
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
            get { return optionalGraphics.GetValue(data); }
        }
    }
}
