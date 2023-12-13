using SS.Utilities;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Packets.Game
{
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	public readonly struct C2S_GoArena
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
		private readonly ArenaName arenaName;
		public readonly byte OptionalGraphics; // continuum

		public C2S_GoArena(byte shipType, sbyte obscenityFilter, sbyte wavMsg, short xRes, short yRes, short arenaType, ReadOnlySpan<char> arenaName, byte optionalGraphics) : this()
		{
			Type = (byte)C2SPacketType.GotoArena;
			ShipType = shipType;
			ObscenityFilter = obscenityFilter;
			WavMsg = wavMsg;
			this.xRes = LittleEndianConverter.Convert(xRes);
			this.yRes = LittleEndianConverter.Convert(yRes);
			this.arenaType = LittleEndianConverter.Convert(arenaType);
			StringUtils.WriteNullPaddedString(this.arenaName, arenaName.TruncateForEncodedByteLimit(ArenaName.Length - 1));
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

		/// <summary>
		/// Converts the arena name from encoded bytes to buffer of characters.
		/// </summary>
		/// <param name="destination">The buffer to write characters to.</param>
		/// <returns>The number of characters written to <paramref name="destination"/>.</returns>
		public readonly int GetArenaName(Span<char> destination)
		{
			ReadOnlySpan<byte> bytes = StringUtils.SliceNullTerminated((ReadOnlySpan<byte>)arenaName);
			return StringUtils.DefaultEncoding.GetChars(bytes, destination);
		}

		#endregion

		[InlineArray(Length)]
		private struct ArenaName
		{
			public const int Length = 16;

			[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Inline array")]
			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;
		}
	}
}
