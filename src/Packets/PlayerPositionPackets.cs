using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SS.Utilities;

namespace SS.Core.Packets
{
    [FlagsAttribute]
    public enum PlayerPositionStatus : byte
    {
        /// <summary>
        /// whether stealth is on
        /// </summary>
        Stealth = 1,

        /// <summary>
        /// whether cloak is on
        /// </summary>
        Cloak = 2,

        /// <summary>
        /// whether xradar is on
        /// </summary>
        XRadar = 4,

        /// <summary>
        /// whether antiwarp is on
        /// </summary>
        Antiwarp = 8,

        /// <summary>
        /// whether to display the flashing image for a few frames
        /// </summary>
        Flash = 16,

        /// <summary>
        /// whether the player is in a safezone
        /// </summary>
        Safezone = 32,

        /// <summary>
        /// whether the player is a ufo
        /// </summary>
        Ufo = 64
    }

    public enum WeaponCodes : byte
    {
        Null = 0, 
        Bullet = 1, 
        BounceBullet = 2, 
        Bomb = 3, 
        ProxBomb = 4, 
        Repel = 5, 
        Decoy = 6, 
        Burst = 7, 
        Thor = 8, 

        /// <summary>
        /// used in watch damage packet only
        /// </summary>
        Wormhole = 0
    }

    public struct Weapons
    {
        static Weapons()
        {
            // TODO: maybe split this into 2 separate bitfields, might be slightly faster
            BitFieldBuilder builder = new BitFieldBuilder(16);
            type = (ByteBitFieldLocation)builder.CreateBitFieldLocation(5);
            level = (ByteBitFieldLocation)builder.CreateBitFieldLocation(2);
            shrapBouncing = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
            shrapLevel = (ByteBitFieldLocation)builder.CreateBitFieldLocation(2);
            shrap = (ByteBitFieldLocation)builder.CreateBitFieldLocation(5);
            alternate = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);

            Length = 2;   
        }

        private static readonly ByteBitFieldLocation type;
        private static readonly ByteBitFieldLocation level;
        private static readonly BoolBitFieldLocation shrapBouncing;
        private static readonly ByteBitFieldLocation shrapLevel;
        private static readonly ByteBitFieldLocation shrap;
        private static readonly BoolBitFieldLocation alternate;
        public static readonly int Length;

        private byte[] data;
        private int byteOffset;

        public Weapons(byte[] data, int byteOffset)
        {
            this.data = data;
            this.byteOffset = byteOffset;
        }

        private ushort BitField
        {
            get { return LittleEndianBitConverter.ToUInt16(data, byteOffset); }
            set { LittleEndianBitConverter.WriteUInt16Bits(value, data, byteOffset); }
        }

        public WeaponCodes Type
        {
            get { return (WeaponCodes)type.GetValue(BitField); }
            set { BitField = type.SetValue((byte)value, BitField); }
        }

        public byte Level
        {
            get { return level.GetValue(BitField); }
            set { BitField = level.SetValue(value, BitField); }
        }

        public bool ShrapBouncing
        {
            get { return shrapBouncing.GetValue(BitField); }
            set { BitField = shrapBouncing.SetValue(value, BitField); }
        }

        public byte ShrapLevel
        {
            get { return shrapLevel.GetValue(BitField); }
            set { BitField = shrapLevel.SetValue(value, BitField); }
        }

        public byte Shrap
        {
            get { return shrap.GetValue(BitField); }
            set { BitField = shrap.SetValue(value, BitField); }
        }

        public bool Alternate
        {
            get { return alternate.GetValue(BitField); }
            set { BitField = alternate.SetValue(value, BitField); }
        }

