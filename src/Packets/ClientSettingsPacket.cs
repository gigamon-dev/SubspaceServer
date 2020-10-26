using SS.Utilities;
using System;
using System.Buffers.Binary;

namespace SS.Core.Packets
{
    public class Int32Array
    {
        private readonly byte[] _data;
        private readonly int _byteOffset;
        private readonly int _bitOffset;
        private readonly Int32DataLocation[] _dataLocations;

        public Int32Array(byte[] data, int byteOffset, int bitOffset, Int32DataLocation[] dataLocations)
        {
            if (dataLocations == null)
                throw new ArgumentNullException(nameof(dataLocations));

            _data = data ?? throw new ArgumentNullException(nameof(data));
            _byteOffset = byteOffset;
            _bitOffset = bitOffset;

            _dataLocations = new Int32DataLocation[dataLocations.Length];
            for (int x = 0; x < dataLocations.Length; x++)
                _dataLocations[x] = dataLocations[x];
        }

        public Int32Array(byte[] data, Int32DataLocation[] dataLocations)
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
        private readonly byte[] _data;
        private readonly int _byteOffset;
        private readonly int _bitOffset;
        private readonly Int16DataLocation[] _dataLocations;

        public Int16Array(byte[] data, int byteOffset, int bitOffset, Int16DataLocation[] dataLocations)
        {
            if (dataLocations == null)
                throw new ArgumentNullException(nameof(dataLocations));

            _data = data ?? throw new ArgumentNullException(nameof(data));
            _byteOffset = byteOffset;
            _bitOffset = bitOffset;

            _dataLocations = new Int16DataLocation[dataLocations.Length];
            for (int x = 0; x < dataLocations.Length; x++)
                _dataLocations[x] = dataLocations[x];
        }

        public Int16Array(byte[] data, Int16DataLocation[] dataLocations)
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
        private readonly byte[] _data;
        private readonly int _byteOffset;
        private readonly int _bitOffset;
        private readonly ByteDataLocation[] _dataLocations;

        public ByteArray(byte[] data, int byteOffset, int bitOffset, ByteDataLocation[] dataLocations)
        {
            if (dataLocations == null)
                throw new ArgumentNullException(nameof(dataLocations));

            _data = data ?? throw new ArgumentNullException(nameof(data));
            _byteOffset = byteOffset;
            _bitOffset = bitOffset;

            _dataLocations = new ByteDataLocation[dataLocations.Length];
            for (int x = 0; x < dataLocations.Length; x++)
                _dataLocations[x] = dataLocations[x];
        }

        public ByteArray(byte[] data, ByteDataLocation[] dataLocations)
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

    public class ClientSettingsPacket
    {
        static ClientSettingsPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateByteDataLocation();
            bitset = locationBuilder.CreateDataLocation(3);

            ships = new DataLocation[8];
            for (int x = 0; x < ships.Length; x++)
            {
                ships[x] = locationBuilder.CreateDataLocation(144);
            }
            
            longSet = new Int32DataLocation[20];
            for (int x = 0; x < longSet.Length; x++)
            {
                longSet[x] = locationBuilder.CreateInt32DataLocation();
            }

            spawnPos = new DataLocation[4];
            for (int x = 0; x < spawnPos.Length; x++)
            {
                spawnPos[x] = locationBuilder.CreateDataLocation(4);
            }

            shortSet = new Int16DataLocation[58];
            for (int x = 0; x < shortSet.Length; x++)
            {
                shortSet[x] = locationBuilder.CreateInt16DataLocation();
            }

            byteSet = new ByteDataLocation[32];
            for (int x = 0; x < byteSet.Length; x++)
            {
                byteSet[x] = locationBuilder.CreateByteDataLocation();
            }

            prizeWeightSet = new ByteDataLocation[28];
            for (int x = 0; x < prizeWeightSet.Length; x++)
            {
                prizeWeightSet[x] = locationBuilder.CreateByteDataLocation();
            }

            Length = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static readonly DataLocation bitset;
        private static readonly DataLocation[] ships;
        private static readonly Int32DataLocation[] longSet;
        private static readonly DataLocation[] spawnPos;
        private static readonly Int16DataLocation[] shortSet;
        private static readonly ByteDataLocation[] byteSet;
        private static readonly ByteDataLocation[] prizeWeightSet;
        public static readonly int Length;

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
                throw new ArgumentNullException(nameof(data));

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
                bitField1 = locationBuilder.CreateByteDataLocation();
                bitField2 = locationBuilder.CreateUInt16DataLocation();

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

            private readonly ArraySegment<byte> segment;

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
                
                longSet = new Int32DataLocation[2];
                for (int x = 0; x < longSet.Length; x++)
                    longSet[x] = locationBuilder.CreateInt32DataLocation();

                shortSet = new Int16DataLocation[49];
                for (int x = 0; x < shortSet.Length; x++)
                    shortSet[x] = locationBuilder.CreateInt16DataLocation();

                byteSet = new ByteDataLocation[18];
                for (int x = 0; x < byteSet.Length; x++)
                    byteSet[x] = locationBuilder.CreateByteDataLocation();

                weaponBits = locationBuilder.CreateDataLocation(4);
            }

            private static readonly Int32DataLocation[] longSet;
            private static readonly Int16DataLocation[] shortSet;
            private static readonly ByteDataLocation[] byteSet;
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
                MiscBits = new MiscBitField(segment.Array, (UInt16DataLocation)new DataLocation(segment.Offset + shortSet[10].ByteOffset, 2));
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

                private readonly ArraySegment<byte> segment;

                public WeaponBits(ArraySegment<byte> segment)
                {
                    this.segment = segment;
                }

                private uint BitField
                {
                    get { return BinaryPrimitives.ReadUInt32LittleEndian(segment); }
                    set { BinaryPrimitives.WriteUInt32LittleEndian(segment, value); }
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

                private readonly byte[] _data;
                private readonly UInt16DataLocation _dataLocation;

                public MiscBitField(byte[] data, UInt16DataLocation dataLocation)
                {
                    _data = data ?? throw new ArgumentException("data");
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
                bitField = locationBuilder.CreateUInt32DataLocation();

                BitFieldBuilder builder = new BitFieldBuilder(32);
                x = builder.CreateBitFieldLocation(10);
                y = builder.CreateBitFieldLocation(10);
                r = builder.CreateBitFieldLocation(9);
            }

            private static readonly UInt32DataLocation bitField;
            private static readonly BitFieldLocation x;
            private static readonly BitFieldLocation y;
            private static readonly BitFieldLocation r;

            private readonly ArraySegment<byte> segment;

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
                set { BitField = BitFieldConverter.SetUInt16(value, BitField, x.LowestOrderBit, x.NumBits); }
            }

            public ushort Y
            {
                set { BitField = BitFieldConverter.SetUInt16(value, BitField, y.LowestOrderBit, y.NumBits); }
            }

            public ushort R
            {
                set { BitField = BitFieldConverter.SetUInt16(value, BitField, r.LowestOrderBit, r.NumBits); }
            }
        }
    }
}
