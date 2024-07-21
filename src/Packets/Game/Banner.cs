using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_Banner(short playerId, ref readonly Banner banner)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2C_Banner>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.Banner;
        private readonly short playerId = LittleEndianConverter.Convert(playerId);
        public readonly Banner Banner = banner;

        #region Helper Properties

        public short PlayerId => LittleEndianConverter.Convert(playerId);

        #endregion
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct C2S_Banner(ref readonly Banner banner)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<C2S_Banner>();

        #endregion

        public readonly byte Type = (byte)C2SPacketType.Banner;
        public readonly Banner Banner = banner;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct S2C_BannerToggle(byte toggle)
    {
        #region Static Members

        public static readonly int Length = Marshal.SizeOf<S2C_BannerToggle>();

        #endregion

        public readonly byte Type = (byte)S2CPacketType.BannerToggle;
        public readonly byte Toggle = toggle;
    }
}
