using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_Banner
    {
        public static readonly int Length;

        static S2C_Banner()
        {
            Length = Marshal.SizeOf<S2C_Banner>();
        }

        public readonly byte Type;

        private readonly short playerId;
        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
        }

        public readonly Banner Banner;

        public S2C_Banner(short playerId, in Banner banner)
        {
            Type = (byte)S2CPacketType.Banner;
            this.playerId = LittleEndianConverter.Convert(playerId);
            Banner = banner; // copy
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_Banner
    {
        public static readonly int Length;

        static C2S_Banner()
        {
            Length = Marshal.SizeOf<C2S_Banner>();
        }

        public readonly byte Type;
        public readonly Banner Banner;

        public C2S_Banner(in Banner banner)
        {
            Type = (byte)C2SPacketType.Banner;
            Banner = banner; // copy
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_BannerToggle
    {
        public static readonly int Length;

        static S2C_BannerToggle()
        {
            Length = Marshal.SizeOf<S2C_BannerToggle>();
        }

        public readonly byte Type;
        public readonly byte Toggle;

        public S2C_BannerToggle(byte toggle)
        {
            Type = (byte)S2CPacketType.BannerToggle;
            Toggle = toggle;
        }
    }
}
