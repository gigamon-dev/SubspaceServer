using SS.Core;
using SS.Utilities;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Enter(ServerTick ticks, short playerId, ReadOnlySpan<char> name, ReadOnlySpan<char> squad, ShipType ship, short freq)
	{
        #region Static members

        public static readonly int Length = Marshal.SizeOf<Enter>();

        #endregion

        public EventHeader Header = new(ticks, EventType.Enter);
        private short playerId = LittleEndianConverter.Convert(playerId);
        public NameInlineArray Name = new(name);
        public SquadInlineArray Squad = new(squad);
        private short ship = LittleEndianConverter.Convert((short)ship);
        private short freq = LittleEndianConverter.Convert(freq);

		#region Helper properties

		public short PlayerId
		{
			readonly get => LittleEndianConverter.Convert(playerId);
			set => playerId = LittleEndianConverter.Convert(value);
		}

		public ShipType Ship
		{
			readonly get => (ShipType)LittleEndianConverter.Convert(ship);
			set => ship = LittleEndianConverter.Convert((short)value);
		}

		public short Freq
		{
			readonly get => LittleEndianConverter.Convert(freq);
			set => freq = LittleEndianConverter.Convert(value);
		}

		#endregion

		#region Inline Array Types

		[InlineArray(Length)]
		public struct NameInlineArray
		{
			public const int Length = 24;

			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public NameInlineArray(ReadOnlySpan<char> value)
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

			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public SquadInlineArray(ReadOnlySpan<char> value)
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
