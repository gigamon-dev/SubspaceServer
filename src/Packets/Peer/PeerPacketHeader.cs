using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Packets.Peer
{
    public enum PeerPacketType : byte
    {
        PlayerList = 1,
        Chat = 2,
        Op = 3,
        PlayerCount = 4,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PeerPacketHeader(uint password, PeerPacketType type, uint timestamp)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<PeerPacketHeader>();

        #endregion

        public byte T1 = 0x00;
        public byte T2 = 0x01;
        private uint _password = LittleEndianConverter.Convert(password);
        public byte T3 = 0xFF;
        private byte _type = (byte)type;
        public uint _timestamp = LittleEndianConverter.Convert(timestamp);

		#region Helper properties

		public uint Password
        {
            readonly get => LittleEndianConverter.Convert(_password);
            set => _password = LittleEndianConverter.Convert(value);
        }

        public PeerPacketType Type
        {
			readonly get => (PeerPacketType)_type;
            set => _type = (byte)value;
        }

        public uint Timestamp
        {
			readonly get => LittleEndianConverter.Convert(_timestamp);
            set => _timestamp = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