        public void CopyTo(Weapons dest)
        {
            Array.Copy(data, byteOffset, dest.data, dest.byteOffset, Length);
        }
    }

    public struct ExtraPositionData
    {
        static ExtraPositionData()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            energy = locationBuilder.CreateDataLocation(2);
            s2cPing = locationBuilder.CreateDataLocation(2);
            timer = locationBuilder.CreateDataLocation(2);
            bitField = locationBuilder.CreateDataLocation(4);
            Length = locationBuilder.NumBytes;

            BitFieldBuilder builder = new BitFieldBuilder(32);
            shields = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
            super = (BoolBitFieldLocation)builder.CreateBitFieldLocation(1);
            bursts = (ByteBitFieldLocation)builder.CreateBitFieldLocation(4);
            repels = (ByteBitFieldLocation)builder.CreateBitFieldLocation(4);
            thors = (ByteBitFieldLocation)builder.CreateBitFieldLocation(4);
            bricks = (ByteBitFieldLocation)builder.CreateBitFieldLocation(4);
            decoys = (ByteBitFieldLocation)builder.CreateBitFieldLocation(4);
            rockets = (ByteBitFieldLocation)builder.CreateBitFieldLocation(4);
            portals = (ByteBitFieldLocation)builder.CreateBitFieldLocation(4);
        }

        private static readonly UInt16DataLocation energy;
        private static readonly UInt16DataLocation s2cPing;
        private static readonly UInt16DataLocation timer;

        private static readonly UInt32DataLocation bitField;
        private static readonly BoolBitFieldLocation shields;
        private static readonly BoolBitFieldLocation super;
        private static readonly ByteBitFieldLocation bursts;
        private static readonly ByteBitFieldLocation repels;
        private static readonly ByteBitFieldLocation thors;
        private static readonly ByteBitFieldLocation bricks;
        private static readonly ByteBitFieldLocation decoys;
        private static readonly ByteBitFieldLocation rockets;
        private static readonly ByteBitFieldLocation portals;
        public static readonly int Length;

        private byte[] data;
        private readonly int byteOffset;

        public ExtraPositionData(byte[] data, int byteOffset)
        {
            this.data = data;
            this.byteOffset = byteOffset;
        }

        public ushort Energy
        {
            get { return energy.GetValue(data, byteOffset); }
            set { energy.SetValue(data, value, byteOffset); }
        }

        public ushort S2CPing
        {
            get { return s2cPing.GetValue(data, byteOffset); }
            set { s2cPing.SetValue(data, value, byteOffset); }
        }

        private uint BitField
        {
            get { return bitField.GetValue(data, byteOffset); }
            set { bitField.SetValue(data, value, byteOffset); }
        }

        public ushort Timer
        {
            get { return timer.GetValue(data, byteOffset); }
            set { timer.SetValue(data, value, byteOffset); }
        }

        public bool Shields
        {
            get { return shields.GetValue(BitField); }
            set { BitField = shields.SetValue(value, BitField); }
        }

        public bool Super
        {
            get { return super.GetValue(BitField); }
            set { BitField = super.SetValue(value, BitField); }
        }

        public byte Bursts
        {
            get { return bursts.GetValue(BitField); }
            set { BitField = bursts.SetValue(value, BitField); }
        }

        public byte Repels
        {
            get { return repels.GetValue(BitField); }
            set { BitField = repels.SetValue(value, BitField); }
        }

        public byte Thors
        {
            get { return thors.GetValue(BitField); }
            set { BitField = thors.SetValue(value, BitField); }
        }

        public byte Bricks
        {
            get { return bricks.GetValue(BitField); }
            set { BitField = bricks.SetValue(value, BitField); }
        }

        public byte Decoys
        {
            get { return decoys.GetValue(BitField); }
            set { BitField = decoys.SetValue(value, BitField); }
        }

        public byte Rockets
        {
            get { return rockets.GetValue(BitField); }
            set { BitField = rockets.SetValue(value, BitField); }
        }

        public byte Portals
        {
            get { return portals.GetValue(BitField); }
            set { BitField = portals.SetValue(value, BitField); }
        }

        public void CopyTo(ExtraPositionData dest)
        {
            Array.Copy(data, byteOffset, dest.data, dest.byteOffset, Length);
        }
    }

    public struct S2CWeaponsPacket
    {
        static S2CWeaponsPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateDataLocation(1);
            rotation = locationBuilder.CreateDataLocation(1);
            time = locationBuilder.CreateDataLocation(2);
            x = locationBuilder.CreateDataLocation(2);
            ySpeed = locationBuilder.CreateDataLocation(2);
            playerId = locationBuilder.CreateDataLocation(2);
            xSpeed = locationBuilder.CreateDataLocation(2);
            checksum = locationBuilder.CreateDataLocation(1);
            status = locationBuilder.CreateDataLocation(1);
            c2sLatency = locationBuilder.CreateDataLocation(1);
            y = locationBuilder.CreateDataLocation(2);
            bounty = locationBuilder.CreateDataLocation(2);
            weapon = locationBuilder.CreateDataLocation(2); // 2 bytes
            Length = locationBuilder.NumBytes;
            extra = locationBuilder.CreateDataLocation(10); // 10 bytes
            LengthWithExtra = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static readonly SByteDataLocation rotation;
        private static readonly UInt16DataLocation time;
        private static readonly Int16DataLocation x;
        private static readonly Int16DataLocation ySpeed;
        private static readonly UInt16DataLocation playerId;
        private static readonly Int16DataLocation xSpeed;
        private static readonly ByteDataLocation checksum;
        private static readonly ByteDataLocation status;
        private static readonly ByteDataLocation c2sLatency;
        private static readonly Int16DataLocation y;
        private static readonly UInt16DataLocation bounty;
        private static readonly DataLocation weapon;
        private static readonly DataLocation extra;
        public static readonly int Length;
        public static readonly int LengthWithExtra;

        private byte[] data;

        public S2CWeaponsPacket(byte[] data)
        {
            this.data = data;
        }

        public byte Type
        {
            get { return type.GetValue(data); }
            set { type.SetValue(data, value); }
        }

        public sbyte Rotation
        {
            get { return rotation.GetValue(data); }
            set { rotation.SetValue(data, value); }
        }

        public ushort Time
        {
            get { return time.GetValue(data); }
            set { time.SetValue(data, value); }
        }

        public short X
        {
            get { return x.GetValue(data); }
            set { x.SetValue(data, value); }
        }

        public short YSpeed
        {
            get { return ySpeed.GetValue(data); }
            set { ySpeed.SetValue(data, value); }
        }

        public ushort PlayerId
        {
            get { return playerId.GetValue(data); }
            set { playerId.SetValue(data, value); }
        }

        public short XSpeed
        {
            get { return xSpeed.GetValue(data); }
            set { xSpeed.SetValue(data, value); }
        }

        public byte Checksum
        {
            get { return checksum.GetValue(data); }
            set { checksum.SetValue(data, value); }
        }

        public PlayerPositionStatus Status
        {
            get { return (PlayerPositionStatus)status.GetValue(data); }
            set { status.SetValue(data, (byte)value); }
        }

        public byte C2SLatency
        {
            get { return c2sLatency.GetValue(data); }
            set { c2sLatency.SetValue(data, value); }
        }

        public short Y
        {
            get { return y.GetValue(data); }
            set { y.SetValue(data, value); }
        }

        public ushort Bounty
        {
            get { return bounty.GetValue(data); }
            set { bounty.SetValue(data, value); }
        }

        public Weapons Weapon
        {
            get { return new Weapons(data, weapon.ByteOffset); }
            set { value.CopyTo(Weapon); }
        }

        public ExtraPositionData Extra
        {
            get { return new ExtraPositionData(data, extra.ByteOffset); }
            set { value.CopyTo(Extra); }
        }

        public void DoChecksum()
        {
            byte ck = 0;
            for (int i = 0; i < S2CWeaponsPacket.Length; i++)
                ck ^= data[i];
            Checksum = ck;
        }
    }

    public struct S2CPositionPacket
    {
        static S2CPositionPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateDataLocation(1);
            rotation = locationBuilder.CreateDataLocation(1);
            time = locationBuilder.CreateDataLocation(2);
            x = locationBuilder.CreateDataLocation(2);
            c2sLatency = locationBuilder.CreateDataLocation(1);
            bounty = locationBuilder.CreateDataLocation(1);
            playerId = locationBuilder.CreateDataLocation(1);
            status = locationBuilder.CreateDataLocation(1);
            ySpeed = locationBuilder.CreateDataLocation(2);
            y = locationBuilder.CreateDataLocation(2);
            xSpeed = locationBuilder.CreateDataLocation(2);
            Length = locationBuilder.NumBytes;
            extra = locationBuilder.CreateDataLocation(10); // 10 bytes
            LengthWithExtra = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static readonly SByteDataLocation rotation;
        private static readonly UInt16DataLocation time;
        private static readonly Int16DataLocation x;
        private static readonly ByteDataLocation c2sLatency;
        private static readonly ByteDataLocation bounty;
        private static readonly ByteDataLocation playerId;
        private static readonly ByteDataLocation status;
        private static readonly Int16DataLocation ySpeed;
        private static readonly Int16DataLocation y;
        private static readonly Int16DataLocation xSpeed;
        private static readonly DataLocation extra;
        public static readonly int Length;
        public static readonly int LengthWithExtra;

        private byte[] data;

        public S2CPositionPacket(byte[] data)
        {
            this.data = data;
        }

        public byte Type
        {
            get { return type.GetValue(data); }
            set { type.SetValue(data, value); }
        }

        public sbyte Rotation
        {
            get { return rotation.GetValue(data); }
            set { rotation.SetValue(data, value); }
        }

        public ushort Time
        {
            get { return time.GetValue(data); }
            set { time.SetValue(data, value); }
        }

        public short X
        {
            get { return x.GetValue(data); }
            set { x.SetValue(data, value); }
        }

        public byte C2SLatency
        {
            get { return c2sLatency.GetValue(data); }
            set { c2sLatency.SetValue(data, value); }
        }

        public byte Bounty
        {
            get { return bounty.GetValue(data); }
            set { bounty.SetValue(data, value); }
        }

        public byte PlayerId
        {
            get { return playerId.GetValue(data); }
            set { playerId.SetValue(data, value); }
        }

        public PlayerPositionStatus Status
        {
            get { return (PlayerPositionStatus)status.GetValue(data); }
            set { status.SetValue(data, (byte)value); }
        }

        public short YSpeed
        {
            get { return ySpeed.GetValue(data); }
            set { ySpeed.SetValue(data, value); }
        }

        public short Y
        {
            get { return y.GetValue(data); }
            set { y.SetValue(data, value); }
        }

        public short XSpeed
        {
            get { return xSpeed.GetValue(data); }
            set { xSpeed.SetValue(data, value); }
        }

        public ExtraPositionData Extra
        {
            get { return new ExtraPositionData(data, extra.ByteOffset); }
            set { value.CopyTo(Extra); }
        }
    }

    public struct C2SPositionPacket
    {
        static C2SPositionPacket()
        {
            DataLocationBuilder locationBuilder = new DataLocationBuilder();
            type = locationBuilder.CreateDataLocation(1);
            rotation = locationBuilder.CreateDataLocation(1);
            time = locationBuilder.CreateDataLocation(4);
            xSpeed = locationBuilder.CreateDataLocation(2);
            y = locationBuilder.CreateDataLocation(2);
            checksum = locationBuilder.CreateDataLocation(1);
            status = locationBuilder.CreateDataLocation(1);
            x = locationBuilder.CreateDataLocation(2);
            ySpeed = locationBuilder.CreateDataLocation(2);
            bounty = locationBuilder.CreateDataLocation(2);
            energy = locationBuilder.CreateDataLocation(2);
            weapon = locationBuilder.CreateDataLocation(2); // 2 bytes
            Length = locationBuilder.NumBytes;
            extra = locationBuilder.CreateDataLocation(10); // 10 bytes
            LengthWithExtra = locationBuilder.NumBytes;
        }

        private static readonly ByteDataLocation type;
        private static readonly SByteDataLocation rotation;
        private static readonly UInt32DataLocation time;
        private static readonly Int16DataLocation xSpeed;
        private static readonly Int16DataLocation y;
        private static readonly ByteDataLocation checksum;
        private static readonly ByteDataLocation status;
        private static readonly Int16DataLocation x;
        private static readonly Int16DataLocation ySpeed;
        private static readonly UInt16DataLocation bounty;
        private static readonly Int16DataLocation energy;
        private static readonly DataLocation weapon;
        private static readonly DataLocation extra; // optional
        public static readonly int Length;
        public static readonly int LengthWithExtra;

        private readonly byte[] data;

        public C2SPositionPacket(byte[] data)
        {
            this.data = data;
        }

        public byte Type
        {
            get { return type.GetValue(data); }
            set { type.SetValue(data, value); }
        }

        public sbyte Rotation
        {
            get { return rotation.GetValue(data); }
            set { rotation.SetValue(data, value); }
        }

        public ServerTick Time
        {
            get { return new ServerTick(time.GetValue(data)); }
            set { time.SetValue(data, (uint)value); }
        }

        public short XSpeed
        {
            get { return xSpeed.GetValue(data); }
            set { xSpeed.SetValue(data, value); }
        }

        public short Y
        {
            get { return y.GetValue(data); }
            set { y.SetValue(data, value); }
        }

        public byte Checksum
        {
            get { return checksum.GetValue(data); }
            set { checksum.SetValue(data, value); }
        }

        public PlayerPositionStatus Status
        {
            get { return (PlayerPositionStatus)status.GetValue(data); }
            set { status.SetValue(data, (byte)value); }
        }

        public short X
        {
            get { return x.GetValue(data); }
            set { x.SetValue(data, value); }
        }

        public short YSpeed
        {
            get { return ySpeed.GetValue(data); }
            set { ySpeed.SetValue(data, value); }
        }

        public ushort Bounty
        {
            get { return bounty.GetValue(data); }
            set { bounty.SetValue(data, value); }
        }

        public short Energy
        {
            get { return energy.GetValue(data); }
            set { energy.SetValue(data, value); }
        }

        public Weapons Weapon
        {
            get { return new Weapons(data, weapon.ByteOffset); }
            set { value.CopyTo(Weapon); }
        }

        public ExtraPositionData Extra
        {
            get { return new ExtraPositionData(data, extra.ByteOffset); }
            set { value.CopyTo(Extra); }
        }
        
        public void CopyTo(C2SPositionPacket dest)
        {
            Array.Copy(data, 0, dest.data, 0, LengthWithExtra);
        }
    }
}
