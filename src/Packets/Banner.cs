using SS.Utilities;
using System;
using System.Runtime.InteropServices;

namespace SS.Core.Packets
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct Banner
    {
        private const int DataLength = 96;
        private fixed byte dataBytes[DataLength];
        public Span<byte> Data => MemoryMarshal.CreateSpan(ref dataBytes[0], DataLength);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2CBanner
    {
        public static readonly int Length;

        static S2CBanner()
        {
            Length = Marshal.SizeOf<S2CBanner>();
        }

        public readonly byte Type;

        private readonly short playerId;
        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
        }

        public readonly Banner Banner;

        public S2CBanner(short playerId, in Banner banner)
        {
            Type = (byte)S2CPacketType.Banner;
            this.playerId = LittleEndianConverter.Convert(playerId);
            Banner = banner; // copy
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2SBanner
    {
        public static readonly int Length;

        static C2SBanner()
        {
            Length = Marshal.SizeOf<C2SBanner>();
        }

        public readonly byte Type;
        public readonly Banner Banner;

        public C2SBanner(in Banner banner)
        {
            Type = (byte)C2SPacketType.Banner;
            Banner = banner; // copy
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2CBannerToggle
    {
        public static readonly int Length;

        static S2CBannerToggle()
        {
            Length = Marshal.SizeOf<S2CBannerToggle>();
        }

        public readonly byte Type;
        public readonly byte Toggle;

        public S2CBannerToggle(byte toggle)
        {
            Type = (byte)S2CPacketType.BannerToggle;
            Toggle = toggle;
        }
    }
}
