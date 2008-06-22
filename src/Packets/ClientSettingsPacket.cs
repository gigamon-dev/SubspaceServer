using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    // note to self, luckily each part of the packet is padded to byte boundary.  otherwise i wouldn't be able to use ArraySegment<byte>
    public struct ClientSettingsPacket
    {
        static ClientSettingsPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateDataLocation(8);
            bitset = locationBuilder.CreateDataLocation(8 * 3);

            ships = new DataLocation[8];
            for (int x = 0; x < ships.Length; x++)
            {
                ships[x] = locationBuilder.CreateDataLocation(8 * 144);
            }
            
            longSet = new DataLocation[20];
            for (int x = 0; x < longSet.Length; x++)
            {
                longSet[x] = locationBuilder.CreateDataLocation(32);
            }

            spawnPos = new DataLocation[4];
            for (int x = 0; x < spawnPos.Length; x++)
            {
                spawnPos[x] = locationBuilder.CreateDataLocation(32);
            }

            shortSet = new DataLocation[58];
            for (int x = 0; x < shortSet.Length; x++)
            {
                shortSet[x] = locationBuilder.CreateDataLocation(16);
            }

            byteSet = new DataLocation[32];
            for (int x = 0; x < byteSet.Length; x++)
            {
                byteSet[x] = locationBuilder.CreateDataLocation(8);
            }

            prizeWeightSet = new DataLocation[28];
            for (int x = 0; x < prizeWeightSet.Length; x++)
            {
                prizeWeightSet[x] = locationBuilder.CreateDataLocation(8);
            }

            Length = locationBuilder.NumBytes;
        }

        private static readonly DataLocation type;
        private static DataLocation bitset;
        private static DataLocation[] ships;
        private static DataLocation[] longSet;
        private static DataLocation[] spawnPos;
        private static DataLocation[] shortSet;
        private static DataLocation[] byteSet;
        private static DataLocation[] prizeWeightSet;
        public static int Length;

        private readonly byte[] data;

        public ClientSettingsPacket(byte[] data)
        {
            this.data = data;
        }

        public byte[] Bytes
        {
            get { return data; }
        }

        public byte Type
        {
            get { return ExtendedBitConverter.ToByte(data, type.ByteOffset, type.BitOffset); }
            set { ExtendedBitConverter.WriteByteBits(value, data, type.ByteOffset, type.BitOffset, type.NumBits); }
        }

        public struct ClientBitSet
        {
            static ClientBitSet()
            {
                DataLocationBuilder locationBuilder = new DataLocationBuilder();
                exactDamage = locationBuilder.CreateDataLocation(1);
                hideFlags = locationBuilder.CreateDataLocation(1);
                noXRadar = locationBuilder.CreateDataLocation(1);
                slowFramerate = locationBuilder.CreateDataLocation(3);
                disableScreenshot = locationBuilder.CreateDataLocation(1);
                reserved = locationBuilder.CreateDataLocation(1);
                maxTimerDrift = locationBuilder.CreateDataLocation(3);
                disableBallThroughWalls = locationBuilder.CreateDataLocation(1);
                disableBallKilling = locationBuilder.CreateDataLocation(1);
            }

            private static readonly DataLocation exactDamage;
            private static readonly DataLocation hideFlags;
            private static readonly DataLocation noXRadar;
            private static readonly DataLocation slowFramerate;
            private static readonly DataLocation disableScreenshot;
            private static readonly DataLocation reserved;
            private static readonly DataLocation maxTimerDrift;
            private static readonly DataLocation disableBallThroughWalls;
            private static readonly DataLocation disableBallKilling;

            private ArraySegment<byte> segment;

            public ClientBitSet(ArraySegment<byte> segment)
            {
                this.segment = segment;
            }

            public byte ExactDamage
            {
                set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + exactDamage.ByteOffset, exactDamage.BitOffset, exactDamage.NumBits); }
            }

            public byte HideFlags
            {
                set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + hideFlags.ByteOffset, hideFlags.BitOffset, hideFlags.NumBits); }
            }

            public byte NoXRadar
            {
                set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + noXRadar.ByteOffset, noXRadar.BitOffset, noXRadar.NumBits); }
            }

            public byte SlowFramerate
            {
                set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + slowFramerate.ByteOffset, slowFramerate.BitOffset, slowFramerate.NumBits); }
            }

            public byte DisableScreenshot
            {
                set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + disableScreenshot.ByteOffset, disableScreenshot.BitOffset, disableScreenshot.NumBits); }
            }

            public byte MaxTimerDrift
            {
                set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + maxTimerDrift.ByteOffset, maxTimerDrift.BitOffset, maxTimerDrift.NumBits); }
            }

            public byte DisableBallThroughWalls
            {
                set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + disableBallThroughWalls.ByteOffset, disableBallThroughWalls.BitOffset, disableBallThroughWalls.NumBits); }
            }

            public byte DisableBallKilling
            {
                set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + disableBallKilling.ByteOffset, disableBallKilling.BitOffset, disableBallKilling.NumBits); }
            }
        }

        public ClientBitSet BitSet
        {
            get { return new ClientBitSet(new ArraySegment<byte>(data, bitset.ByteOffset, bitset.NumBits / 8)); }
        }

        public struct ShipSettings
        {
            static ShipSettings()
            {
                DataLocationBuilder locationBuilder = new DataLocationBuilder();
                
                longSet = new DataLocation[2];
                for (int x = 0; x < longSet.Length; x++)
                    longSet[x] = locationBuilder.CreateDataLocation(32);

                shortSet = new DataLocation[49];
                for (int x = 0; x < shortSet.Length; x++)
                    shortSet[x] = locationBuilder.CreateDataLocation(16);

                byteSet = new DataLocation[18];
                for (int x = 0; x < byteSet.Length; x++)
                    byteSet[x] = locationBuilder.CreateDataLocation(8);

                weaponBits = locationBuilder.CreateDataLocation(8 * 4);
            }

            private static readonly DataLocation[] longSet;
            private static readonly DataLocation[] shortSet;
            private static readonly DataLocation[] byteSet;
            private static readonly DataLocation weaponBits;

            private ArraySegment<byte> segment;

            public ShipSettings(ArraySegment<byte> segment)
            {
                this.segment = segment;
            }

            public void SetLongSet(int index, int value)
            {
                ExtendedBitConverter.WriteInt32Bits(value, segment.Array, segment.Offset + longSet[index].ByteOffset, longSet[index].BitOffset, longSet[index].NumBits);
            }

            public void SetShortSet(int index, short value)
            {
                ExtendedBitConverter.WriteInt16Bits(value, segment.Array, segment.Offset + shortSet[index].ByteOffset, shortSet[index].BitOffset, shortSet[index].NumBits);
            }

            public void SetByteSet(int index, byte value)
            {
                ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + byteSet[index].ByteOffset, byteSet[index].BitOffset, byteSet[index].NumBits);
            }

            public struct WeaponBits
            {
                static WeaponBits()
                {
                    DataLocationBuilder locationBuilder = new DataLocationBuilder();
                    shrapnelMax = locationBuilder.CreateDataLocation(5);
                    shrapnelRate = locationBuilder.CreateDataLocation(5);
                    cloakStatus = locationBuilder.CreateDataLocation(2);
                    stealthStatus = locationBuilder.CreateDataLocation(2);
                    xRadarStatus = locationBuilder.CreateDataLocation(2);
                    antiWarpStatus = locationBuilder.CreateDataLocation(2);
                    initialGuns = locationBuilder.CreateDataLocation(2);
                    maxGuns = locationBuilder.CreateDataLocation(2);
                    initialBombs = locationBuilder.CreateDataLocation(2);
                    maxBombs = locationBuilder.CreateDataLocation(2);
                    doubleBarrel = locationBuilder.CreateDataLocation(1);
                    empBomb = locationBuilder.CreateDataLocation(1);
                    seeMines = locationBuilder.CreateDataLocation(1);
                }

                private static readonly DataLocation shrapnelMax;
                private static readonly DataLocation shrapnelRate;
                private static readonly DataLocation cloakStatus;
                private static readonly DataLocation stealthStatus;
                private static readonly DataLocation xRadarStatus;
                private static readonly DataLocation antiWarpStatus;
                private static readonly DataLocation initialGuns;
                private static readonly DataLocation maxGuns;
                private static readonly DataLocation initialBombs;
                private static readonly DataLocation maxBombs;
                private static readonly DataLocation doubleBarrel;
                private static readonly DataLocation empBomb;
                private static readonly DataLocation seeMines;

                private ArraySegment<byte> segment;

                public WeaponBits(ArraySegment<byte> segment)
                {
                    this.segment = segment;
                }

                public byte ShrapnelMax
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + shrapnelMax.ByteOffset, shrapnelMax.BitOffset, shrapnelMax.NumBits); }
                }

                public byte ShrapnelRate
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + shrapnelRate.ByteOffset, shrapnelRate.BitOffset, shrapnelRate.NumBits); }
                }

                public byte CloakStatus
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + cloakStatus.ByteOffset, cloakStatus.BitOffset, cloakStatus.NumBits); }
                }

                public byte StealthStatus
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + stealthStatus.ByteOffset, stealthStatus.BitOffset, stealthStatus.NumBits); }
                }

                public byte XRadarStatus
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + xRadarStatus.ByteOffset, xRadarStatus.BitOffset, xRadarStatus.NumBits); }
                }

                public byte AntiWarpStatus
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + antiWarpStatus.ByteOffset, antiWarpStatus.BitOffset, antiWarpStatus.NumBits); }
                }

                public byte InitialGuns
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + initialGuns.ByteOffset, initialGuns.BitOffset, initialGuns.NumBits); }
                }

                public byte MaxGuns
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + maxGuns.ByteOffset, maxGuns.BitOffset, maxGuns.NumBits); }
                }
                public byte InitialBombs
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + initialBombs.ByteOffset, initialBombs.BitOffset, initialBombs.NumBits); }
                }

                public byte MaxBombs
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + maxBombs.ByteOffset, maxBombs.BitOffset, maxBombs.NumBits); }
                }

                public byte DoubleBarrel
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + doubleBarrel.ByteOffset, doubleBarrel.BitOffset, doubleBarrel.NumBits); }
                }

                public byte EmpBomb
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + empBomb.ByteOffset, empBomb.BitOffset, empBomb.NumBits); }
                }

                public byte SeeMines
                {
                    set { ExtendedBitConverter.WriteByteBits(value, segment.Array, segment.Offset + seeMines.ByteOffset, seeMines.BitOffset, seeMines.NumBits); }
                }
            }

            public WeaponBits Weapons
            {
                get { return new WeaponBits(new ArraySegment<byte>(segment.Array, segment.Offset + weaponBits.ByteOffset, weaponBits.NumBits/8)); }
            }
        }

        public ShipSettings GetShipSetting(int index)
        {
            return new ShipSettings(new ArraySegment<byte>(data, ships[index].ByteOffset, ships[index].NumBits / 8));
        }

        public void SetLongSet(int index, int value)
        {
            ExtendedBitConverter.WriteInt32Bits(value, data, longSet[index].ByteOffset, longSet[index].BitOffset, longSet[index].NumBits);
        }

        public struct SpawnPos
        {
            static SpawnPos()
            {
                DataLocationBuilder locationBuilder = new DataLocationBuilder();
                x = locationBuilder.CreateDataLocation(10);
                y = locationBuilder.CreateDataLocation(10);
                r = locationBuilder.CreateDataLocation(9);
            }

            private static readonly DataLocation x;
            private static readonly DataLocation y;
            private static readonly DataLocation r;

            private ArraySegment<byte> segment;

            public SpawnPos(ArraySegment<byte> segment)
            {
                this.segment = segment;
            }

            public ushort X
            {
                set { ExtendedBitConverter.WriteUInt16Bits(value, segment.Array, segment.Offset + x.ByteOffset, x.BitOffset, x.NumBits); }
            }

            public ushort Y
            {
                set { ExtendedBitConverter.WriteUInt16Bits(value, segment.Array, segment.Offset + y.ByteOffset, y.BitOffset, y.NumBits); }
            }

            public ushort R
            {
                set { ExtendedBitConverter.WriteUInt16Bits(value, segment.Array, segment.Offset + r.ByteOffset, r.BitOffset, r.NumBits); }
            }
        }

        public SpawnPos GetSpawnPos(int index)
        {
            return new SpawnPos(new ArraySegment<byte>(data, spawnPos[index].ByteOffset, spawnPos[index].NumBits));
        }

        public void SetShortSet(int index, short value)
        {
            ExtendedBitConverter.WriteInt16Bits(value, data, shortSet[index].ByteOffset, shortSet[index].BitOffset, shortSet[index].NumBits);
        }

        public void SetByteSet(int index, byte value)
        {
            ExtendedBitConverter.WriteByteBits(value, data, byteSet[index].ByteOffset, byteSet[index].BitOffset, byteSet[index].NumBits);
        }
    }
}
