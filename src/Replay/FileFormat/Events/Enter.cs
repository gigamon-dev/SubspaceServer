using SS.Core;
using SS.Utilities;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Enter
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
        public NameInlineArray Name;
        public SquadInlineArray Squad;
        private short ship;
        private short freq;

        public Enter(ServerTick ticks, short playerId, ReadOnlySpan<char> name, ReadOnlySpan<char> squad, ShipType ship, short freq)
        {
            Header = new EventHeader(ticks, EventType.Enter);
            this.playerId = LittleEndianConverter.Convert(playerId);
			this.ship = LittleEndianConverter.Convert((short)ship);
            this.freq = LittleEndianConverter.Convert(freq);
			Name.Set(name);
			Squad.Set(squad);
		}

        #region Helper properties

        public short PlayerId
        {
            get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
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

		#region Inline Array Types

		[InlineArray(Length)]
		public struct NameInlineArray
		{
			public const int Length = 24;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public void Set(ReadOnlySpan<char> value)
			{
				StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length), false);
			}

			public void Clear()
			{
				((Span<byte>)this).Clear();
			}
		}

		[InlineArray(Length)]
		public struct SquadInlineArray
		{
			public const int Length = 24;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public void Set(ReadOnlySpan<char> value)
			{
				StringUtils.WriteNullPaddedString(this, value.TruncateForEncodedByteLimit(Length), false);
			}

			public void Clear()
			{
				((Span<byte>)this).Clear();
			}
		}

		#endregion
	}
}
