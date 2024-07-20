using SS.Utilities;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FileHeader
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<FileHeader>();

		#endregion

		/// <summary>
		/// Always "asssgame".
		/// </summary>
		public HeaderInlineArray Header;

        private uint version;
        private uint offset;
        private uint events;
        private uint endTime;
        private uint maxPlayerId;
        private uint specFreq;
        private long recorded;
        private uint mapChecksum;

		/// <summary>
		/// The name of the player who recorded it.
		/// </summary>
		public RecorderInlineArray Recorder;

		/// <summary>
		/// The name of the arena that the replay was recorded in.
		/// </summary>
		public ArenaNameInlineArray ArenaName;

        #region Helper properties

        /// <summary>
        /// To tell if the file is compatible.
        /// </summary>
        public uint Version
        {
            readonly get => LittleEndianConverter.Convert(version);
            set => version = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// Offset (bytes) of the start of events from the beginning of the file.
        /// </summary>
        public uint Offset
        {
			readonly get => LittleEndianConverter.Convert(offset);
            set => offset = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// # of events in the file.
        /// </summary>
        public uint Events
        {
			readonly get => LittleEndianConverter.Convert(events);
            set => events = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// Ending time of the recording.
        /// </summary>
        public uint EndTime
        {
			readonly get => LittleEndianConverter.Convert(endTime);
            set => endTime = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// The highest numbered pid in the file.
        /// </summary>
        public uint MaxPlayerId
        {
			readonly get => LittleEndianConverter.Convert(maxPlayerId);
            set => maxPlayerId = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// The spectator freq at the time the game was recorded.
        /// </summary>
        public uint SpecFreq
        {
			readonly get => LittleEndianConverter.Convert(specFreq);
            set => specFreq = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// Timestamp that the game was recorded.
        /// </summary>
        public DateTimeOffset Recorded
        {
			readonly get => DateTimeOffset.FromUnixTimeSeconds(recorded);
            set => recorded = value.ToUnixTimeSeconds();
        }

        /// <summary>
        /// Checksum of the map the recording was on.
        /// </summary>
        public uint MapChecksum
        {
			readonly get => LittleEndianConverter.Convert(mapChecksum);
            set => mapChecksum = LittleEndianConverter.Convert(value);
        }

		#endregion

		#region Inline Array Types

		[InlineArray(Length)]
		public struct HeaderInlineArray
		{
			public const int Length = 8;

			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public HeaderInlineArray(ReadOnlySpan<char> name)
			{
				Set(name);
			}

			public static implicit operator HeaderInlineArray(string value)
			{
				return new(value);
			}

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
		public struct RecorderInlineArray
		{
			public const int Length = 24;

			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public RecorderInlineArray(ReadOnlySpan<char> name)
			{
				Set(name);
			}

			public static implicit operator RecorderInlineArray(string value)
			{
				return new(value);
			}

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
		public struct ArenaNameInlineArray
		{
			public const int Length = 24;

			[SuppressMessage("CodeQuality", "IDE0051:Remove unused private members", Justification = "Inline array")]
			private byte _element0;

			public ArenaNameInlineArray(ReadOnlySpan<char> name)
			{
				Set(name);
			}

			public static implicit operator ArenaNameInlineArray(string value)
			{
				return new(value);
			}

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
