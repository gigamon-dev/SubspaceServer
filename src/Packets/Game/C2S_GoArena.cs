using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct C2S_GoArena
    {
        #region Static members

        public static readonly int LengthVIE;
        public static readonly int LengthContinuum;

        static C2S_GoArena()
        {
            LengthContinuum = Marshal.SizeOf<C2S_GoArena>();
            LengthVIE = LengthContinuum - 1;
        }

        #endregion

        public readonly byte Type;
        public readonly byte ShipType;
        public readonly sbyte WavMsg;
        public readonly sbyte ObscenityFilter;
        private readonly short xRes;
        private readonly short yRes;
        private readonly short arenaType;
        private fixed byte arenaNameBytes[ArenaNameBytesLength];
        public readonly byte OptionalGraphics; // continuum

        public C2S_GoArena(byte shipType, sbyte obscenityFilter, sbyte wavMsg, short xRes, short yRes, short arenaType, string arenaName, byte optionalGraphics) : this()
        {
            Type = (byte)C2SPacketType.GotoArena;
            ShipType = shipType;
            ObscenityFilter = obscenityFilter;
            WavMsg = wavMsg;
            this.xRes = LittleEndianConverter.Convert(xRes);
            this.yRes = LittleEndianConverter.Convert(yRes);
            this.arenaType = LittleEndianConverter.Convert(arenaType);
            SetArenaName(arenaName);
            OptionalGraphics = optionalGraphics;
        }

        #region Helpers

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

        private const int ArenaNameBytesLength = 16;
        public Span<byte> ArenaNameBytes => MemoryMarshal.CreateSpan(ref arenaNameBytes[0], ArenaNameBytesLength);

        public void SetArenaName(ReadOnlySpan<char> value)
        {
            ArenaNameBytes.WriteNullPaddedString(value.TruncateForEncodedByteLimit(ArenaNameBytesLength - 1));
        }

        #endregion
    }
}
