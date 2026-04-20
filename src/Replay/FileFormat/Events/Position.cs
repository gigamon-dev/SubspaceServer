using SS.Packets.Game;
using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    /// <summary>
    /// Event representing a C2S position packet without extra position data.
    /// </summary>
    /// <remarks>
    /// This is for backwards compatibility with the original format used by the ASSS record module.
    /// In it, <see cref="C2S_PositionPacket.Time"/> (client time) is overwritten with the Player ID.
    /// </remarks>
    /// <param name="ticks"></param>
    /// <param name="c2sPosition"></param>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Position(ServerTick ticks, ref readonly C2S_PositionPacket c2sPosition)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<Position>();

        #endregion

        public EventHeader Header = new(ticks, EventType.Position);
        public C2S_PositionPacket PositionPacket = c2sPosition;
    }

    /// <summary>
    /// Event representing a C2S position packet with extra position data
    /// </summary>
    /// <remarks>
    /// This is for backwards compatibility with the original format used by the ASSS record module.
    /// In it, <see cref="C2S_PositionPacket.Time"/> (client time) is overwritten with the Player ID.
    /// </remarks>
    /// <param name="ticks"></param>
    /// <param name="c2sPosition"></param>
    /// <param name="extra"></param>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PositionWithExtra(ServerTick ticks, ref readonly C2S_PositionPacket c2sPosition, ref readonly ExtraPositionData extra)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<PositionWithExtra>();

        #endregion

        public EventHeader Header = new(ticks, EventType.Position);
        public C2S_PositionPacket PositionPacket = c2sPosition;
        public ExtraPositionData ExtraPositionData = extra;
    }


    /// <summary>
    /// Event that wraps a C2S position packet without extra position data.
    /// </summary>
    /// <remarks>
    /// This has a separate field for <see cref="PlayerId"/>, which allows the <see cref="C2S_PositionPacket.Time"/> (client time) to be used.
    /// This makes it more accurate than how the ASSS record module records position packets.
    /// </remarks>
    /// <param name="ticks"></param>
    /// <param name="playerId"></param>
    /// <param name="c2sPosition"></param>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PositionWrapper(ServerTick ticks, short playerId, ref readonly C2S_PositionPacket c2sPosition)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<PositionWrapper>();

        #endregion

        public EventHeader Header = new(ticks, EventType.PositionWrapper);
        private short playerId = LittleEndianConverter.Convert(playerId);
        public C2S_PositionPacket PositionPacket = c2sPosition;

        #region Helper properties

        public short PlayerId
        {
            readonly get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        #endregion
    }

    /// <summary>
    /// Event that wraps a C2S position packet with extra position data
    /// </summary>
    /// <remarks>
    /// This has a separate field for <see cref="PlayerId"/>, which allows the <see cref="C2S_PositionPacket.Time"/> (client time) to be used.
    /// This makes it more accurate than how the ASSS record module records position packets.
    /// </remarks>
    /// <param name="ticks"></param>
    /// <param name="playerId"></param>
    /// <param name="c2sPosition"></param>
    /// <param name="extra"></param>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PositionWithExtraWrapper(ServerTick ticks, short playerId, ref readonly C2S_PositionPacket c2sPosition, ref readonly ExtraPositionData extra)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<PositionWithExtraWrapper>();

        #endregion

        public EventHeader Header = new(ticks, EventType.PositionWithExtraWrapper);
        private short playerId = LittleEndianConverter.Convert(playerId);
        public C2S_PositionPacket PositionPacket = c2sPosition;
        public ExtraPositionData ExtraPositionData = extra;

        #region Helper properties

        public short PlayerId
        {
            readonly get => LittleEndianConverter.Convert(playerId);
            set => playerId = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
