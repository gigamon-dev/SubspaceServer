using SS.Utilities;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct GoArenaPacket
    {
        public static readonly int LengthVIE;
        public static readonly int LengthContinuum;

        static GoArenaPacket()
        {
            LengthContinuum = Marshal.SizeOf<GoArenaPacket>();
            LengthVIE = LengthContinuum - 1;
        }

        public readonly byte Type;
        public readonly byte ShipType;
        public readonly sbyte ObscenityFilter;
        public readonly sbyte WavMsg;
        private readonly short xRes;
        private readonly short yRes;
        private readonly short arenaType;

        private const int ArenaNameLength = 16;
        private fixed byte arenaNameBytes[ArenaNameLength];
        public Span<byte> ArenaNameBytes => new(Unsafe.AsPointer(ref arenaNameBytes[0]), ArenaNameLength);

        public readonly byte OptionalGraphics; // continuum

        public short XRes
        {
            get => LittleEndianConverter.Convert(xRes);
            init => xRes = LittleEndianConverter.Convert(value);
        }

        public short YRes
        {
            get => LittleEndianConverter.Convert(yRes);
            init => yRes = LittleEndianConverter.Convert(value);
        }

        public short ArenaType
        {
            get => LittleEndianConverter.Convert(arenaType);
            init => arenaType = LittleEndianConverter.Convert(value);
        }

        public string ArenaName
        {
            get => ArenaNameBytes.ReadNullTerminatedString();
            init => ArenaNameBytes.WriteNullPaddedString(value);
        }

        public GoArenaPacket(byte shipType, sbyte obscenityFilter, sbyte wavMsg, short xRes, short yRes, short arenaType, string arenaName, byte optionalGraphics) : this()
        {
            Type = (byte)C2SPacketType.GotoArena;
            ShipType = shipType;
            ObscenityFilter = obscenityFilter;
            WavMsg = wavMsg;
            this.xRes = LittleEndianConverter.Convert(xRes);
            this.yRes = LittleEndianConverter.Convert(yRes);
            this.arenaType = LittleEndianConverter.Convert(arenaType);
            ArenaName = arenaName;
            OptionalGraphics = optionalGraphics;
        }
    }
}
