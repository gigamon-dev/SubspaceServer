using SS.Utilities;
using System.Runtime.InteropServices;

namespace SS.Replay.FileFormat.Events
{
    /// <summary>
    /// A full ownership update (all flags). 
    /// Normally, at the start of a recording.
    /// Or, if the flag game is reset mid-recording.
    /// </summary>
    /// <remarks>
    /// This event is a variable length, based on the flag count.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct StaticFlagFullUpdate(ServerTick ticks, short flagCount)
    {
        #region Static members

        public static readonly int Length = Marshal.SizeOf<StaticFlagFullUpdate>();

        #endregion

        public EventHeader Header = new(ticks, EventType.StaticFlagFullUpdate);
        private short flagCount = LittleEndianConverter.Convert(flagCount);
        // Followed by an array of short with the length of flagCount which contains the owner freq.

        #region Helper properties

        public short FlagCount
        {
            readonly get => LittleEndianConverter.Convert(flagCount);
            set => flagCount = LittleEndianConverter.Convert(value);
        }

        #endregion
    }
}
