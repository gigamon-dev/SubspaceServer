using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
    public enum Prize : short
    {
        Recharge = 1,
        Energy,
        Rotation,
        Stealth,
        Cloak,
        XRadar,
        Warp,
        Gun,
        Bomb,
        Bounce,
        Thrust,
        Speed,
        FullCharge,
        Shutdown,
        Multifire,
        Prox,
        Super,
        Shield,
        Shrap,
        Antiwarp,
        Repel,
        Burst,
        Decoy,
        Thor,
        Multiprize,
        Brick,
        Rocket,
        Portal
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct GreenPacket
    {
        public static readonly int S2CLength;
        public static readonly int C2SLength;

        static GreenPacket()
        {
            S2CLength = Marshal.SizeOf<GreenPacket>();
            C2SLength = S2CLength - Marshal.SizeOf<short>();
        }

        public byte Type;
        private uint time;
        private short x;
        private short y;
        private short green;
        private short playerId; // only S2C

        public uint Time
        {
            get => LittleEndianConverter.Convert(time);
            set => time = LittleEndianConverter.Convert(value);
        }

        public short X
        {
            get => LittleEndianConverter.Convert(x);
            set => x = LittleEndianConverter.Convert(value);
        }

        public short Y
        {
            get => LittleEndianConverter.Convert(y);
            set => y = LittleEndianConverter.Convert(value);
        }

        public Prize Green
        {
            get => (Prize)LittleEndianConverter.Convert(green);
            set => green = LittleEndianConverter.Convert((short)value);
        }

        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }
    }
}
