using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct FileHeader
    {
        #region Static members

        public static readonly int Length;

        static FileHeader()
        {
            Length = Marshal.SizeOf(typeof(FileHeader));
        }

        #endregion

        private fixed byte headerBytes[HeaderBytesLength]; // or intead maybe use: private ulong header;
        private uint version;
        private uint offset;
        private uint events;
        private uint endTime;
        private uint maxPlayerId;
        private uint specFreq;
        private long recorded;
        private uint mapChecksum;
        private fixed byte recorderBytes[RecorderBytesLength];
        private fixed byte arenaNameBytes[ArenaNameBytesLength];

        #region Helper properties

        private const int HeaderBytesLength = 8;
        public Span<byte> HeaderBytes => MemoryMarshal.CreateSpan(ref headerBytes[0], HeaderBytesLength);

        /// <summary>
        /// Always "asssgame".
        /// </summary>
        public ReadOnlySpan<char> Header
        {
            set => StringUtils.WriteNullPaddedString(HeaderBytes, value, false);
        }

        /// <summary>
        /// To tell if the file is compatible.
        /// </summary>
        public uint Version
        {
            get => LittleEndianConverter.Convert(version);
            set => version = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// Offset (bytes) of the start of events from the beginning of the file.
        /// </summary>
        public uint Offset
        {
            get => LittleEndianConverter.Convert(offset);
            set => offset = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// # of events in the file.
        /// </summary>
        public uint Events
        {
            get => LittleEndianConverter.Convert(events);
            set => events = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// Ending time of the recording.
        /// </summary>
        public uint EndTime
        {
            get => LittleEndianConverter.Convert(endTime);
            set => endTime = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// The highest numbered pid in the file.
        /// </summary>
        public uint MaxPlayerId
        {
            get => LittleEndianConverter.Convert(maxPlayerId);
            set => maxPlayerId = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// The spectator freq at the time the game was recorded.
        /// </summary>
        public uint SpecFreq
        {
            get => LittleEndianConverter.Convert(specFreq);
            set => specFreq = LittleEndianConverter.Convert(value);
        }

        /// <summary>
        /// Timestamp that the game was recorded.
        /// </summary>
        public DateTimeOffset Recorded
        {
            get => DateTimeOffset.FromUnixTimeSeconds(recorded);
            set => recorded = value.ToUnixTimeSeconds();
        }

        /// <summary>
        /// Checksum of the map the recording was on.
        /// </summary>
        public uint MapChecksum
        {
            get => LittleEndianConverter.Convert(mapChecksum);
            set => mapChecksum = LittleEndianConverter.Convert(value);
        }

        private const int RecorderBytesLength = 24;
        public Span<byte> RecorderBytes => MemoryMarshal.CreateSpan(ref recorderBytes[0], RecorderBytesLength);

        /// <summary>
        /// The name of the player who recorded it.
        /// </summary>
        public ReadOnlySpan<char> Recorder
        {
            set => StringUtils.WriteNullPaddedString(RecorderBytes, value, false);
        }

        private const int ArenaNameBytesLength = 24;
        public Span<byte> ArenaNameBytes => MemoryMarshal.CreateSpan(ref arenaNameBytes[0], ArenaNameBytesLength);

        /// <summary>
        /// The name of the arena that was recorded.
        /// </summary>
        public ReadOnlySpan<char> ArenaName
        {
            set => StringUtils.WriteNullPaddedString(ArenaNameBytes, value, false);
        }

        #endregion
    }
}
