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
    public struct PeerPacketHeader
    {
        #region Static members

        public static readonly int Length;

        static PeerPacketHeader()
        {
            Length = Marshal.SizeOf<PeerPacketHeader>();
        }

        #endregion

        public byte T1;
        public byte T2;
        private uint _password;
        public byte T3; // 0xFF
        private byte _type; // peer packet type
        public uint _timestamp;

        public PeerPacketHeader(uint password, PeerPacketType type, uint timestamp)
        {
            T1 = 0x00;
            T2 = 0x01;
            _password = LittleEndianConverter.Convert(password);
            T3 = 0xFF;
            _type = (byte)type;
            _timestamp = LittleEndianConverter.Convert(timestamp);
        }

        #region Helper properties

        public uint Password
        {
            get => LittleEndianConverter.Convert(_password);
            set => _password = LittleEndianConverter.Convert(value);
        }

        public PeerPacketType Type
        {
            get => (PeerPacketType)_type;
            set => _type = (byte)value;
        }

        public uint Timestamp
        {
            get => LittleEndianConverter.Convert(_timestamp);
            set => _timestamp = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
