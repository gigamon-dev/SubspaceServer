using SS.Core;
using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct Enter
    {
        #region Static members

        public static readonly int Length;

        static Enter()
        {
            Length = Marshal.SizeOf(typeof(Enter));
        }

        #endregion

        public EventHeader Header;
        private short playerId;
        private fixed byte nameBytes[nameBytesLength];
        private fixed byte squadBytes[squadBytesLength];
        private short ship;
        private short freq;

        public Enter(ServerTick ticks, short playerId, ReadOnlySpan<char> name, ReadOnlySpan<char> squad, ShipType ship, short freq)
        {
            Header = new EventHeader(ticks, EventType.Enter);
            this.playerId = LittleEndianConverter.Convert(playerId);
            this.ship = LittleEndianConverter.Convert((short)ship);
            this.freq = LittleEndianConverter.Convert(freq);
            SetName(name);
            SetSquad(squad);
        }

        #region Helper properties

        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        private const int nameBytesLength = 24;
        public Span<byte> NameBytes => MemoryMarshal.CreateSpan(ref nameBytes[0], nameBytesLength);

        public void SetName(ReadOnlySpan<char> value)
        {
            StringUtils.WriteNullPaddedString(NameBytes, value, true);
        }

        private const int squadBytesLength = 24;
        public Span<byte> SquadBytes => MemoryMarshal.CreateSpan(ref squadBytes[0], squadBytesLength);

        public void SetSquad(ReadOnlySpan<char> value)
        {
            StringUtils.WriteNullPaddedString(SquadBytes, value, true);
        }

        public ShipType Ship
        {
            get => (ShipType)LittleEndianConverter.Convert(ship);
            set => ship = LittleEndianConverter.Convert((short)value);
        }

        public short Freq
        {
            get => LittleEndianConverter.Convert(freq);
            set => freq = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
