using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    public class Int32Array
    {
        private byte[] _data;
        private int _byteOffset;
        private int _bitOffset;
        private Int32DataLocation[] _dataLocations;

        public Int32Array(byte[] data, int byteOffset, int bitOffset, DataLocation[] dataLocations)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (dataLocations == null)
                throw new ArgumentNullException("dataLocations");

            _data = data;
            _byteOffset = byteOffset;
            _bitOffset = bitOffset;

            _dataLocations = new Int32DataLocation[dataLocations.Length];
            for (int x = 0; x < dataLocations.Length; x++)
                _dataLocations[x] = dataLocations[x];
        }

        public Int32Array(byte[] data, DataLocation[] dataLocations)
            : this(data, 0, 0, dataLocations)
        {
        }

        public int this[int index]
        {
            get { return _dataLocations[index].GetValue(_data, _byteOffset); }
            set { _dataLocations[index].SetValue(_data, value, _byteOffset); }
        }

        public int Length
        {
            get { return _dataLocations.Length; }
        }
    }

    public class Int16Array
    {
        private byte[] _data;
        private int _byteOffset;
        private int _bitOffset;
        private Int16DataLocation[] _dataLocations;

        public Int16Array(byte[] data, int byteOffset, int bitOffset, DataLocation[] dataLocations)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (dataLocations == null)
                throw new ArgumentNullException("dataLocations");

            _data = data;
            _byteOffset = byteOffset;
            _bitOffset = bitOffset;

            _dataLocations = new Int16DataLocation[dataLocations.Length];
            for (int x = 0; x < dataLocations.Length; x++)
                _dataLocations[x] = dataLocations[x];
        }

        public Int16Array(byte[] data, DataLocation[] dataLocations)
            : this(data, 0, 0, dataLocations)
        {
        }

        public short this[int index]
        {
            get { return _dataLocations[index].GetValue(_data, _byteOffset); }
            set { _dataLocations[index].SetValue(_data, value, _byteOffset); }
        }

        public int Length
        {
            get { return _dataLocations.Length; }
        }
    }

    public class ByteArray
    {
        private byte[] _data;
        private int _byteOffset;
        private int _bitOffset;
        private ByteDataLocation[] _dataLocations;

        public ByteArray(byte[] data, int byteOffset, int bitOffset, DataLocation[] dataLocations)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (dataLocations == null)
                throw new ArgumentNullException("dataLocations");

            _data = data;
            _byteOffset = byteOffset;
            _bitOffset = bitOffset;

            _dataLocations = new ByteDataLocation[dataLocations.Length];
            for (int x = 0; x < dataLocations.Length; x++)
                _dataLocations[x] = dataLocations[x];
        }

        public ByteArray(byte[] data, DataLocation[] dataLocations)
            : this(data, 0, 0, dataLocations)
        {
        }

        public byte this[int index]
        {
            get { return _dataLocations[index].GetValue(_data, _byteOffset); }
            set { _dataLocations[index].SetValue(_data, value, _byteOffset); }
        }

        public int Length
        {
            get { return _dataLocations.Length; }
        }
    }

    // note to self, luckily each part of the packet is padded to byte boundary.  otherwise i wouldn't be able to use ArraySegment<byte>
    public class ClientSettingsPacket
    {
        static ClientSettingsPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateDataLocation(1);
            bitset = locationBuilder.CreateDataLocation(3);

            ships = new DataLocation[8];
            for (int x = 0; x < ships.Length; x++)
            {
                ships[x] = locationBuilder.CreateDataLocation(144);
            }
            
            longSet = new DataLocation[20];
            for (int x = 0; x < longSet.Length; x++)
            {
                longSet[x] = locationBuilder.CreateDataLocation(4);
            }

            spawnPos = new DataLocation[4];
            for (int x = 0; x < spawnPos.Length; x++)
            {
                spawnPos[x] = locationBuilder.CreateDataLocation(4);
            }

            shortSet = new DataLocation[58];
            for (int x = 0; x < shortSet.Length; x++)
            {
                shortSet[x] = locationBuilder.CreateDataLocation(2);
            }

            byteSet = new DataLocation[32];
            for (int x = 0; x < byteSet.Length; x++)
            {
                byteSet[x] = locationBuilder.CreateDataLocation(1);
            }

            prizeWeightSet = new DataLocation[28];
            for (int x = 0; x < prizeWeightSet.Length; x++)
            {
                prizeWeightSet[x] = locationBuilder.CreateDataLocation(1);
            }

            Length = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static DataLocation bitset;
        private static DataLocation[] ships;
        private static DataLocation[] longSet;
        private static DataLocation[] spawnPos;
        private static DataLocation[] shortSet;
        private static DataLocation[] byteSet;
        private static DataLocation[] prizeWeightSet;
        public static int Length;

        private readonly byte[] data;

        public readonly ClientBitSet BitSet;
        public readonly ShipSettings[] Ships;
        public readonly Int32Array LongSet;
        public readonly SpawnPos[] SpawnPosition;
        public readonly Int16Array ShortSet;
        public readonly ByteArray ByteSet;
        public readonly ByteArray PrizeWeightSet;

        public ClientSettingsPacket() : this(new byte[ClientSettingsPacket.Length])
        {
        }

        public ClientSettingsPacket(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException("data");

            if (data.Length != ClientSettingsPacket.Length)
                throw new ArgumentOutOfRangeException("data", "must be of length " + ClientSettingsPacket.Length + " (was " + data.Length + ")");

            this.data = data;

            BitSet = new ClientBitSet(new ArraySegment<byte>(data, bitset.ByteOffset, bitset.NumBytes));

            Ships = new ShipSettings[ships.Length];
            for (int x = 0; x < Ships.Length; x++)
                Ships[x] = new ShipSettings(new ArraySegment<byte>(data, ships[x].ByteOffset, ships[x].NumBytes));

            LongSet = new Int32Array(data, longSet);

            SpawnPosition = new SpawnPos[spawnPos.Length];
            for (int x = 0; x < SpawnPosition.Length; x++)
                SpawnPosition[x] = new SpawnPos(new ArraySegment<byte>(data, spawnPos[x].ByteOffset, spawnPos[x].NumBytes));

            ShortSet = new Int16Array(data, shortSet);
            ByteSet = new ByteArray(data, byteSet);
            PrizeWeightSet = new ByteArray(data, prizeWeightSet);
        }

        public byte[] Bytes
        {
            get { return data; }
        }

        public byte Type
        {
            get { return type.GetValue(data); }
            set { type.SetValue(data, value); }
        }

        public class ClientBitSet
        {
            static ClientBitSet()
            {
                // note: ASSS puts Type as part of the client bit set (hacky)
                // instead i have type separate and split the bit field into 2 separate bit fields (first is a byte, second is a ushort)

                DataLocationBuilder locationBuilder = new DataLocationBuilder();
                bitField1 = locationBuilder.CreateDataLocation(1);
                bitField2 = locationBuilder.CreateDataLocation(2);

                BitFieldBuilder builder = new BitFieldBuilder((byte)(bitField1.NumBytes * 8));
                exactDamage = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
                hideFlags = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
                noXRadar = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
                slowFramerate = (ByteBitFieldLocation)builder.CreateBitFieldLocation(3);
                disableScreenshot = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
                //reserved = builder.CreateBitFieldLocation(1);

                builder = new BitFieldBuilder((byte)(bitField2.NumBytes * 8));
                maxTimerDrift = (ByteBitFieldLocation)builder.CreateBitFieldLocation(3);
                disableBallThroughWalls = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
                disableBallKilling = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
            }

            private static readonly ByteDataLocation bitField1;
            private static readonly BoolBitFieldLocation exactDamage;
            private static readonly BoolBitFieldLocation hideFlags;
            private static readonly BoolBitFieldLocation noXRadar;
            private static readonly ByteBitFieldLocation slowFramerate;
            private static readonly BoolBitFieldLocation disableScreenshot;
            //private static readonly BitFieldLocation reserved;

            private static readonly UInt16DataLocation bitField2;            
            private static readonly ByteBitFieldLocation maxTimerDrift;
            private static readonly BoolBitFieldLocation disableBallThroughWalls;
            private static readonly BoolBitFieldLocation disableBallKilling;

            private ArraySegment<byte> segment;

            public ClientBitSet(ArraySegment<byte> segment)
            {
                this.segment = segment;
            }

            private byte BitField1
            {
                get { return bitField1.GetValue(segment.Array, segment.Offset); }
                set { bitField1.SetValue(segment.Array, value, segment.Offset); }
            }

            private ushort BitField2
            {
                get { return bitField2.GetValue(segment.Array, segment.Offset); }
                set { bitField2.SetValue(segment.Array, value, segment.Offset); }
            }

            public bool ExactDamage
            {
                set { BitField1 = exactDamage.SetValue(value, BitField1); }
            }

            public bool HideFlags
            {
                set { BitField1 = hideFlags.SetValue(value, BitField1); }
            }

            public bool NoXRadar
            {
                set { BitField1 = noXRadar.SetValue(value, BitField1); }
            }

            public byte SlowFramerate
            {
                set { BitField1 = slowFramerate.SetValue(value, BitField1); }
            }

            public bool DisableScreenshot
            {
                set { BitField1 = disableScreenshot.SetValue(value, BitField1); }
            }

            public byte MaxTimerDrift
            {
                set { BitField2 = maxTimerDrift.SetValue(value, BitField2); }
            }

            public bool DisableBallThroughWalls
            {
                set { BitField2 = disableBallThroughWalls.SetValue(value, BitField2); }
            }

            public bool DisableBallKilling
            {
                set { BitField2 = disableBallKilling.SetValue(value, BitField2); }
            }
        }

        public class ShipSettings
        {
            static ShipSettings()
            {
                DataLocationBuilder locationBuilder = new DataLocationBuilder();
                
                longSet = new DataLocation[2];
                for (int x = 0; x < longSet.Length; x++)
                    longSet[x] = locationBuilder.CreateDataLocation(4);

                shortSet = new DataLocation[49];
                for (int x = 0; x < shortSet.Length; x++)
                    shortSet[x] = locationBuilder.CreateDataLocation(2);

                byteSet = new DataLocation[18];
                for (int x = 0; x < byteSet.Length; x++)
                    byteSet[x] = locationBuilder.CreateDataLocation(1);

                weaponBits = locationBuilder.CreateDataLocation(4);
            }

            private static readonly DataLocation[] longSet;
            private static readonly DataLocation[] shortSet;
            private static readonly DataLocation[] byteSet;
            private static readonly DataLocation weaponBits;

            private readonly ArraySegment<byte> segment;

            public readonly Int32Array LongSet;
            public readonly Int16Array ShortSet;
            public readonly ByteArray ByteSet;
            public readonly WeaponBits Weapons;
            public readonly MiscBitField MiscBits;

            public ShipSettings(ArraySegment<byte> segment)
            {
                this.segment = segment;
                LongSet = new Int32Array(segment.Array, segment.Offset, 0, longSet);
                ShortSet = new Int16Array(segment.Array, segment.Offset, 0, shortSet);
                ByteSet = new ByteArray(segment.Array, segment.Offset, 0, byteSet);
                Weapons = new WeaponBits(new ArraySegment<byte>(segment.Array, segment.Offset + weaponBits.ByteOffset, weaponBits.NumBytes));
                MiscBits = new MiscBitField(segment.Array, new DataLocation(segment.Offset + shortSet[10].ByteOffset, 2));
            }

            public struct WeaponBits
            {
                static WeaponBits()
                {
                    BitFieldBuilder builder = new BitFieldBuilder(32);
                    shrapnelMax = (ByteBitFieldLocation)builder.CreateBitFieldLocation(5);
                    shrapnelRate = (ByteBitFieldLocation)builder.CreateBitFieldLocation(5);
                    cloakStatus = (ByteBitFieldLocation)builder.CreateBitFieldLocation(2);
                    stealthStatus = (ByteBitFieldLocation)builder.CreateBitFieldLocation(2);
                    xRadarStatus = (ByteBitFieldLocation)builder.CreateBitFieldLocation(2);
                    antiWarpStatus = (ByteBitFieldLocation)builder.CreateBitFieldLocation(2);
                    initialGuns = (ByteBitFieldLocation)builder.CreateBitFieldLocation(2);
                    maxGuns = (ByteBitFieldLocation)builder.CreateBitFieldLocation(2);
                    initialBombs = (ByteBitFieldLocation)builder.CreateBitFieldLocation(2);
                    maxBombs = (ByteBitFieldLocation)builder.CreateBitFieldLocation(2);
                    doubleBarrel = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
                    empBomb = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
                    seeMines = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
                }

                private static readonly ByteBitFieldLocation shrapnelMax;
                private static readonly ByteBitFieldLocation shrapnelRate;
                private static readonly ByteBitFieldLocation cloakStatus;
                private static readonly ByteBitFieldLocation stealthStatus;
                private static readonly ByteBitFieldLocation xRadarStatus;
                private static readonly ByteBitFieldLocation antiWarpStatus;
                private static readonly ByteBitFieldLocation initialGuns;
                private static readonly ByteBitFieldLocation maxGuns;
                private static readonly ByteBitFieldLocation initialBombs;
                private static readonly ByteBitFieldLocation maxBombs;
                private static readonly BoolBitFieldLocation doubleBarrel;
                private static readonly BoolBitFieldLocation empBomb;
                private static readonly BoolBitFieldLocation seeMines;

                private ArraySegment<byte> segment;

                public WeaponBits(ArraySegment<byte> segment)
                {
                    this.segment = segment;
                }

                private uint BitField
                {
                    get { return LittleEndianBitConverter.ToUInt32(segment.Array, segment.Offset); }
                    set { LittleEndianBitConverter.WriteUInt32Bits(value, segment.Array, segment.Offset); }
                }

                public byte ShrapnelMax
                {
                    set { BitField = shrapnelMax.SetValue(value, BitField); }
                }

                public byte ShrapnelRate
                {
                    set { BitField = shrapnelRate.SetValue(value, BitField); }
                }

                public byte CloakStatus
                {
                    set { BitField = cloakStatus.SetValue(value, BitField); }
                }

                public byte StealthStatus
                {
                    set { BitField = stealthStatus.SetValue(value, BitField); }
                }

                public byte XRadarStatus
                {
                    set { BitField = xRadarStatus.SetValue(value, BitField); }
                }

                public byte AntiWarpStatus
                {
                    set { BitField = antiWarpStatus.SetValue(value, BitField); }
                }

                public byte InitialGuns
                {
                    set { BitField = initialGuns.SetValue(value, BitField); }
                }

                public byte MaxGuns
                {
                    set { BitField = maxGuns.SetValue(value, BitField); }
                }

                public byte InitialBombs
                {
                    set { BitField = initialBombs.SetValue(value, BitField); }
                }

                public byte MaxBombs
                {
                    set { BitField = maxBombs.SetValue(value, BitField); }
                }

                public bool DoubleBarrel
                {
                    set { BitField = doubleBarrel.SetValue(value, BitField); }
                }

                public bool EmpBomb
                {
                    set { BitField = empBomb.SetValue(value, BitField); }
                }

                public bool SeeMines
                {
                    set { BitField = seeMines.SetValue(value, BitField); }
                }
            }

            public class MiscBitField
            {
                static MiscBitField()
                {
                    BitFieldBuilder builder = new BitFieldBuilder(16);
                    seeBombLevel = (ByteBitFieldLocation)builder.CreateBitFieldLocation(2);
                    disableFastShooting = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
                    radius = (ByteBitFieldLocation)builder.CreateBitFieldLocation(8);
                    padding = (ByteBitFieldLocation)builder.CreateBitFieldLocation(5);
                }

                private static readonly ByteBitFieldLocation seeBombLevel;
                private static readonly BoolBitFieldLocation disableFastShooting;
                private static readonly ByteBitFieldLocation radius;
                private static readonly ByteBitFieldLocation padding;

                private byte[] _data;
                private UInt16DataLocation _dataLocation;

                public MiscBitField(byte[] data, DataLocation dataLocation)
                {
                    if (data == null)
                        throw new ArgumentException("data");

                    _data = data;
                    _dataLocation = dataLocation;
                }

                private ushort BitField
                {
                    get { return _dataLocation.GetValue(_data); }
                    set { _dataLocation.SetValue(_data, value); }
                }

                public byte SeeBombLevel
                {
                    set { BitField = seeBombLevel.SetValue(value, BitField); }
                }

                public bool DisableFastShooting
                {
                    set { BitField = disableFastShooting.SetValue(value, BitField); }
                }

                public byte Radius
                {
                    set { BitField = radius.SetValue(value, BitField); }
                }
            }
        }

        public class SpawnPos
        {
            static SpawnPos()
            {
                DataLocationBuilder locationBuilder = new DataLocationBuilder();
                bitField = locationBuilder.CreateDataLocation(4);

                BitFieldBuilder builder = new BitFieldBuilder(32);
                x = builder.CreateBitFieldLocation(10);
                y = builder.CreateBitFieldLocation(10);
                r = builder.CreateBitFieldLocation(9);
            }

            private static readonly UInt32DataLocation bitField;
            private static readonly BitFieldLocation x;
            private static readonly BitFieldLocation y;
            private static readonly BitFieldLocation r;

            private ArraySegment<byte> segment;

            public SpawnPos(ArraySegment<byte> segment)
            {
                this.segment = segment;
            }

            private uint BitField
            {
                get { return bitField.GetValue(segment.Array, segment.Offset); }
                set { bitField.SetValue(segment.Array, value, segment.Offset); }
            }

            public ushort X
            {
                set { BitField = LittleEndianBitConverter.SetUInt16(value, BitField, x.LowestOrderBit, x.NumBits); }
            }

            public ushort Y
            {
                set { BitField = LittleEndianBitConverter.SetUInt16(value, BitField, y.LowestOrderBit, y.NumBits); }
            }

            public ushort R
            {
                set { BitField = LittleEndianBitConverter.SetUInt16(value, BitField, r.LowestOrderBit, r.NumBits); }
            }
        }
    }
}
